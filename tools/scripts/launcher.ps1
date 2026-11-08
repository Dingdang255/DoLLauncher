# DoL 版本隔离启动器（PCL 式：每个版本一个 mods 目录 + 独立 origin / 独立存档）
# 用法：
#   双击 DoL.bat            -> 菜单选择版本
#   DoL.bat 0.5.12.13       -> 直接启动该版本
#   DoL.bat -Stop           -> 停止所有由本启动器拉起的服务器
#   DoL.bat -List           -> 只列出所有版本与端口

param(
    [string]$Version,
    [switch]$Stop,
    [switch]$List,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$HtmlName = 'Degrees of Lewdity.html'
$ConfigPath = Join-Path $Root 'launcher-versions.json'
$PortBase = 8701

function Get-VersionDirs {
    $versionsRoot = Join-Path $Root 'versions'
    $bases = if (Test-Path -LiteralPath $versionsRoot) {
        Get-ChildItem -LiteralPath $versionsRoot -Directory
    } else {
        Get-ChildItem -LiteralPath $Root -Directory
    }
    $bases | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName $HtmlName) } | Sort-Object Name
}

function Get-Config {
    if (Test-Path -LiteralPath $ConfigPath) {
        try {
            $raw = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8
            if ($raw) { return @((ConvertFrom-Json $raw).versions) }
        } catch { }
    }
    return @()
}

function Save-Config($entries) {
    $obj = [pscustomobject]@{ versions = @($entries | ForEach-Object { [pscustomobject]@{ folder = $_.folder; port = $_.port } }) }
    [System.IO.File]::WriteAllText($ConfigPath, ($obj | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
}

function Test-PortInUse([int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        if (-not $task.AsyncWaitHandle.WaitOne(300)) { return $false }
        $client.EndConnect($task)
        return $true
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}

function Get-VersionPort([string]$Folder, $Entries) {
    $hit = $Entries | Where-Object { $_.folder -eq $Folder } | Select-Object -First 1
    if ($hit -and $hit.port) { return [int]$hit.port }
    $used = @($Entries | ForEach-Object { [int]$_.port })
    for ($p = $PortBase; $p -lt ($PortBase + 200); $p++) {
        if ($used -contains $p) { continue }
        if (Test-PortInUse $p) { continue }
        return $p
    }
    throw '没有可用端口'
}

# 把 mods/*.zip 写成 ModLoader 原生支持的 modList.json
function Update-ModList([string]$Dir) {
    $modsDir = Join-Path $Dir 'mods'
    if (-not (Test-Path -LiteralPath $modsDir)) { New-Item -ItemType Directory -Path $modsDir | Out-Null }
    $prio = @{ 'ModHub' = 0; 'ModI18N' = 1; 'GameOriginalImagePack' = 2 }
    $files = @(Get-ChildItem -LiteralPath $modsDir -File | Where-Object { $_.Extension -eq '.zip' })
    $sorted = @($files | Sort-Object `
            @{ Expression = { $n = ($_.BaseName -split '-')[0]; if ($prio.ContainsKey($n)) { $prio[$n] } else { 99 } } }, `
            @{ Expression = { $_.Name } })

    $dupes = $sorted | Group-Object { ($_.BaseName -split '-')[0] } | Where-Object { $_.Count -gt 1 }
    foreach ($d in $dupes) {
        Write-Warning ("mods 里有多个同前缀模组 [" + $d.Name + "]，会被同时加载，建议只留一个: " + (($d.Group | ForEach-Object { $_.Name }) -join ', '))
    }

    $list = @($sorted | ForEach-Object { 'mods/' + $_.Name })
    $json = if ($list.Count -eq 0) { '[]' } else { ConvertTo-Json -InputObject $list -Depth 3 }
    [System.IO.File]::WriteAllText((Join-Path $Dir 'modList.json'), $json, (New-Object System.Text.UTF8Encoding($false)))
    return $sorted
}

function Start-Server([string]$Dir, [int]$Port) {
    if (Test-PortInUse $Port) { return $false }
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) { throw '未找到 python，无法启动本地服务器' }
    $argLine = "-m http.server $Port --bind 127.0.0.1 --directory `"$Dir`""
    Start-Process -FilePath $python.Source -ArgumentList $argLine -WorkingDirectory $Dir -WindowStyle Minimized | Out-Null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 150
        if (Test-PortInUse $Port) { return $true }
    }
    throw "本地服务器在端口 $Port 上没有启动成功"
}

function Stop-Servers([string]$Dir) {
    $procs = Get-CimInstance Win32_Process -Filter "Name = 'python.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine -match 'http\.server' }
    if ($Dir) { $procs = $procs | Where-Object { $_.CommandLine -match [regex]::Escape($Dir) } }
    $n = 0
    foreach ($p in @($procs)) {
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        $n++
    }
    return $n
}

function Show-Versions($Dirs, $Entries) {
    $i = 1
    $work = @($Entries)
    foreach ($d in $Dirs) {
        $port = Get-VersionPort $d.Name $work
        $work = @($work | Where-Object { $_.folder -ne $d.Name }) + [pscustomobject]@{ folder = $d.Name; port = $port }
        $modsDir = Join-Path $d.FullName 'mods'
        $count = @(Get-ChildItem -LiteralPath $modsDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -eq '.zip' }).Count
        $state = if (Test-PortInUse $port) { '运行中' } else { '未运行' }
        '{0,2}) {1}  端口 {2}  mods {3} 个  [{4}]' -f $i, $d.Name, $port, $count, $state
        $i++
    }
}

$versionDirs = @(Get-VersionDirs)
if ($versionDirs.Count -eq 0) {
    Write-Host "在 $Root 下没有找到包含 $HtmlName 的版本目录。"
    exit 1
}

$entries = @(Get-Config)

if ($Stop) {
    $total = 0
    foreach ($d in $versionDirs) {
        $total += (Stop-Servers $d.FullName)
    }
    Write-Host "已停止 $total 个本地服务器。"
    exit 0
}

if ($List) {
    Show-Versions $versionDirs $entries
    exit 0
}

# 选择版本
$target = $null
if ($Version) {
    $target = $versionDirs | Where-Object { $_.Name -eq $Version } | Select-Object -First 1
    if (-not $target) {
        $target = $versionDirs | Where-Object { $_.Name -like "*$Version*" } | Select-Object -First 1
    }
    if (-not $target) {
        Write-Host "找不到版本 [$Version]，现有："
        Show-Versions $versionDirs $entries
        exit 1
    }
} else {
    Write-Host 'DoL 版本隔离启动器'
    Write-Host ''
    Write-Host '  0) 停止所有正在运行的版本'
    Show-Versions $versionDirs $entries
    Write-Host ''
    $pick = Read-Host '选择要启动的版本（序号或目录名）'
    if ($pick -eq '0') {
        $total = 0
        foreach ($d in $versionDirs) { $total += (Stop-Servers $d.FullName) }
        Write-Host "已停止 $total 个本地服务器。"
        exit 0
    }
    if ($pick -match '^\d+$') {
        $idx = [int]$pick
        if ($idx -lt 1 -or $idx -gt $versionDirs.Count) { Write-Host '序号超出范围'; exit 1 }
        $target = $versionDirs[$idx - 1]
    } else {
        $target = $versionDirs | Where-Object { $_.Name -like "*$pick*" } | Select-Object -First 1
    }
    if (-not $target) { Write-Host '没有匹配的版本'; exit 1 }
}

$folder = $target.Name
$port = Get-VersionPort $folder $entries
$entries = @($entries | Where-Object { $_.folder -ne $folder }) + [pscustomobject]@{ folder = $folder; port = $port }
Save-Config $entries

$mods = Update-ModList $target.FullName
$url = 'http://127.0.0.1:' + $port + '/' + [Uri]::EscapeDataString($HtmlName)

Write-Host ''
Write-Host "版本目录 : $($target.FullName)"
Write-Host "端口     : $port  (不同端口 = 不同浏览器 origin，存档/模组开关各自独立)"
Write-Host "磁盘模组 : $(if ($mods.Count -eq 0) { '（mods 目录为空）' } else { ($mods | ForEach-Object { $_.Name }) -join '; ' })"
Write-Host "游戏地址 : $url"
Write-Host ''

$alreadyRunning = Test-PortInUse $port
if ($alreadyRunning) {
    Write-Host '该版本已经在运行，直接打开页面。'
} else {
    Start-Server $target.FullName $port | Out-Null
    Write-Host '本地服务器已启动。'
}

if (-not $NoBrowser) {
    Start-Process $url | Out-Null
    Write-Host '已在浏览器中打开。'
}
Write-Host '提示：把新的 .zip 模组丢进该版本的 mods 目录，下次启动自动生效；不玩了可在菜单选 0 或运行 DoL.bat -Stop。'
