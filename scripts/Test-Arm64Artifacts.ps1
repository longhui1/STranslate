param(
    [string]$AppDirectory,
    [string]$ReleaseDirectory,
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Reflection.Metadata
if (-not $AppDirectory -and -not $ReleaseDirectory) { throw '必须指定 AppDirectory 或 ReleaseDirectory。' }

function Get-PeImageInfo([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $headers = $pe.PEHeaders
        $machine = [int]$headers.CoffHeader.Machine
        $clrFlags = if ($null -eq $headers.CorHeader) { 0 } else { [int]$headers.CorHeader.Flags }
        $anyCpu = $machine -eq 0x14c -and ($clrFlags -band 1) -ne 0 -and ($clrFlags -band 0x20002) -eq 0
        $imports = @()
        $directory = $headers.PEHeader.ImportTableDirectory
        if ($directory.RelativeVirtualAddress -ne 0) {
            $reader = $pe.GetSectionData($directory.RelativeVirtualAddress).GetReader()
            while ($reader.RemainingBytes -ge 20) {
                $lookup = $reader.ReadUInt32()
                $timestamp = $reader.ReadUInt32()
                $forwarder = $reader.ReadUInt32()
                $nameRva = $reader.ReadUInt32()
                $address = $reader.ReadUInt32()
                if (($lookup -bor $timestamp -bor $forwarder -bor $nameRva -bor $address) -eq 0) { break }
                $nameReader = $pe.GetSectionData([int]$nameRva).GetReader()
                $nameBytes = [Collections.Generic.List[byte]]::new()
                while ($nameReader.RemainingBytes -gt 0) {
                    $value = $nameReader.ReadByte()
                    if ($value -eq 0) { break }
                    $nameBytes.Add($value)
                }
                $imports += [Text.Encoding]::ASCII.GetString($nameBytes.ToArray())
            }
        }
        return [pscustomobject]@{ Machine = $machine; AnyCpu = $anyCpu; Imports = $imports }
    }
    finally { $pe.Dispose(); $stream.Dispose() }
}

function Assert-NativeArm64([string]$Path, [switch]$RequireStaticCrt) {
    if (-not (Test-Path $Path)) { throw "缺少必须的 ARM64 文件：$Path" }
    $info = Get-PeImageInfo $Path
    if ($info.Machine -ne 0xaa64) { throw "必须是原生 ARM64 PE：$Path（Machine=0x$($info.Machine.ToString('x4'))）" }
    if ($RequireStaticCrt -and @($info.Imports | Where-Object { $_ -match '^(VCRUNTIME|MSVCP)\d' }).Count -ne 0) {
        throw "原生组件仍依赖外部 VC++ runtime：$Path；请使用 crt-static 构建。"
    }
    return $info
}

function Assert-PaddleOcr([string]$Directory) {
    $pluginName = 'STranslate.Plugin.Ocr.PaddleV6Arm64'
    $pluginDirectory = Join-Path $Directory "Plugins/$pluginName"
    foreach ($name in @('plugin.json', "$pluginName.dll", "$pluginName.deps.json", 'model-manifest.json', 'RapidOcrNet.dll', 'Microsoft.ML.OnnxRuntime.dll', 'SkiaSharp.dll', 'LICENSE-RapidOcrNet.txt', 'NOTICE.txt')) {
        if (-not (Test-Path (Join-Path $pluginDirectory $name) -PathType Leaf)) { throw "内置 ARM64 OCR 资源缺失：$name" }
    }
    $metadata = Get-Content (Join-Path $pluginDirectory 'plugin.json') -Raw | ConvertFrom-Json
    if ($metadata.PluginID -ne 'c67c0e3de45b48f6a852ffa8f0aae2f2' -or $metadata.ExecuteFileName -ne "$pluginName.dll") {
        throw '内置 ARM64 OCR 插件身份错误。'
    }
    $modelFiles = @(Get-ChildItem $pluginDirectory -Recurse -File |
        Where-Object { $_.Extension -in @('.onnx', '.ort') -or $_.Name -match '^(ppocr.*(dict|keys).*\.txt|.*_dict\.txt)$' })
    $modelDirectories = @(Get-ChildItem $pluginDirectory -Recurse -Directory | Where-Object Name -Match '^models?$')
    if ($modelFiles.Count -ne 0 -or $modelDirectories.Count -ne 0) { throw 'OCR 模型必须在线下载，发布包不能包含模型或字符字典。' }

    $manifestPath = Join-Path $pluginDirectory 'model-manifest.json'
    $sourceManifest = Join-Path $PSScriptRoot "../src/Arm64/Plugins/$pluginName/model-manifest.json"
    $manifestHash = (Get-FileHash $manifestPath -Algorithm SHA256).Hash
    if ($manifestHash -ne (Get-FileHash $sourceManifest -Algorithm SHA256).Hash) { throw '打包的 OCR 模型清单与固定源码不一致。' }
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.family -ne 'PP-OCRv6-small' -or $manifest.license -ne 'Apache-2.0' -or
        $manifest.provenance.rapidOcrNetVersion -ne '4.2.0' -or $manifest.provenance.modelMirrorRepository -ne 'BobLd/RapidOcrNet' -or
        $manifest.provenance.rapidOcrNetSourceCommit -notmatch '^[0-9a-f]{40}$' -or $manifest.provenance.modelMirrorCommit -notmatch '^[0-9a-f]{40}$' -or
        @($manifest.files).Count -ne 4) { throw 'OCR 在线模型清单的 schema、Small 模型身份或固定来源错误。' }
    $fileNames = @{
        det = 'PP-OCRv6_det_small.onnx'; cls = 'ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx'
        rec = 'PP-OCRv6_rec_small.onnx'; dict = 'ppocrv6_dict.txt'
    }
    foreach ($role in @('det', 'cls', 'rec', 'dict')) {
        $entry = @($manifest.files | Where-Object role -EQ $role)
        if ($entry.Count -ne 1 -or $entry[0].fileName -ne $fileNames[$role] -or $entry[0].size -le 0 -or
            $entry[0].sha256 -notmatch '^[0-9a-f]{64}$' -or @($entry[0].urls).Count -lt 1) { throw "模型清单缺少固定文件、大小或 SHA-256：$role" }
        foreach ($url in $entry[0].urls) {
            $uri = [uri]$url
            if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.UserInfo) { throw "模型下载 URL 非法：$url" }
        }
        $mirrorPrefix = "https://raw.githubusercontent.com/BobLd/RapidOcrNet/$($manifest.provenance.modelMirrorCommit)/"
        if (@($entry[0].urls | Where-Object { $_.StartsWith($mirrorPrefix, [StringComparison]::Ordinal) }).Count -eq 0) {
            throw "模型清单缺少固定 commit 的下载后备地址：$role"
        }
    }
    $deps = Get-Content (Join-Path $pluginDirectory "$pluginName.deps.json") -Raw | ConvertFrom-Json
    $packageNames = @($deps.libraries.PSObject.Properties.Name)
    foreach ($required in @('RapidOcrNet/4.2.0', 'Microsoft.ML.OnnxRuntime/1.29.0', 'Microsoft.ML.OnnxRuntime.Managed/1.29.0', 'SkiaSharp/3.119.1', 'SkiaSharp.NativeAssets.Win32/3.119.1')) {
        if ($required -notin $packageNames) { throw "OCR 依赖版本错误：$required" }
    }

    $report = Get-Content (Join-Path $Directory 'arm64-ocr-provenance.json') -Raw | ConvertFrom-Json
    if ($report.SchemaVersion -ne 1 -or $report.Plugin -ne $pluginName -or $report.Backend.RapidOcrNet -ne '4.2.0' -or
        $report.Backend.OnnxRuntime -ne '1.29.0' -or $report.Backend.SkiaSharp -ne '3.119.1' -or
        $report.Backend.Provider -ne 'CPU' -or $report.Backend.Architecture -ne 'win-arm64' -or
        $report.Models.Policy -ne 'download-on-demand' -or $report.Models.ManifestFile -ne "Plugins/$pluginName/model-manifest.json" -or
        $report.Models.ManifestSHA256 -ne $manifestHash -or $report.Models.Family -ne $manifest.family -or $report.Models.License -ne $manifest.license -or
        $report.Models.Provenance.modelMirrorCommit -ne $manifest.provenance.modelMirrorCommit -or
        (($report.Models.Files | ConvertTo-Json -Depth 10 -Compress) -ne ($manifest.files | ConvertTo-Json -Depth 10 -Compress))) {
        throw 'OCR provenance 的后端、架构或在线模型清单身份错误。'
    }
    $expectedNative = @('onnxruntime.dll', 'onnxruntime_providers_shared.dll', 'libSkiaSharp.dll') |
        ForEach-Object { "Plugins/$pluginName/$_" }
    if (@($report.NativeFiles).Count -ne $expectedNative.Count -or
        (@($report.NativeFiles.FileName | Sort-Object) -join '|') -ne (@($expectedNative | Sort-Object) -join '|')) {
        throw 'OCR provenance 缺少必须的 ARM64 native 组件。'
    }
    $crtNames = @($report.Crt.Files.FileName)
    foreach ($required in @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll')) {
        if ($required -notin $crtNames) { throw "OCR 缺少 app-local ARM64 CRT：$required" }
    }
    if ($report.Crt.Vendor -ne 'Microsoft Visual Studio' -or $report.Crt.Distribution -ne 'app-local Microsoft.VC143.CRT ARM64') {
        throw 'OCR CRT 必须来自 Visual Studio 官方 ARM64 Redist。'
    }
    foreach ($name in $crtNames) {
        if ($name -ne [IO.Path]::GetFileName($name) -or $name -notmatch '^(MSVCP|VCRUNTIME|CONCRT|VCOMP|VCCORLIB)\d.*\.dll$') {
            throw "OCR CRT 记录含非法路径或无关文件：$name"
        }
    }
    foreach ($file in @($report.NativeFiles) + @($report.Crt.Files)) {
        $path = Join-Path $Directory $file.FileName
        $info = Assert-NativeArm64 $path
        if ($file.Machine -ne 'ARM64' -or $info.AnyCpu -or (Get-Item $path).Length -ne $file.Size -or
            (Get-FileHash $path -Algorithm SHA256).Hash -ne $file.SHA256 -or
            (@($info.Imports | Sort-Object -Unique) -join '|') -ne (@($file.Imports | Sort-Object -Unique) -join '|')) {
            throw "OCR native / CRT provenance 与文件不符：$($file.FileName)"
        }
        foreach ($dependency in $info.Imports | Where-Object { $_ -match '^(MSVCP|VCRUNTIME|CONCRT|VCOMP|VCCORLIB)\d.*\.dll$' }) {
            if ($dependency -notin $crtNames) { throw "OCR ARM64 CRT 依赖不完整：$($file.FileName) -> $dependency" }
        }
    }
    $noticeNames = @('ONNXRuntime-LICENSE.txt', 'ONNXRuntime-ThirdPartyNotices.txt', 'SkiaSharp-LICENSE.txt', 'SkiaSharp-ThirdPartyNotices.txt') |
        ForEach-Object { "Plugins/$pluginName/licenses/$_" }
    if (@($report.Notices).Count -ne $noticeNames.Count -or
        (@($report.Notices.FileName | Sort-Object) -join '|') -ne (@($noticeNames | Sort-Object) -join '|')) { throw 'OCR 缺少 native 组件的许可及第三方归属通知。' }
    foreach ($notice in $report.Notices) {
        $path = Join-Path $Directory $notice.FileName
        if ((Get-Item $path).Length -le 0 -or (Get-Item $path).Length -ne $notice.Size -or
            (Get-FileHash $path -Algorithm SHA256).Hash -ne $notice.SHA256) { throw "OCR 许可文件哈希不符：$($notice.FileName)" }
    }
}

function Assert-AppTree([string]$Directory) {
    foreach ($name in @('STranslate.exe', 'z_stranslate_host.exe', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'wpfgfx_cor3.dll', 'e_sqlite3.dll')) {
        $null = Assert-NativeArm64 (Join-Path $Directory $name)
    }
    $hostInfo = Get-PeImageInfo (Join-Path $Directory 'z_stranslate_host.exe')
    if (@($hostInfo.Imports | Where-Object { $_ -match '^(VCRUNTIME|MSVCP)\d' }).Count -ne 0) {
        throw '辅助程序仍依赖外部 VC++ runtime；请使用 crt-static 构建。'
    }
    $badFiles = @()
    $peFiles = Get-ChildItem $Directory -Recurse -File | Where-Object Extension -In @('.exe', '.dll')
    foreach ($file in $peFiles) {
        $info = Get-PeImageInfo $file.FullName
        if ($info.Machine -ne 0xaa64 -and -not $info.AnyCpu) { $badFiles += $file.FullName }
    }
    if ($badFiles.Count -gt 0) { throw "ARM64 输出混入不兼容原生文件：$($badFiles -join ', ')" }
    $pluginDirectory = Join-Path $Directory 'Plugins'
    if (Test-Path (Join-Path $pluginDirectory 'STranslate.Plugin.Ocr.WeChatBuiltIn')) { throw 'ARM64 包必须排除微信 OCR。' }
    $expectedPlugins = Get-ChildItem (Join-Path $PSScriptRoot '../src/Plugins') -Recurse -Filter '*.csproj' |
        Where-Object { $_.FullName -notmatch '[\\/]ThirdPlugins[\\/]' -and $_.BaseName -ne 'STranslate.Plugin.Ocr.WeChatBuiltIn' }
    foreach ($plugin in $expectedPlugins) {
        $pluginPath = Join-Path $pluginDirectory $plugin.BaseName
        foreach ($name in @('plugin.json', "$($plugin.BaseName).dll")) {
            if (-not (Test-Path (Join-Path $pluginPath $name))) { throw "内置插件资源缺失：$pluginPath/$name" }
        }
    }
    Assert-PaddleOcr $Directory
    $actualVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Directory 'STranslate.dll')).Version
    $expectedVersion = [version]$Version.Replace('-arm64.', '.')
    if ($actualVersion -ne $expectedVersion) { throw "主程序集版本错误：需要 $Version，实际 $actualVersion。" }
    $sdkVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Directory 'STranslate.Plugin.dll')).Version
    if ($sdkVersion -ne [version]'1.0.0.0') { throw "插件 SDK 程序集标识改变：需要 1.0.0.0，实际 $sdkVersion；现有社区插件可能无法加载。" }
    Write-Host "ARM64 程序验证通过：$($peFiles.Count) 个 PE 文件，$(@($expectedPlugins).Count + 1) 个内置插件；OCR native、CRT 与在线模型清单通过。"
}

if ($AppDirectory) { Assert-AppTree $AppDirectory }
if (-not $ReleaseDirectory) { return }

$packageId = 'STranslate-ARM64'
$prefix = "$packageId-win-arm64"
$setupPath = Join-Path $ReleaseDirectory "$prefix-Setup.exe"
$null = Assert-NativeArm64 $setupPath -RequireStaticCrt
$feedPath = Join-Path $ReleaseDirectory 'releases.win-arm64.json'
$legacyPath = Join-Path $ReleaseDirectory 'RELEASES-win-arm64'
foreach ($path in @($feedPath, $legacyPath)) {
    if (-not (Test-Path $path)) { throw "缺少 OTA 更新元数据：$path" }
}
$feed = Get-Content $feedPath -Raw | ConvertFrom-Json
$fullAsset = @($feed.Assets | Where-Object { $_.Version -eq $Version -and ($_.Type -eq 'Full' -or $_.Type -eq 1) })
if ($fullAsset.Count -ne 1) { throw 'OTA feed 必须包含且仅包含一个本版本的完整更新包。' }
foreach ($asset in $feed.Assets) {
    if ($asset.PackageId -ne $packageId -or $asset.FileName -notmatch '^STranslate-ARM64-.*-win-arm64-(full|delta)\.nupkg$') {
        throw "OTA feed 混入错误安装 ID 或 channel：$($asset.FileName)"
    }
    if ($asset.FileName -ne [IO.Path]::GetFileName($asset.FileName)) { throw 'OTA 文件名含非法路径。' }
    $assetPath = Join-Path $ReleaseDirectory $asset.FileName
    if (-not (Test-Path $assetPath)) { throw "OTA feed 引用了不存在的包：$assetPath" }
    if ((Get-Item $assetPath).Length -ne $asset.Size) { throw "OTA 包长度不符：$assetPath" }
    if ((Get-FileHash $assetPath -Algorithm SHA1).Hash -ne $asset.SHA1) { throw "OTA 包 SHA1 不符：$assetPath" }
    if ($asset.SHA256 -and (Get-FileHash $assetPath -Algorithm SHA256).Hash -ne $asset.SHA256) { throw "OTA 包 SHA256 不符：$assetPath" }
}

$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("stranslate-arm64-check-" + [guid]::NewGuid().ToString('N'))
try {
    New-Item $temporaryDirectory -ItemType Directory | Out-Null
    $portablePath = Join-Path $ReleaseDirectory "$prefix-Portable.zip"
    $portableArchive = [IO.Compression.ZipFile]::OpenRead($portablePath)
    try {
        if (-not $portableArchive.GetEntry('current/PortableConfig/')) { throw '便携包缺少 PortableConfig 标识。' }
    }
    finally { $portableArchive.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($portablePath, (Join-Path $temporaryDirectory 'portable'))
    $portableDirectory = Join-Path $temporaryDirectory 'portable'
    $null = Assert-NativeArm64 (Join-Path $portableDirectory 'Update.exe') -RequireStaticCrt
    $null = Assert-NativeArm64 (Join-Path $portableDirectory 'STranslate ARM64.exe') -RequireStaticCrt
    Assert-AppTree (Join-Path $portableDirectory 'current')

    $packagePath = Join-Path $ReleaseDirectory $fullAsset[0].FileName
    [IO.Compression.ZipFile]::ExtractToDirectory($packagePath, (Join-Path $temporaryDirectory 'package'))
    $packageDirectory = Join-Path $temporaryDirectory 'package'
    $null = Assert-NativeArm64 (Join-Path $packageDirectory 'lib/app/Squirrel.exe') -RequireStaticCrt
    Assert-AppTree (Join-Path $packageDirectory 'lib/app')
    if ((Get-FileHash (Join-Path $packageDirectory 'lib/app/arm64-ocr-provenance.json') -Algorithm SHA256).Hash -ne
        (Get-FileHash (Join-Path $ReleaseDirectory 'arm64-ocr-provenance.json') -Algorithm SHA256).Hash) {
        throw '公开 OCR provenance 与实际安装包不同。'
    }
    [xml]$nuspec = Get-Content (Join-Path $packageDirectory "$packageId.nuspec") -Raw
    if ($nuspec.package.metadata.id -ne $packageId -or $nuspec.package.metadata.version -ne $Version -or $nuspec.package.metadata.rid -ne 'win-arm64' -or
        $nuspec.package.metadata.channel -ne 'win-arm64' -or $nuspec.package.metadata.machineArchitecture -ne 'arm64') {
        throw '安装包 nuspec 的安装 ID、版本、channel 或目标架构错误。'
    }
    Write-Host '原生 ARM64 Setup、更新组件、Portable、完整包及 OTA 元数据验证通过。' -ForegroundColor Green
}
finally { if (Test-Path $temporaryDirectory) { Remove-Item $temporaryDirectory -Recurse -Force } }
