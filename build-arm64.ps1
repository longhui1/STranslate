param(
    [string]$Version = '2.0.10-arm64.1',
    [string]$OutputDirectory = 'publish/arm64',
    [switch]$DownloadPrevious
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw '完整 ARM64 构建需要 Windows、.NET 10 SDK、Rust 和 ARM64 MSVC 工具链。' }

$cleanVersion = $Version -replace '^arm64-v', '' -replace '^v', ''
if ($cleanVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-arm64\.([1-9]\d*)$') {
    throw '版本号必须为上游三段版本加 ARM64 修订号，例如 2.0.10-arm64.1，数字不得有前导零。'
}
$assemblyVersion = $cleanVersion.Replace('-arm64.', '.')
foreach ($part in $assemblyVersion.Split('.')) {
    if ([long]$part -gt 65534) { throw '程序集版本各段不能超过 65534。' }
}

$repositoryRoot = $PSScriptRoot
$repoUrl = 'https://github.com/longhui1/STranslate'
$packageId = 'STranslate-ARM64'
$channel = 'win-arm64'
$workDirectory = Join-Path $repositoryRoot 'src/.artifacts/arm64'
$appDirectory = Join-Path $workDirectory 'app'
$toolDirectory = Join-Path $workDirectory 'vpk-tool'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory, $repositoryRoot)
$publishRoot = Join-Path $repositoryRoot 'publish'
if (-not $outputPath.StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory 必须是仓库 publish 目录下的独立子目录，避免误清理源码或 x64 输出。'
}
$assemblyInfo = Join-Path $repositoryRoot 'src/SolutionAssemblyInfo.cs'
$fodyPath = Join-Path $repositoryRoot 'src/STranslate/FodyWeavers.xml'
$originalAssemblyInfo = [IO.File]::ReadAllBytes($assemblyInfo)
$originalFody = [IO.File]::ReadAllBytes($fodyPath)

function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "命令失败（$LASTEXITCODE）：$Program $($Arguments -join ' ')" }
}

Push-Location $repositoryRoot
try {
    # 只清理本脚本独占的 staging 和输出，避免混入 x64 或上一版本资源。
    foreach ($path in @($appDirectory, $outputPath, $toolDirectory)) {
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
        New-Item $path -ItemType Directory -Force | Out-Null
    }
    $manifest = Get-Content '.config/dotnet-tools.json' -Raw | ConvertFrom-Json
    $vpkVersion = $manifest.tools.vpk.version
    [xml]$packageVersions = Get-Content 'src/Directory.Packages.props' -Raw
    $libraryVersion = ($packageVersions.Project.ItemGroup.PackageVersion | Where-Object Include -EQ 'Velopack').Version
    if ($vpkVersion -ne $libraryVersion -or $vpkVersion -ne '1.2.0') {
        throw 'Velopack 库、vpk 清单和 ARM64 helper 源码 pin 必须一起升级。'
    }
    Invoke-Checked 'dotnet' @('tool', 'install', 'vpk', '--tool-path', $toolDirectory, '--version', $vpkVersion)
    $vpk = Join-Path $toolDirectory 'vpk.exe'
    $vendor = Join-Path $toolDirectory ".store/vpk/$vpkVersion/vpk/$vpkVersion/vendor"
    if (-not (Test-Path $vendor)) { throw "vpk 私有 vendor 路径不存在：$vendor" }
    & './scripts/Build-VelopackArm64.ps1' -VendorDirectory $vendor -WorkDirectory $workDirectory

    $rustFlagsName = 'CARGO_TARGET_AARCH64_PC_WINDOWS_MSVC_RUSTFLAGS'
    $oldRustFlags = [Environment]::GetEnvironmentVariable($rustFlagsName)
    try {
        [Environment]::SetEnvironmentVariable($rustFlagsName, '-C target-feature=+crt-static')
        Invoke-Checked 'cargo' @('build', '--manifest-path', 'src/STranslate.Host/Cargo.toml', '--target-dir', 'src/STranslate.Host/target', '--locked', '--release', '--target', 'aarch64-pc-windows-msvc')
    }
    finally { [Environment]::SetEnvironmentVariable($rustFlagsName, $oldRustFlags) }

    $content = [Text.Encoding]::UTF8.GetString($originalAssemblyInfo)
    foreach ($attribute in @('AssemblyVersion', 'AssemblyFileVersion', 'AssemblyInformationalVersion')) {
        $attributeVersion = if ($attribute -eq 'AssemblyInformationalVersion') { $cleanVersion } else { $assemblyVersion }
        $content = [regex]::Replace($content, "$attribute\(`"[^`"]+`"\)", "$attribute(`"$attributeVersion`")")
    }
    [IO.File]::WriteAllText($assemblyInfo, $content, [Text.UTF8Encoding]::new($false))

    # 应用版本由 SolutionAssemblyInfo 写入。不要把应用 Version 作为全局 MSBuild
    # 属性传给 SDK/插件，否则 STranslate.Plugin 的程序集标识会变化，破坏现有社区插件。
    $buildProperties = @('-p:Platform=AnyCPU', '-p:DisableFody=true')
    Invoke-Checked 'dotnet' (@('publish', 'src/STranslate/STranslate.csproj', '-c', 'Release', '-r', 'win-arm64', '--self-contained', 'true', '-o', $appDirectory, '-p:PublishSingleFile=false', '-p:PublishReadyToRun=false', '-p:PublishTrimmed=false') + $buildProperties)

    $pluginProjects = Get-ChildItem 'src/Plugins' -Recurse -Filter '*.csproj' |
        Where-Object { $_.FullName -notmatch '[\\/]ThirdPlugins[\\/]' -and $_.BaseName -ne 'STranslate.Plugin.Ocr.WeChatBuiltIn' } |
        Sort-Object FullName
    foreach ($plugin in $pluginProjects) {
        $destination = Join-Path $appDirectory "Plugins/$($plugin.BaseName)"
        # 纯托管插件保留 AnyCPU；有 RID 资产时只解析 win-arm64，不为插件带入整套 .NET runtime。
        Invoke-Checked 'dotnet' (@('publish', $plugin.FullName, '-c', 'Release', '-r', 'win-arm64', '--self-contained', 'false', '-o', $destination, '-p:UseAppHost=false') + $buildProperties)
        Get-ChildItem $destination -Recurse -File | Where-Object Name -In @('STranslate.Plugin.dll', 'STranslate.Plugin.xml') | Remove-Item -Force
    }

    & './scripts/Test-Arm64Artifacts.ps1' -AppDirectory $appDirectory -Version $cleanVersion
    if ($DownloadPrevious) {
        $previousPath = Join-Path $workDirectory 'previous'
        if (Test-Path $previousPath) { Remove-Item $previousPath -Recurse -Force }
        New-Item $previousPath -ItemType Directory -Force | Out-Null
        # 第一版或网络不可用时仍可发布 full；vpk pack 和验证失败必须中止。
        $downloadArguments = @('download', 'github', '--repoUrl', $repoUrl, '--channel', $channel, '--outputDir', $previousPath, '--timeout', '2')
        # CI 的短期 token 避免 runner 共享 IP 的匿名限流；本地无 token 仍可生成 full。
        if ($env:GH_TOKEN) { $downloadArguments += @('--token', $env:GH_TOKEN) }
        & $vpk @downloadArguments
        if ($LASTEXITCODE -eq 0) {
            Get-ChildItem $previousPath -Filter "$packageId-*-win-arm64-full.nupkg" | Where-Object {
                $previousVersion = $_.Name -replace '^STranslate-ARM64-', '' -replace '-win-arm64-full\.nupkg$', ''
                if ($previousVersion -notmatch '^\d+\.\d+\.\d+-arm64\.[1-9]\d*$') { throw "上一版 ARM64 包版本不符合发布规则：$previousVersion" }
                $previousComparable = [version]$previousVersion.Replace('-arm64.', '.')
                $currentComparable = [version]$assemblyVersion
                $previousComparable -lt $currentComparable
            } | Copy-Item -Destination $outputPath
        }
        else { Write-Warning '没有可用的上一版 ARM64 包，本次仅生成完整更新包。' }
    }

    Invoke-Checked $vpk @('pack', '--packId', $packageId, '--packVersion', $cleanVersion, '--packDir', $appDirectory, '--outputDir', $outputPath, '--runtime', 'win-arm64', '--channel', $channel, '--mainExe', 'STranslate.exe', '--packTitle', 'STranslate ARM64', '--icon', 'src/STranslate/Resources/updater.ico', '--releaseNotes', 'ARM64-RELEASE.md')

    # 与上游便携版一致：只在 Portable ZIP 中选择便携配置目录。
    $portableZip = [IO.Compression.ZipFile]::Open((Join-Path $outputPath "$packageId-win-arm64-Portable.zip"), [IO.Compression.ZipArchiveMode]::Update)
    try {
        if (-not $portableZip.GetEntry('current/PortableConfig/')) {
            $portableZip.CreateEntry('current/PortableConfig/') | Out-Null
        }
    }
    finally { $portableZip.Dispose() }

    # 保留至多一个上一版 full，确保 vpk 写入的 feed 中每个包都有对应文件；
    # 不使用上游按下载前后文件名删除的方式，以免删除本次 metadata。
    & './scripts/Test-Arm64Artifacts.ps1' -AppDirectory $appDirectory -ReleaseDirectory $outputPath -Version $cleanVersion
    Copy-Item (Join-Path $workDirectory 'velopack-arm64-provenance.json') $outputPath
    Get-ChildItem $outputPath -File | Where-Object Name -NE 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
        "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
    } | Set-Content (Join-Path $outputPath 'SHA256SUMS.txt') -Encoding utf8
    Write-Host "ARM64 安装和更新资源已生成：$outputPath" -ForegroundColor Green
}
finally {
    [IO.File]::WriteAllBytes($assemblyInfo, $originalAssemblyInfo)
    [IO.File]::WriteAllBytes($fodyPath, $originalFody)
    Pop-Location
}
