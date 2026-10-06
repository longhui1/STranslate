param(
    [Parameter(Mandatory)][string]$VpkPath,
    [Parameter(Mandatory)][string]$BasePackage,
    [Parameter(Mandatory)][string]$DeltaPackage,
    [Parameter(Mandatory)][string]$FullPackage,
    [Parameter(Mandatory)][string]$ReportPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false

# ZIP 重压缩会改变整包哈希；验证每个文件的精确路径、解压长度和内容。
function Get-ZipFileEntries([string]$Path) {
    $entries = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            if ($entries.ContainsKey($entry.FullName)) {
                throw "ZIP 含重复文件路径：$Path / $($entry.FullName)"
            }
            $stream = $entry.Open()
            $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
            try {
                $buffer = [byte[]]::new(131072)
                $length = 0L
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $hash.AppendData($buffer, 0, $read)
                    $length += $read
                }
                if ($length -ne $entry.Length) {
                    throw "ZIP 解压长度错误：$Path / $($entry.FullName)"
                }
                $entries.Add($entry.FullName, [pscustomobject]@{
                    Length = $length
                    SHA256 = [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
                })
            }
            finally { $hash.Dispose(); $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    if ($entries.Count -eq 0) { throw "ZIP 不含可验证的文件：$Path" }
    return ,$entries
}

$reportFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportPath)
foreach ($inputPath in @($VpkPath, $BasePackage, $DeltaPackage, $FullPackage)) {
    $canonicalInput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($inputPath)
    if ([string]::Equals($canonicalInput, $reportFile, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ReportPath 不能覆盖验证输入文件。'
    }
}
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("stranslate-arm64-delta-" + [guid]::NewGuid().ToString('N'))
$report = [ordered]@{
    Pass = $false
    VelopackVersion = '1.2.0'
    Inputs = [ordered]@{}
    EntryCount = 0
    Scope = '真实 vpk delta patch 重建；全部非目录 ZIP entry 的精确路径、解压长度和 SHA-256 相同。未执行 ARM64 Update.exe、安装、退出替换或重启；这些仍需 Windows ARM64 实机验证。'
    Error = $null
}
$verificationError = $null

try {
    $paths = [ordered]@{}
    foreach ($inputFile in ([ordered]@{ Vpk = $VpkPath; Base = $BasePackage; Delta = $DeltaPackage; Full = $FullPackage }).GetEnumerator()) {
        $item = Get-Item -LiteralPath $inputFile.Value
        if ($item.PSIsContainer) { throw "必须提供文件：$($inputFile.Value)" }
        $paths[$inputFile.Key] = $item.FullName
        if ($inputFile.Key -ne 'Vpk') {
            $report.Inputs[$inputFile.Key] = [ordered]@{
                FileName = $item.Name
                SHA256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    }

    New-Item $temporaryDirectory -ItemType Directory | Out-Null
    $reconstructedPackage = Join-Path $temporaryDirectory 'reconstructed-full.nupkg'
    $patchArguments = @('--skip-updates', '--legacyConsole', 'delta', 'patch', '--base', $paths.Base, '--patch', $paths.Delta, '--output', $reconstructedPackage)
    $patchOutput = @(& $paths.Vpk @patchArguments 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) {
        throw "vpk delta patch 失败（$LASTEXITCODE）：$($patchOutput -join [Environment]::NewLine)"
    }
    if (($patchOutput -join [Environment]::NewLine) -notmatch 'Velopack CLI 1\.2\.0,') {
        throw '未确认实际执行的 vpk 为固定版本 1.2.0。'
    }

    $expected = Get-ZipFileEntries $paths.Full
    $actual = Get-ZipFileEntries $reconstructedPackage
    $report.EntryCount = $expected.Count
    if ($actual.Count -ne $expected.Count) {
        throw "delta 重建文件数量错误：需要 $($expected.Count)，实际 $($actual.Count)。"
    }
    foreach ($entry in $expected.GetEnumerator()) {
        if (-not $actual.ContainsKey($entry.Key)) { throw "delta 重建缺少精确路径：$($entry.Key)" }
        $actualEntry = $actual[$entry.Key]
        if ($entry.Value.Length -ne $actualEntry.Length -or $entry.Value.SHA256 -ne $actualEntry.SHA256) {
            throw "delta 重建文件内容不符：$($entry.Key)"
        }
    }
    $report.Pass = $true
}
catch {
    $verificationError = $_
    $report.Error = $_.Exception.Message
}
finally {
    try {
        if (Test-Path -LiteralPath $temporaryDirectory) { Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force }
    }
    catch {
        $verificationError = $_
        $report.Pass = $false
        $report.Error = "清理 delta 验证临时目录失败：$($_.Exception.Message)"
    }
    New-Item (Split-Path $reportFile -Parent) -ItemType Directory -Force | Out-Null
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportFile -Encoding utf8
}

if ($null -ne $verificationError) { throw $verificationError }
Write-Host "ARM64 delta 重建验证通过：$($report.EntryCount) 个文件，报告 $reportFile。" -ForegroundColor Green
