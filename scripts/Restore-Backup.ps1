<#
.SYNOPSIS
    從系統備份 zip（「系統備份」頁或排程作業產生）還原資料庫與檔案。

.DESCRIPTION
    ⚠️ 必須先停止網站（IIS 停止應用程式集區，或在網站目錄放 app_offline.htm）。網站執行中無法獨佔資料庫檔，腳本會直接中止。

    步驟：
      1. 讀網站目錄的 appsettings.json 與 appsettings.Production.json 取得各資料夾路徑（參數可個別覆寫）。
         ⚠️ 寫在 web.config 或系統環境變數裡的路徑讀不到，請用參數指定。
      2. 解壓到暫存目錄，核對 manifest.json 的格式版本與資料庫 SHA-256。
      3. 備份的系統版本比目前部署的新時中止（-Force 可略過）；比較舊沒關係，網站啟動時會自動 migrate。
      4. 現有的資料庫檔與 -wal、-shm 改名保留（*.before-restore-時間）；殘留的 -wal 會弄壞還原的資料庫，絕不可留著。
      5. 刪除鎖檔（*.migration.lock、*.job-*.lock），放入備份的資料庫。
      6. 各資料夾：現有的改名保留，換成備份的內容；金鑰資料夾則是「合併」（只補上沒有的金鑰），備份之後才發的登入 Cookie 仍有效。
    還原後啟動網站，確認可以登入；有問題時依腳本最後印出的步驟回復。

    衍生專案重新產生過 migration，不能用腳手架（或其他專案）的備份還原。

.EXAMPLE
    .\Restore-Backup.ps1 -BackupFile D:\Backup\backup-20261004-020000.zip -SitePath C:\inetpub\app

.EXAMPLE
    .\Restore-Backup.ps1 -BackupFile .\backup-20261004-020000.zip -DatabasePath D:\data\DB -ProjectFilePath D:\data\ProjectFile -ExceptionPath D:\data\Exception -TokenUsagePath D:\data\TokenUsage -AuditContentPath D:\data\AuditContent -DataProtectionKeyPath D:\data\Keys -Yes
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupFile,

    # 網站目錄（含 appsettings.json）。不給時所有路徑都要用參數指定。
    [string]$SitePath,

    [string]$DatabasePath,
    [string]$ProjectFilePath,
    [string]$ExceptionPath,
    [string]$TokenUsagePath,
    [string]$AiCallLogPath,
    [string]$AuditContentPath,
    [string]$DataProtectionKeyPath,

    # 目前部署的系統版本（例如 0.9.99）；不給時讀 appsettings 的 SystemVersion。
    [string]$CurrentVersion,

    # 備份的系統版本比目前部署的新時仍然還原。
    [switch]$Force,

    # 不再詢問確認。
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$DatabaseFileName = 'BackendDB.db'
$SupportedFormatVersion = 1

function Read-JsonFile([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Get-SettingValue($settings, [string[]]$names) {
    $node = $settings
    foreach ($name in $names) {
        if ($null -eq $node) { return $null }
        $property = $node.PSObject.Properties[$name]
        if ($null -eq $property) { return $null }
        $node = $property.Value
    }
    return $node
}

function Get-VersionNumber([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    $match = [regex]::Match($text, '\d+\.\d+\.\d+')
    if (-not $match.Success) { return $null }
    return [version]$match.Value
}

# ---------- 1. 解析路徑 ----------
$BackupFile = (Resolve-Path -LiteralPath $BackupFile).Path
$settingsLayers = @()
if ($SitePath) {
    $settingsLayers += Read-JsonFile (Join-Path $SitePath 'appsettings.json')
    $settingsLayers += Read-JsonFile (Join-Path $SitePath 'appsettings.Production.json')
}

function Resolve-SettingPath([string]$explicit, [string]$key) {
    if ($explicit) { return $explicit }
    $value = $null
    foreach ($layer in $settingsLayers) {
        $candidate = Get-SettingValue $layer @('SystemSettings', 'ExternalFileSystem', $key)
        if ($candidate) { $value = $candidate }
    }
    return $value
}

$targets = @{
    DatabasePath          = Resolve-SettingPath $DatabasePath 'DatabasePath'
    ProjectFilePath       = Resolve-SettingPath $ProjectFilePath 'ProjectFilePath'
    ExceptionPath         = Resolve-SettingPath $ExceptionPath 'ExceptionPath'
    TokenUsagePath        = Resolve-SettingPath $TokenUsagePath 'TokenUsagePath'
    AiCallLogPath         = Resolve-SettingPath $AiCallLogPath 'AiCallLogPath'
    AuditContentPath      = Resolve-SettingPath $AuditContentPath 'AuditContentPath'
    DataProtectionKeyPath = Resolve-SettingPath $DataProtectionKeyPath 'DataProtectionKeyPath'
}

if (-not $targets.DatabasePath) {
    throw '找不到資料庫目錄：請給 -SitePath（含 appsettings.json）或 -DatabasePath。'
}

if (-not $CurrentVersion) {
    foreach ($layer in $settingsLayers) {
        $candidate = Get-SettingValue $layer @('SystemSettings', 'SystemInformation', 'SystemVersion')
        if ($candidate) { $CurrentVersion = $candidate }
    }
}

$databaseFile = Join-Path $targets.DatabasePath $DatabaseFileName

# ---------- 2. 確認網站已停止 ----------
if (Test-Path -LiteralPath $databaseFile) {
    try {
        $probe = [System.IO.File]::Open($databaseFile, 'Open', 'ReadWrite', 'None')
        $probe.Dispose()
    }
    catch {
        throw "無法獨佔開啟 $databaseFile —— 網站可能還在執行。請先停止應用程式集區（或放 app_offline.htm）再還原。"
    }
}

# ---------- 3. 解壓並核對 ----------
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('restore-' + [guid]::NewGuid().ToString('N'))
[System.IO.Compression.ZipFile]::ExtractToDirectory($BackupFile, $staging)

try {
    $manifest = Read-JsonFile (Join-Path $staging 'manifest.json')
    if ($null -eq $manifest) { throw '備份裡沒有 manifest.json，不是本系統產生的備份。' }
    if ([int]$manifest.formatVersion -ne $SupportedFormatVersion) {
        throw "不支援的備份格式版本 $($manifest.formatVersion)（這支腳本支援 $SupportedFormatVersion）。"
    }

    $stagedDatabase = Join-Path $staging ($manifest.databaseEntry -replace '/', '\')
    if (-not (Test-Path -LiteralPath $stagedDatabase)) { throw '備份裡找不到資料庫檔。' }
    $hash = (Get-FileHash -LiteralPath $stagedDatabase -Algorithm SHA256).Hash
    if ($hash -ne $manifest.databaseSha256) { throw '資料庫檔的 SHA-256 與 manifest 不符，備份可能已損毀。' }

    $backupVersion = Get-VersionNumber $manifest.systemVersion
    $deployedVersion = Get-VersionNumber $CurrentVersion
    if ($backupVersion -and $deployedVersion -and $backupVersion -gt $deployedVersion -and -not $Force) {
        throw "備份的系統版本（$($manifest.systemVersion)）比目前部署的（$CurrentVersion）新：舊程式不認得新的資料表結構。請先部署相同或更新的版本，或加 -Force。"
    }

    Write-Host ''
    Write-Host "備份檔：$BackupFile"
    Write-Host "建立時間（UTC）：$($manifest.createdAtUtc)　系統版本：$($manifest.systemVersion)　最後 migration：$($manifest.lastMigration)"
    Write-Host "目前部署版本：$CurrentVersion"
    Write-Host "資料庫 → $databaseFile"
    foreach ($folder in $manifest.folders) {
        Write-Host ("{0}（{1} 個檔案）→ {2}" -f $folder.entry, $folder.fileCount, $targets[$folder.settingKey])
    }
    Write-Host '⚠️ 寫在 web.config 或系統環境變數裡的路徑讀不到；路徑不對請用參數指定。'
    Write-Host ''

    if (-not $Yes) {
        $answer = Read-Host '確定要還原嗎？現有的資料會改名保留。輸入 YES 繼續'
        if ($answer -ne 'YES') { Write-Host '已取消。'; return }
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $kept = @()

    # ---------- 4. 資料庫 ----------
    New-Item -ItemType Directory -Force -Path $targets.DatabasePath | Out-Null
    foreach ($file in @($databaseFile, "$databaseFile-wal", "$databaseFile-shm")) {
        if (Test-Path -LiteralPath $file) {
            $aside = "$file.before-restore-$stamp"
            Move-Item -LiteralPath $file -Destination $aside
            $kept += $aside
        }
    }

    Get-ChildItem -LiteralPath $targets.DatabasePath -Filter "$DatabaseFileName.*.lock" -ErrorAction SilentlyContinue |
        Remove-Item -Force
    Copy-Item -LiteralPath $stagedDatabase -Destination $databaseFile

    # ---------- 5. 資料夾 ----------
    foreach ($folder in $manifest.folders) {
        $target = $targets[$folder.settingKey]
        if (-not $target) {
            Write-Warning "找不到 $($folder.settingKey) 的路徑，略過 $($folder.entry)。"
            continue
        }

        $source = Join-Path $staging ($folder.entry -replace '/', '\')
        if ($folder.settingKey -eq 'DataProtectionKeyPath') {
            # 金鑰合併：只補上沒有的，不覆蓋、不刪除。
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            if (Test-Path -LiteralPath $source) {
                Get-ChildItem -LiteralPath $source -File | ForEach-Object {
                    $destination = Join-Path $target $_.Name
                    if (-not (Test-Path -LiteralPath $destination)) { Copy-Item -LiteralPath $_.FullName -Destination $destination }
                }
            }
            continue
        }

        if (Test-Path -LiteralPath $target) {
            $aside = "$target.before-restore-$stamp"
            Rename-Item -LiteralPath $target -NewName (Split-Path $aside -Leaf)
            $kept += $aside
        }

        $parent = Split-Path $target -Parent
        if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
        if (Test-Path -LiteralPath $source) {
            Move-Item -LiteralPath $source -Destination $target
        }
        else {
            New-Item -ItemType Directory -Force -Path $target | Out-Null
        }
    }

    Write-Host ''
    Write-Host '還原完成。請啟動網站並確認可以登入、資料正確。'
    if ($kept.Count -gt 0) {
        Write-Host '還原前的資料保留在：'
        $kept | ForEach-Object { Write-Host "  $_" }
        Write-Host "要回復到還原前：停站 → 刪除新放入的檔案與資料夾 → 把上列項目去掉「.before-restore-$stamp」改回原名 → 啟動網站。"
    }
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
