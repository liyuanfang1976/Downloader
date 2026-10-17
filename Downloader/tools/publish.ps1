param(
    [string]$Configuration = 'Release',
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $Root "src\Downloader\bin\$Configuration\net8.0-windows"
if (-not $OutDir) { $OutDir = Join-Path $Root 'dist\Downloader' }

if (-not (Test-Path (Join-Path $bin 'Downloader.exe'))) {
    Write-Host "找不到 $bin，请先编译 $Configuration" -ForegroundColor Red
    exit 1
}

# 主程序与宿主各自的运行时文件 + 扩展包。*.pdb 是调试符号，运行不需要；
# 图标已作为嵌入资源进 dll，不必单独分发
$keep = @(
    'Downloader.exe', 'Downloader.dll', 'Downloader.deps.json', 'Downloader.runtimeconfig.json',
    'Downloader.NativeHost.exe', 'Downloader.NativeHost.dll',
    'Downloader.NativeHost.deps.json', 'Downloader.NativeHost.runtimeconfig.json',
    'System.Threading.RateLimiting.dll',
    'DownloaderHandoff.crx'
)

New-Item -Path $OutDir -ItemType Directory -Force | Out-Null
$stale = @(Get-ChildItem -Path $OutDir -File -ErrorAction SilentlyContinue)
if ($stale.Count -gt 0) { $stale | Remove-Item -Force }

foreach ($file in $keep) {
    $src = Join-Path $bin $file
    if (-not (Test-Path $src)) {
        Write-Host "输出目录缺少 $file，发布中止" -ForegroundColor Red
        exit 1
    }
    Copy-Item $src $OutDir
}

Write-Host ''
Write-Host "发布目录: $OutDir" -ForegroundColor Green
Get-ChildItem $OutDir | ForEach-Object {
    Write-Host ("  {0,-38} {1,7:N0} KB" -f $_.Name, ($_.Length / 1KB))
}
Write-Host ''
Write-Host '用法：整个文件夹拷走即可用；接管在「设置 → 启用 Chrome 下载接管」，改完重启 Chrome（首次要点一次启用）'
