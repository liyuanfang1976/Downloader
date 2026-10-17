$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root 'src\Downloader\Downloader.csproj'
$HostProject = Join-Path $Root 'src\NativeHost\NativeHost.csproj'

function Invoke-Build {
    param([string]$Configuration)

    Write-Host ''
    Write-Host "  正在编译 $Configuration ..." -ForegroundColor Cyan
    Write-Host ''

    dotnet build $Project -c $Configuration
    $mainOk = $LASTEXITCODE -eq 0
    dotnet build $HostProject -c $Configuration
    $hostOk = $LASTEXITCODE -eq 0

    if ($mainOk -and $hostOk) {
        Write-Host ''
        Write-Host "  $Configuration 编译成功" -ForegroundColor Green
        $outDir = Join-Path $Root "src\Downloader\bin\$Configuration\net8.0-windows"
        Write-Host "  输出目录: $outDir"
        if (Test-Path $outDir) {
            $exe = Join-Path $outDir 'Downloader.exe'
            if (Test-Path $exe) {
                $file = Get-Item $exe
                Write-Host "  文件大小: $([math]::Round($file.Length / 1MB, 2)) MB"
            }
        }
    }
    else {
        Write-Host ''
        Write-Host "  $Configuration 编译失败，退出码 $LASTEXITCODE" -ForegroundColor Red
    }
}

while ($true) {
    Clear-Host
    Write-Host ''
    Write-Host '  ========================================'
    Write-Host '            Downloader 编译脚本'
    Write-Host '  ========================================'
    Write-Host ''
    Write-Host '    [1]  编译 Debug'
    Write-Host '    [2]  编译 Release'
    Write-Host '    [0]  退出'
    Write-Host ''
    $choice = (Read-Host '  请输入选项').Trim()

    switch ($choice) {
        '1' {
            Invoke-Build 'Debug'
            Write-Host ''
            Read-Host '  按回车返回菜单'
        }
        '2' {
            Invoke-Build 'Release'
            Write-Host ''
            Read-Host '  按回车返回菜单'
        }
        '0' {
            Write-Host ''
            Write-Host '  已退出' -ForegroundColor Gray
            return
        }
        default {
            Write-Host '  无效选项，请重新输入' -ForegroundColor Yellow
            Start-Sleep -Seconds 1
        }
    }
}
