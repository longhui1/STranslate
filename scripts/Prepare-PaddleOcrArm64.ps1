param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [string]$CrtDirectory,
    [string]$NuGetPackagesDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Reflection.Metadata
$AppDirectory = [IO.Path]::GetFullPath($AppDirectory)
$pluginName = 'STranslate.Plugin.Ocr.PaddleV6Arm64'
$pluginDirectory = Join-Path $AppDirectory "Plugins/$pluginName"
if (-not (Test-Path $pluginDirectory -PathType Container)) { throw '请先发布 ARM64 PaddleOCR 插件。' }

function Get-NativeInfo([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $headers = $pe.PEHeaders
        $machine = [int]$headers.CoffHeader.Machine
        $isManaged = $null -ne $headers.CorHeader
        if ($machine -ne 0xaa64 -or $isManaged) {
            throw "OCR 原生依赖必须是 ARM64 native DLL：$Path（Machine=0x$($machine.ToString('x4'))，Managed=$isManaged）"
        }
        $imports = @()
        $directory = $headers.PEHeader.ImportTableDirectory
        if ($directory.RelativeVirtualAddress -ne 0) {
            $reader = $pe.GetSectionData($directory.RelativeVirtualAddress).GetReader()
            while ($reader.RemainingBytes -ge 20) {
                $lookup = $reader.ReadUInt32(); $timestamp = $reader.ReadUInt32(); $forwarder = $reader.ReadUInt32()
                $nameRva = $reader.ReadUInt32(); $address = $reader.ReadUInt32()
                if (($lookup -bor $timestamp -bor $forwarder -bor $nameRva -bor $address) -eq 0) { break }
                $nameReader = $pe.GetSectionData([int]$nameRva).GetReader()
                $bytes = [Collections.Generic.List[byte]]::new()
                while ($nameReader.RemainingBytes -gt 0) {
                    $value = $nameReader.ReadByte()
                    if ($value -eq 0) { break }
                    $bytes.Add($value)
                }
                $imports += [Text.Encoding]::ASCII.GetString($bytes.ToArray())
            }
        }
        return [ordered]@{
            FileName = [IO.Path]::GetRelativePath($AppDirectory, $Path).Replace('\', '/')
            Size = (Get-Item $Path).Length
            SHA256 = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
            Machine = 'ARM64'
            FileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion
            Imports = @($imports | Sort-Object -Unique)
        }
    }
    finally { $pe.Dispose(); $stream.Dispose() }
}

# RapidOcrNet 的 NuGet targets 可能复制默认 v5 模型。用户选择在线下载，
# staging 只保留模型清单，任何模型或字典都不能进入 Setup / Portable / OTA。
Get-ChildItem $pluginDirectory -Directory -Recurse |
    Where-Object Name -Match '^models?$' | Sort-Object { $_.FullName.Length } -Descending |
    Remove-Item -Recurse -Force
Get-ChildItem $pluginDirectory -File -Recurse |
    Where-Object { $_.Extension -in @('.onnx', '.ort') -or $_.Name -match '^(ppocr.*(dict|keys).*\.txt|.*_dict\.txt)$' } |
    Remove-Item -Force
Get-ChildItem $pluginDirectory -File -Recurse | Where-Object Extension -In @('.lib', '.exp', '.pdb') | Remove-Item -Force

$nativeFiles = @()
foreach ($name in @('onnxruntime.dll', 'onnxruntime_providers_shared.dll', 'libSkiaSharp.dll')) {
    $path = Join-Path $pluginDirectory $name
    if (-not (Test-Path $path -PathType Leaf)) { throw "缺少 OCR native 资产：$name" }
    $nativeFiles += Get-NativeInfo $path
}

# 只取 Visual Studio 官方 ARM64 Redistributable 文件夹；不要求设备预装 VC++ runtime。
if (-not $CrtDirectory) {
    if (-not $IsWindows) { throw '自动寻找 VC++ ARM64 Redist 需要 Windows Visual Studio；离线验证可显式指定 CrtDirectory。' }
    $candidates = @()
    if ($env:VCToolsRedistDir) { $candidates += Join-Path $env:VCToolsRedistDir 'arm64/Microsoft.VC143.CRT' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path $vswhere) {
        $installations = @(& $vswhere -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
        if ($LASTEXITCODE -ne 0) { throw 'vswhere 查询 Visual Studio ARM64 Redist 失败。' }
        foreach ($installation in $installations) {
            $redistRoot = Join-Path $installation 'VC/Redist/MSVC'
            if (Test-Path $redistRoot) {
                $candidates += Get-ChildItem $redistRoot -Directory |
                    Where-Object { $parsedVersion = $null; [version]::TryParse($_.Name, [ref]$parsedVersion) } |
                    Sort-Object { [version]$_.Name } -Descending |
                    ForEach-Object { Join-Path $_.FullName 'arm64/Microsoft.VC143.CRT' }
                $candidates += Join-Path $redistRoot 'v143/arm64/Microsoft.VC143.CRT'
            }
        }
    }
    $CrtDirectory = $candidates | Where-Object { Test-Path $_ -PathType Container } | Select-Object -First 1
    if (-not $CrtDirectory) { throw '未找到 Visual Studio 官方 ARM64 Microsoft.VC143.CRT；请安装 ARM64 MSVC 工具和 Redist 组件。' }
}
$CrtDirectory = [IO.Path]::GetFullPath($CrtDirectory)
$crtSources = @(Get-ChildItem $CrtDirectory -File -Filter '*.dll' | Sort-Object Name)
foreach ($required in @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll')) {
    if ($required -notin $crtSources.Name) { throw "ARM64 Redist 文件夹缺少 $required。" }
}
$crtSourcesByName = @{}
foreach ($source in $crtSources) { $crtSourcesByName[$source.Name] = $source }
$crtDependencyPattern = '^(MSVCP|VCRUNTIME|CONCRT|VCOMP|VCCORLIB)\d.*\.dll$'
$pendingCrt = [Collections.Generic.Queue[string]]::new()
foreach ($file in $nativeFiles) {
    foreach ($dependency in $file.Imports | Where-Object { $_ -match $crtDependencyPattern }) {
        $pendingCrt.Enqueue($dependency)
    }
}
$selectedCrt = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$crtFiles = @()
# 官方 arm64 Redist 也可能附带供其他 ABI 使用的兼容组件。只部署实际原生
# 推理组件所需的递归 import 闭包，不把无关的 ARM64EC / x64 文件带进应用。
while ($pendingCrt.Count -gt 0) {
    $dependency = $pendingCrt.Dequeue()
    if (-not $selectedCrt.Add($dependency)) { continue }
    if (-not $crtSourcesByName.ContainsKey($dependency)) { throw "OCR app-local CRT 依赖未闭合：$dependency" }
    $source = $crtSourcesByName[$dependency]
    $info = Get-NativeInfo $source.FullName
    $destination = Join-Path $AppDirectory $source.Name
    Copy-Item $source.FullName $destination -Force
    $crtFiles += Get-NativeInfo $destination
    Write-Host "OCR app-local CRT：$($source.Name)，Machine=0xaa64，Version=$($info.FileVersion)"
    foreach ($nestedDependency in $info.Imports | Where-Object { $_ -match $crtDependencyPattern }) {
        $pendingCrt.Enqueue($nestedDependency)
    }
}

# 检查 ORT 及 CRT 的依赖闭包；UCRT / Windows API DLL 由 Windows 本身提供。
foreach ($file in @($nativeFiles) + @($crtFiles)) {
    foreach ($dependency in $file.Imports | Where-Object { $_ -match $crtDependencyPattern }) {
        if (-not $selectedCrt.Contains($dependency)) { throw "OCR app-local CRT 依赖未闭合：$($file.FileName) -> $dependency" }
    }
}

$depsPath = Join-Path $pluginDirectory "$pluginName.deps.json"
$deps = Get-Content $depsPath -Raw | ConvertFrom-Json
$packageNames = @($deps.libraries.PSObject.Properties.Name)
foreach ($required in @('RapidOcrNet/4.2.0', 'Microsoft.ML.OnnxRuntime/1.29.0', 'Microsoft.ML.OnnxRuntime.Managed/1.29.0', 'SkiaSharp/3.119.1', 'SkiaSharp.NativeAssets.Win32/3.119.1')) {
    if ($required -notin $packageNames) { throw "OCR 发布依赖与固定版本不符：$required" }
}
$manifestPath = Join-Path $pluginDirectory 'model-manifest.json'
if (-not (Test-Path $manifestPath -PathType Leaf)) { throw 'OCR 发布缺少在线模型清单。' }
$modelManifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

# NuGet 不会自动将许可文件复制到 publish；保留原包的许可及第三方通知。
if (-not $NuGetPackagesDirectory) {
    if ($env:NUGET_PACKAGES) { $NuGetPackagesDirectory = $env:NUGET_PACKAGES }
    else {
        $packageLocation = @(& dotnet nuget locals global-packages --list)
        if ($LASTEXITCODE -ne 0 -or $packageLocation.Count -ne 1) { throw '无法查询 NuGet global-packages 目录。' }
        $NuGetPackagesDirectory = $packageLocation[0] -replace '^global-packages:\s*', ''
    }
}
$noticesDirectory = Join-Path $pluginDirectory 'licenses'
New-Item $noticesDirectory -ItemType Directory -Force | Out-Null
$notices = @()
foreach ($entry in @(
    @{ Package = 'microsoft.ml.onnxruntime/1.29.0'; File = 'LICENSE'; Name = 'ONNXRuntime-LICENSE.txt' },
    @{ Package = 'microsoft.ml.onnxruntime/1.29.0'; File = 'ThirdPartyNotices.txt'; Name = 'ONNXRuntime-ThirdPartyNotices.txt' },
    @{ Package = 'skiasharp.nativeassets.win32/3.119.1'; File = 'LICENSE.txt'; Name = 'SkiaSharp-LICENSE.txt' },
    @{ Package = 'skiasharp.nativeassets.win32/3.119.1'; File = 'THIRD-PARTY-NOTICES.txt'; Name = 'SkiaSharp-ThirdPartyNotices.txt' }
)) {
    $source = Join-Path $NuGetPackagesDirectory "$($entry.Package)/$($entry.File)"
    if (-not (Test-Path $source -PathType Leaf)) { throw "NuGet 包缺少许可或归属通知：$source" }
    $destination = Join-Path $noticesDirectory $entry.Name
    Copy-Item $source $destination -Force
    $notices += [ordered]@{
        FileName = "Plugins/$pluginName/licenses/$($entry.Name)"
        Size = (Get-Item $destination).Length
        SHA256 = (Get-FileHash $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$report = [ordered]@{
    SchemaVersion = 1
    Plugin = $pluginName
    Backend = [ordered]@{ RapidOcrNet = '4.2.0'; OnnxRuntime = '1.29.0'; SkiaSharp = '3.119.1'; Provider = 'CPU'; Architecture = 'win-arm64' }
    Models = [ordered]@{
        Policy = 'download-on-demand'
        ManifestFile = "Plugins/$pluginName/model-manifest.json"
        ManifestSHA256 = (Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Family = $modelManifest.family
        License = $modelManifest.license
        Provenance = $modelManifest.provenance
        Files = $modelManifest.files
    }
    NativeFiles = $nativeFiles
    Notices = $notices
    Crt = [ordered]@{
        Vendor = 'Microsoft Visual Studio'
        Distribution = 'app-local Microsoft.VC143.CRT ARM64'
        Selection = 'native-import-closure'
        RedistVersion = [IO.Directory]::GetParent([IO.Directory]::GetParent($CrtDirectory).FullName).Name
        LicenseReference = 'https://learn.microsoft.com/visualstudio/releases/2022/redistribution'
        Files = $crtFiles
    }
}
$report | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $AppDirectory 'arm64-ocr-provenance.json') -Encoding utf8
Write-Host 'ARM64 OCR native 依赖及 app-local CRT 已准备；模型保持在线下载。'
