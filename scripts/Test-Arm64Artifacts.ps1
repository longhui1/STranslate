param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [string]$ReleaseDirectory,
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Reflection.Metadata

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
    $actualVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Directory 'STranslate.dll')).Version
    $expectedVersion = [version]$Version.Replace('-arm64.', '.')
    if ($actualVersion -ne $expectedVersion) { throw "主程序集版本错误：需要 $Version，实际 $actualVersion。" }
    $sdkVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Directory 'STranslate.Plugin.dll')).Version
    if ($sdkVersion -ne [version]'1.0.0.0') { throw "插件 SDK 程序集标识改变：需要 1.0.0.0，实际 $sdkVersion；现有社区插件可能无法加载。" }
    Write-Host "ARM64 程序验证通过：$($peFiles.Count) 个 PE 文件，$(@($expectedPlugins).Count) 个内置插件。"
}

Assert-AppTree $AppDirectory
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
    [xml]$nuspec = Get-Content (Join-Path $packageDirectory "$packageId.nuspec") -Raw
    if ($nuspec.package.metadata.id -ne $packageId -or $nuspec.package.metadata.version -ne $Version -or $nuspec.package.metadata.rid -ne 'win-arm64' -or
        $nuspec.package.metadata.channel -ne 'win-arm64' -or $nuspec.package.metadata.machineArchitecture -ne 'arm64') {
        throw '安装包 nuspec 的安装 ID、版本、channel 或目标架构错误。'
    }
    Write-Host '原生 ARM64 Setup、更新组件、Portable、完整包及 OTA 元数据验证通过。' -ForegroundColor Green
}
finally { if (Test-Path $temporaryDirectory) { Remove-Item $temporaryDirectory -Recurse -Force } }
