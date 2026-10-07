[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ReportDirectory
)

$ErrorActionPreference = 'Stop'
if (-not [OperatingSystem]::IsWindows() -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'Arm64') {
    throw '真实 OCR 验证必须在 Windows ARM64 runner 执行，不能用交叉构建或 x64 模拟替代。'
}
$releasePath = (Resolve-Path $ReleaseDirectory).Path
$reportPath = [IO.Path]::GetFullPath($ReportDirectory)
New-Item $reportPath -ItemType Directory -Force | Out-Null
$feed = Get-Content (Join-Path $releasePath 'releases.win-arm64.json') -Raw | ConvertFrom-Json
$target = @($feed.Assets | Where-Object { $_.Type -eq 'Full' -and $_.Version -eq $Version })
if ($target.Count -ne 1 -or $target[0].PackageId -ne 'STranslate-ARM64' -or
    $target[0].FileName -ne "STranslate-ARM64-$Version-win-arm64-full.nupkg") {
    throw '无法唯一确定本次需要执行的 ARM64 完整包。'
}
$fullPackage = Join-Path $releasePath $target[0].FileName
$fullHash = (Get-FileHash $fullPackage -Algorithm SHA256).Hash
if ($fullHash -ne $target[0].SHA256 -or (Get-Item $fullPackage).Length -ne $target[0].Size) {
    throw '用于真实执行的完整包不符合更新 feed 的 SHA256 或长度。'
}
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$temporaryPath = Join-Path $temporaryRoot "STranslate-arm64-ocr-$([guid]::NewGuid().ToString('N'))"
New-Item $temporaryPath -ItemType Directory | Out-Null
try {
    $unpackPath = Join-Path $temporaryPath 'package'
    [IO.Compression.ZipFile]::ExtractToDirectory($fullPackage, $unpackPath)
    $appDirectory = Join-Path $unpackPath 'lib/app'
    if (-not (Test-Path (Join-Path $appDirectory 'STranslate.Plugin.dll'))) {
        throw '完整包中缺少宿主插件 SDK。'
    }
    $harnessPath = Join-Path $temporaryPath 'harness'
    $project = Join-Path $PSScriptRoot '../src/Arm64/Tests/PaddleOcrSmoke/PaddleOcrSmoke.csproj'
    dotnet publish $project -c Release -r win-arm64 --self-contained false -p:SmokeAppDirectory="$appDirectory" -p:DisableFody=true -o "$harnessPath"
    if ($LASTEXITCODE -ne 0) { throw '真实插件 OCR 验证程序编译失败。' }
    $cachePath = Join-Path $temporaryPath 'user-cache'
    $validationPath = Join-Path $reportPath 'arm64-ocr-validation.json'
    & (Join-Path $harnessPath 'PaddleOcrSmoke.exe') `
        --app $appDirectory --cache $cachePath --report $validationPath `
        --version $Version --package-sha256 $fullHash 2>&1 |
        Tee-Object (Join-Path $reportPath 'arm64-ocr-smoke.log')
    $smokeExitCode = $LASTEXITCODE
    if ($smokeExitCode -ne 0) { throw "Windows ARM64 实际 OCR 验证失败，退出码 $smokeExitCode。" }
    $report = Get-Content $validationPath -Raw | ConvertFrom-Json
    if (-not $report.Success -or $report.ProcessArchitecture -ne 'Arm64' -or $report.OSArchitecture -ne 'Arm64' -or
        $report.Version -ne $Version -or $report.FullPackageSHA256 -ne $fullHash -or
        @($report.ModelInputs).Count -ne 4 -or @($report.Recognitions).Count -ne 2 -or
        @($report.NativeModules | Where-Object Machine -NE '0xAA64').Count -ne 0) {
        throw 'ARM64 执行报告缺少必须的真实模型、识别或原生架构证据。'
    }
    Write-Host "原生 Windows ARM64 插件 OCR 验证通过，版本 $Version，报告 $validationPath。" -ForegroundColor Green
}
finally {
    # 临时在线模型不进入构建或 Release 产物。
    if (Test-Path $temporaryPath) { Remove-Item $temporaryPath -Recurse -Force }
}
