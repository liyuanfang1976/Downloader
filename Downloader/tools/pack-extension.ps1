param(
    [string]$Chrome = (Join-Path ${env:ProgramFiles} 'Google\Chrome\Application\chrome.exe'),
    [switch]$NewKey
)

$extDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'extension'
$src = Join-Path $extDir 'src'
$pem = Join-Path $extDir 'DownloaderHandoff.pem'
$crx = Join-Path $extDir 'DownloaderHandoff.crx'

if (-not (Test-Path (Join-Path $src 'manifest.json'))) { Write-Host "缺少文件: $(Join-Path $src 'manifest.json')" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $Chrome)) { Write-Host "找不到 Chrome: $Chrome" -ForegroundColor Red; exit 1 }

if (-not (Test-Path $pem)) {
    if (-not $NewKey) {
        Write-Host "缺少私钥 DownloaderHandoff.pem（签名私钥不随开源仓库分发）。" -ForegroundColor Red
        Write-Host ""
        Write-Host "  - 只是改了源码自己用：不需要本脚本，直接在 chrome://extensions 以开发者模式「加载已解压」指向 extension\src\，" -ForegroundColor Yellow
        Write-Host "    ID 由 manifest.json 里的 key（公钥）固定，与私钥无关。"
        Write-Host "  - 要重新签名成 crx 分发：加 -NewKey 生成你自己的私钥。注意 ID 会随之改变，" -ForegroundColor Yellow
        Write-Host "    需同步更新 manifest.json 的 key、宿主清单的 allowed_origins 与注册表键名（见 extension\README.md「三处必须对齐的 ID」）。"
        exit 1
    }
    Write-Host "未找到私钥，将生成新私钥（新文件 $pem，扩展 ID 随之改变）..." -ForegroundColor Yellow
}

# Chrome 的打包器总是把 <目录名>.crx 写在该目录的上一级，不能直接喂 dist 之类的目录名
if (Test-Path $pem) {
    & $Chrome --pack-extension="$src" --pack-extension-key="$pem"
} else {
    & $Chrome --pack-extension="$src"
}

$staged = Join-Path $extDir 'src.crx'
for ($i = 0; $i -lt 40 -and -not (Test-Path $staged); $i++) { Start-Sleep -Milliseconds 250 }
if (-not (Test-Path $staged)) { Write-Host "打包失败，Chrome 没有产出 $staged" -ForegroundColor Red; exit 1 }

Move-Item $staged $crx -Force
if (-not (Test-Path $pem) -and (Test-Path (Join-Path $extDir 'src.pem'))) {
    Move-Item (Join-Path $extDir 'src.pem') $pem
    Write-Host "已生成新私钥: $pem（请妥善保管，丢了 ID 就变了）" -ForegroundColor Yellow
    Write-Host "别忘了同步更新 manifest.json 的 key、allowed_origins 与注册表键名。" -ForegroundColor Yellow
}
Write-Host "已打包: $crx" -ForegroundColor Green
if (Test-Path $pem) {
    Write-Host "扩展 ID: mhdhfggpookgphfnepgemppfhjdpdhhc (由 $pem 决定，私钥不可丢)"
} else {
    Write-Host "扩展 ID: 见打包输出（新私钥对应新 ID）"
}
