param(
    [Parameter(Mandatory)][string]$VendorDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# vpk 1.2.0 的 Windows helper 默认仍是 x86。直接构建同版本上游源码，
# 只替换本次 ARM64 构建的私有工具目录，不修改用户的全局工具或 NuGet 缓存。
$velopackVersion = '1.2.0'
$sourceCommit = 'f2edcbcafb81da5b3c884aaea330e225ad91d8b6'
$sourceDirectory = Join-Path $WorkDirectory 'velopack-source'

if (-not (Test-Path (Join-Path $sourceDirectory '.git'))) {
    & git clone --quiet --depth 1 --branch $velopackVersion https://github.com/velopack/velopack.git $sourceDirectory
    if ($LASTEXITCODE -ne 0) { throw '获取 Velopack 上游源码失败。' }
}
$actualCommit = (& git -C $sourceDirectory rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $sourceCommit) {
    throw "Velopack 源码提交不匹配：需要 $sourceCommit，实际 $actualCommit。"
}
& git -C $sourceDirectory diff --quiet -- . ':(top,exclude)Cargo.toml' ':(top,exclude)Cargo.lock'
if ($LASTEXITCODE -ne 0) { throw 'Velopack 缓存源码含额外修改；请移除构建缓存后重试。' }

# 与该版本上游发布工作流相同，只写入包版本，不改功能代码或依赖版本。
foreach ($name in @('Cargo.toml', 'Cargo.lock')) {
    $path = Join-Path $sourceDirectory $name
    $content = [IO.File]::ReadAllText($path).Replace('"0.0.0-local"', '"1.2.0"')
    [IO.File]::WriteAllText($path, $content, [Text.UTF8Encoding]::new($false))
}

$rustFlagsName = 'CARGO_TARGET_AARCH64_PC_WINDOWS_MSVC_RUSTFLAGS'
$oldRustFlags = [Environment]::GetEnvironmentVariable($rustFlagsName)
try {
    [Environment]::SetEnvironmentVariable($rustFlagsName, '-C target-feature=+crt-static')
    & cargo build --manifest-path (Join-Path $sourceDirectory 'Cargo.toml') `
        --target-dir (Join-Path $sourceDirectory 'target') `
        --locked --release --target aarch64-pc-windows-msvc `
        --package velopack_bins --features windows --bin setup --bin update --bin stub
    if ($LASTEXITCODE -ne 0) { throw '编译原生 ARM64 Velopack 安装及更新组件失败。' }
}
finally {
    [Environment]::SetEnvironmentVariable($rustFlagsName, $oldRustFlags)
}

$nativeDirectory = Join-Path $sourceDirectory 'target/aarch64-pc-windows-msvc/release'
foreach ($name in @('setup.exe', 'update.exe', 'stub.exe')) {
    $nativePath = Join-Path $nativeDirectory $name
    if (-not (Test-Path $nativePath)) { throw "缺少 Velopack ARM64 组件：$nativePath" }
    Copy-Item $nativePath (Join-Path $VendorDirectory $name) -Force
}

@{
    Version = $velopackVersion
    Source = 'https://github.com/velopack/velopack'
    Commit = $sourceCommit
    Target = 'aarch64-pc-windows-msvc'
    Crt = 'static'
    Components = @('setup.exe', 'update.exe', 'stub.exe') | ForEach-Object {
        @{ FileName = $_; SHA256 = (Get-FileHash (Join-Path $VendorDirectory $_) -Algorithm SHA256).Hash }
    }
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $WorkDirectory 'velopack-arm64-provenance.json') -Encoding utf8
