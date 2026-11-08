# 构建 / 打包脚本（需要 .NET 10 SDK）
# 用法：
#   .\build.ps1                     -> 生成 DoLLauncher.exe，并打一个「只有启动器」的分发包
#   .\build.ps1 -IncludeVersions    -> 额外把本机 versions 目录一起打进分发包
#   .\build.ps1 -NoZip              -> 只生成 exe，不打包
#   .\build.ps1 -FrameworkDependent -> 体积更小，但需要目标机器安装 .NET 10 桌面运行时

param(
    [switch]$IncludeVersions,
    [switch]$NoZip,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\DoLLauncher'
$publish = Join-Path $root '_publish'
# 运行时目录（exe、versions、backups 都在这里），与源码目录 dev 同级
$runtime = Join-Path (Split-Path $root -Parent) 'DoLLauncher'

# 版本信息：tag|commit|构建时间，程序通过程序集 InformationalVersion 读取
$tag = (git -C $root describe --tags --exact-match 2>$null | Out-String).Trim()
if ($tag -eq '') { $tag = 'dev' }
$commit = (git -C $root rev-parse --short HEAD 2>$null | Out-String).Trim()
if ($commit -eq '') { $commit = 'local' }
$buildTime = (Get-Date).ToString('yyyy-MM-dd HH:mm')
$infoVersion = "$tag|$commit|$buildTime"

Push-Location $project
try {
    if ($FrameworkDependent) {
        dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true `
            -p:InformationalVersion="$infoVersion" -o $publish
    } else {
        dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
            -p:InformationalVersion="$infoVersion" -o $publish
    }
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }
} finally {
    Pop-Location
}

$exe = Join-Path $publish 'DoLLauncher.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "没有生成 $exe" }
Copy-Item -LiteralPath $exe -Destination (Join-Path $runtime 'DoLLauncher.exe') -Force
Write-Host "已生成：$(Join-Path $runtime 'DoLLauncher.exe')"

if ($NoZip) { exit 0 }

$dist = Join-Path $root '_dist'
$stage = Join-Path $dist 'DoL'
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Copy-Item -LiteralPath $exe -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'tools\scripts\DoL.bat') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'tools\scripts\launcher.ps1') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $stage

if ($IncludeVersions) {
    Write-Host '正在打包游戏版本，体积较大，请耐心等待'
    Copy-Item -LiteralPath (Join-Path $runtime 'versions') -Destination $stage -Recurse
}

$zip = Join-Path (Split-Path $root -Parent) 'DoL-Launcher.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -LiteralPath $dist -Recurse -Force

Write-Host ''
Write-Host "分发包：$zip"
Write-Host ("大小：{0:N1} MB" -f ((Get-Item -LiteralPath $zip).Length / 1MB))
