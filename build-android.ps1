# 构建 Android 端 APK（需要 .NET 10 SDK + maui-android workload + JDK 17）
# 用法：
#   .\build-android.ps1                 -> Release APK，复制到上一级目录的 DoL-Launcher-android.apk
#   .\build-android.ps1 -Debug          -> Debug APK，便于排查
#   .\build-android.ps1 -NoCopy         -> 只构建，不复制产物
#   .\build-android.ps1 -Install        -> 构建后直接 adb install 到模拟器/设备
#   .\build-android.ps1 -StorePass xxx -KeyPass xxx -KeyAlias xxx
#                                       -> 用 keys\dol-release.keystore 做正式签名
#                                          （也可先用 DOL_KEY_ALIAS/DOL_KEY_PASS/DOL_STORE_PASS 环境变量）
#
# 说明：
#   1. 未提供签名密码时，产物是 debug keystore 签名的本地测试包（可安装、不能用于发版）；
#      正式签名包由 CI 在 main / tag 上生成。
#   2. 版本信息（tag|commit|时间）由 csproj 的 InjectDolBuildInfo 目标自动写入，这里无需传参。

param(
    [switch]$Debug,
    [switch]$NoCopy,
    [switch]$Install,
    [string]$Serial = '127.0.0.1:7555',
    [string]$KeyStore = '',
    [string]$KeyAlias = '',
    [string]$KeyPass = '',
    [string]$StorePass = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\DoLLauncher.Maui\DoLLauncher.Maui.csproj'
$dist = Join-Path (Split-Path $root -Parent) 'DoL-Launcher-android.apk'

# ---------- Android SDK / JDK ----------

function Resolve-Dir([string]$current, [string[]]$candidates) {
    if ($current -and (Test-Path -LiteralPath $current)) { return $current }
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    return ''
}

$sdk = Resolve-Dir $env:ANDROID_HOME @($env:ANDROID_SDK_ROOT, 'D:\Android\Sdk', "$env:LOCALAPPDATA\Android\Sdk")
if (-not $sdk) { throw '找不到 Android SDK，请设置 ANDROID_HOME' }
$env:ANDROID_HOME = $sdk
$env:ANDROID_SDK_ROOT = $sdk

$jdkCandidates = @($env:JAVA_HOME, 'D:\Dev\jdk-17.0.21.102-hotspot')
$jdkCandidates += (Get-ChildItem 'D:\Dev' -Directory -Filter 'jdk-17*' -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName)
$jdkCandidates += (Get-ChildItem 'C:\Program Files\Microsoft' -Directory -Filter 'jdk-17*' -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName)
$java = Resolve-Dir '' $jdkCandidates
if (-not $java) { throw '找不到 JDK 17，请设置 JAVA_HOME' }
$env:JAVA_HOME = $java

Write-Host "SDK：$sdk"
Write-Host "JDK：$java"

# ---------- 签名 ----------

$keystore = if ($KeyStore) { $KeyStore } elseif ($env:DOL_KEYSTORE) { $env:DOL_KEYSTORE } else { Join-Path $root 'keys\dol-release.keystore' }
$alias = if ($KeyAlias) { $KeyAlias } else { $env:DOL_KEY_ALIAS }
$key = if ($KeyPass) { $KeyPass } else { $env:DOL_KEY_PASS }
$store = if ($StorePass) { $StorePass } else { $env:DOL_STORE_PASS }

if ((Test-Path -LiteralPath $keystore) -and $alias -and $key -and $store) {
    $env:DOL_KEYSTORE = (Resolve-Path -LiteralPath $keystore).Path
    $env:DOL_KEY_ALIAS = $alias
    $env:DOL_KEY_PASS = $key
    $env:DOL_STORE_PASS = $store
    Write-Host "签名：正式签名（$($env:DOL_KEYSTORE)）"
} else {
    Write-Host '签名：未提供正式签名密码，产物为 debug 签名的本地测试包（不能用于发版，正式包由 CI 生成）'
}

# ---------- 构建 ----------

$configuration = if ($Debug) { 'Debug' } else { 'Release' }
Push-Location $root
try {
    dotnet build $project -c $configuration -f net10.0-android -p:AndroidPackageFormat=apk
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build 失败' }
} finally {
    Pop-Location
}

$apk = Join-Path $root "src\DoLLauncher.Maui\bin\$configuration\net10.0-android\com.dol.launcher-Signed.apk"
if (-not (Test-Path -LiteralPath $apk)) { throw "没有生成 $apk" }
Write-Host "已生成：$apk"

if (-not $NoCopy) {
    Copy-Item -LiteralPath $apk -Destination $dist -Force
    Write-Host ''
    Write-Host "APK：$dist"
    Write-Host ("大小：{0:N1} MB" -f ((Get-Item -LiteralPath $dist).Length / 1MB))
}

if ($Install) {
    & adb connect $Serial | Out-Null
    $installed = (& adb -s $Serial shell pm install -r $apk | Out-String).Trim()
    Write-Host "安装：$installed"
}
