# 一键打包 QiChargerStationTool
# 用法（Windows PowerShell，在 QiChargerStationTool 目录）：
#   .\publish.ps1              # 绿色单文件 + 文件夹版
#   .\publish.ps1 -Zip         # 额外打 zip
#   .\publish.ps1 -SelfContained:$false   # 小体积，需装 .NET 8 桌面运行时

param(
    [bool]$SelfContained = $true,
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProj = Join-Path $root "src\QiChargerStationTool.App\QiChargerStationTool.App\QiChargerStationTool.App.csproj"
$out = Join-Path $root "publish"
$portable = Join-Path $out "QiChargerStationTool-Portable"
$single = Join-Path $out "QiChargerStationTool-SingleFile"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $portable, $single | Out-Null

Write-Host "==> 发布文件夹版（绿色）→ $portable" -ForegroundColor Cyan
dotnet publish $appProj -c Release -r win-x64 --self-contained $SelfContained `
    -p:PublishSingleFile=false -p:PublishReadyToRun=true `
    -o $portable
if ($LASTEXITCODE -ne 0) { throw "publish portable failed" }

Write-Host "==> 发布单文件版 → $single" -ForegroundColor Cyan
dotnet publish $appProj -c Release -r win-x64 --self-contained $SelfContained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $single
if ($LASTEXITCODE -ne 0) { throw "publish single-file failed" }

# 拷贝密钥与配置到两个包
$keysSrc = Join-Path $root "..\docs\keys\private.pem"
foreach ($dir in @($portable, $single)) {
    $keysDst = Join-Path $dir "keys"
    New-Item -ItemType Directory -Path $keysDst -Force | Out-Null
    if (Test-Path $keysSrc) {
        Copy-Item $keysSrc (Join-Path $keysDst "private.pem") -Force
        Write-Host "已包含密钥 keys/private.pem"
    } else {
        Write-Warning "未找到 docs/keys/private.pem，产线需自行拷贝"
    }
}

# 说明文件
$readme = @"
QiChargerStationTool 产线工装
============================
1. 解压后运行 QiChargerStationTool.exe
2. ZQWL-CANFD 插入 Windows（不要占用 WSL），点「扫描」自动选中
3. 点「连接」→ 测试
4. 密钥：keys/private.pem（写 SN / 设功率 / OTA 需要）
5. 配置：appsettings.json

若提示缺少 .NET 运行时，请安装
  .NET 8 Desktop Runtime (x64)
https://dotnet.microsoft.com/download/dotnet/8.0
"@
foreach ($dir in @($portable, $single)) {
    Set-Content -Path (Join-Path $dir "使用说明.txt") -Value $readme -Encoding UTF8
}

if ($Zip) {
    Write-Host "==> 打 zip" -ForegroundColor Cyan
    Compress-Archive -Path $portable -DestinationPath (Join-Path $out "QiChargerStationTool-Portable.zip") -Force
    Compress-Archive -Path $single  -DestinationPath (Join-Path $out "QiChargerStationTool-SingleFile.zip") -Force
}

Write-Host ""
Write-Host "完成！输出目录：" -ForegroundColor Green
Write-Host "  $portable"
Write-Host "  $single"
Get-ChildItem $out | Format-Table Name, Length
