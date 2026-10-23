param(
    [Parameter(Mandatory = $true)]
    # 每一段都必須是合法的 C# 識別字：擋掉 Acme..Erp、Acme.、Acme.1x 這類建置才會爆的命名空間。
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)*$')]
    [string]$ProjectName,

    [Parameter(Mandatory = $true)]
    [string]$DestinationPath,

    [string]$SourceProjectName = "CustomerHelpAgent",

    [string]$UserSecretsId = [guid]::NewGuid().ToString(),

    [switch]$Force,

    # 預設會清掉腳手架自己的開發史（docs/changelog 內容），
    # 那些對衍生專案沒有用處。要原封不動整份複製時加這個開關。
    [switch]$KeepStarterHistory,

    # 預設要求來源是「最新」：工作目錄乾淨（沒有未提交修改或未追蹤檔）、在 origin 的預設分支上、不落後 origin。
    # 否則新專案會帶著半成品或舊版內容出生。確定要從這種狀態複刻時才加這個開關。
    [switch]$AllowUnsyncedSource
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$destinationFullPath = [System.IO.Path]::GetFullPath($DestinationPath)

# 衍生專案不帶腳手架的 migration 歷史，最後會清空 Migrations 並以 dotnet ef 重建單一 Init。
# 先檢查 dotnet-ef 是否可用，避免複製到一半才失敗、留下半成品。
$dotnetEfAvailable = $false
try {
    & dotnet ef --version *> $null
    $dotnetEfAvailable = ($LASTEXITCODE -eq 0)
}
catch {
    $dotnetEfAvailable = $false
}

if (-not $dotnetEfAvailable) {
    throw "dotnet-ef is required to create the initial migration. Install it with: dotnet tool install --global dotnet-ef"
}

# 這些目錄可能出現在任何層級（例如 src/CustomerHelpAgent/CustomerHelpAgent.Web/bin），必須遞迴排除。
# artifacts（發佈輸出，可達上百 MB）、.gstack、PublishProfiles（本機發佈設定）都是開發機上的產物，不屬於範本。
$excludedDirectories = @(".git", "bin", "obj", ".vs", ".playwright-cli", "output", "artifacts", ".gstack", "PublishProfiles")
$excludedFilePatterns = @("*.user", "*.suo")
# 個人的 Claude Code 權限設定；以「上層目錄\檔名」比對，避免誤排除其他同名檔案。
$excludedRelativeFiles = @(".claude\settings.local.json")

# 與 Copy-TreeExcluding 同一組規則，改以 repo 相對路徑判斷；供來源檢查找出「被 git 忽略、卻會被複製」的檔案。
function Test-ExcludedRelativePath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $segments = @($RelativePath -split '[\\/]')
    for ($i = 0; $i -lt $segments.Count - 1; $i++) {
        if ($excludedDirectories -contains $segments[$i]) {
            return $true
        }
    }

    $name = $segments[-1]
    foreach ($pattern in $excludedFilePatterns) {
        if ($name -like $pattern) {
            return $true
        }
    }

    return ($segments.Count -ge 2) -and ($excludedRelativeFiles -contains (Join-Path $segments[-2] $name))
}

function Invoke-SourceGit {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    # git 沒裝、不是 repo、沒有網路都只回報失敗，由呼叫端決定要警告還是略過，不讓腳本在這裡中斷。
    try {
        $output = & git -C $repoRoot -c core.quotepath=false @Arguments 2>$null
        $exitCode = $LASTEXITCODE
    }
    catch {
        return [pscustomobject]@{ Success = $false; Lines = @() }
    }

    return [pscustomobject]@{ Success = ($exitCode -eq 0); Lines = @($output | Where-Object { $_ -ne "" }) }
}

function Format-PathList {
    param([Parameter(Mandatory = $true)][string[]]$Lines)

    $listed = @($Lines | Select-Object -First 20 | ForEach-Object { "    $_" })
    if ($Lines.Count -gt 20) {
        $listed += "    ... and $($Lines.Count - 20) more"
    }

    return $listed -join [Environment]::NewLine
}

# 腳本複製的是本機磁碟上的工作目錄，不是 git 的某個版本：未提交的修改、未追蹤檔、
# 落後 origin 的舊內容、開發中的功能分支，都會原樣進入新專案。開跑前先確認來源是最新的。
# 無法判斷時（不是 git repo、沒有 origin、fetch 失敗）只警告：例如從下載的 zip 複刻，本來就沒有 git 可比對。
$sourceProblems = New-Object System.Collections.Generic.List[string]

# git 輸出的中文檔名要以 UTF-8 解讀，否則清單會變亂碼；沒有主控台（輸出被導向）時設定可能失敗，忽略即可。
$previousOutputEncoding = [Console]::OutputEncoding
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
}
catch {
}

try {
    $insideWorkTree = Invoke-SourceGit -Arguments @("rev-parse", "--is-inside-work-tree")
    if (-not $insideWorkTree.Success) {
        Write-Warning "The source is not a git repository (or git is not installed), so it cannot be verified as the latest version: $repoRoot"
    }
    else {
        $remotes = Invoke-SourceGit -Arguments @("remote")
        $hasOrigin = $remotes.Success -and ($remotes.Lines -contains "origin")
        if ($hasOrigin) {
            $fetch = Invoke-SourceGit -Arguments @("fetch", "--quiet", "origin")
            if (-not $fetch.Success) {
                Write-Warning "git fetch origin failed; comparing against the last known state of origin instead."
            }
        }
        else {
            Write-Warning "The source has no 'origin' remote; skipping the default-branch and behind-origin checks."
        }

        $status = Invoke-SourceGit -Arguments @("status", "--porcelain")
        if ($status.Success -and $status.Lines.Count -gt 0) {
            $sourceProblems.Add("Uncommitted changes or untracked files ($($status.Lines.Count)):$([Environment]::NewLine)$(Format-PathList -Lines $status.Lines)")
        }

        if ($hasOrigin) {
            $defaultRef = Invoke-SourceGit -Arguments @("symbolic-ref", "--short", "refs/remotes/origin/HEAD")
            $defaultRemoteBranch = if ($defaultRef.Success -and $defaultRef.Lines.Count -gt 0) { $defaultRef.Lines[0] } else { "origin/main" }
            $defaultBranch = $defaultRemoteBranch -replace '^origin/', ''

            $currentBranch = Invoke-SourceGit -Arguments @("branch", "--show-current")
            $currentBranchName = if ($currentBranch.Success -and $currentBranch.Lines.Count -gt 0) { $currentBranch.Lines[0] } else { "(detached HEAD)" }
            if ($currentBranchName -ne $defaultBranch) {
                $sourceProblems.Add("Not on the default branch: on '$currentBranchName', expected '$defaultBranch'.")
            }

            $behind = Invoke-SourceGit -Arguments @("rev-list", "--count", "HEAD..$defaultRemoteBranch")
            if (-not $behind.Success -or $behind.Lines.Count -eq 0) {
                Write-Warning "Could not compare with $defaultRemoteBranch; skipping the behind-origin check."
            }
            elseif ([int]$behind.Lines[0] -gt 0) {
                $sourceProblems.Add("Behind $defaultRemoteBranch by $($behind.Lines[0]) commit(s).")
            }
        }

        # 被 .gitignore 忽略的檔案 git status 看不到，但不在排除清單裡就一樣會被複製；只列出提醒，不中止。
        $ignored = Invoke-SourceGit -Arguments @("ls-files", "--others", "--ignored", "--exclude-standard")
        if ($ignored.Success) {
            $copiedIgnored = @($ignored.Lines | Where-Object { -not (Test-ExcludedRelativePath -RelativePath $_) })
            if ($copiedIgnored.Count -gt 0) {
                Write-Warning "These files are ignored by git but will still be copied into the new project ($($copiedIgnored.Count)); delete them first if they are local leftovers:$([Environment]::NewLine)$(Format-PathList -Lines $copiedIgnored)"
            }
        }
    }
}
finally {
    try {
        [Console]::OutputEncoding = $previousOutputEncoding
    }
    catch {
    }
}

if ($sourceProblems.Count -gt 0) {
    # 明細用警告逐行印出：throw 的訊息在預設錯誤檢視裡會被擠成一行，清單就看不清楚了。
    Write-Warning "The source is not the latest version, so the new project would not get the latest files and docs:"
    $sourceProblems | ForEach-Object { Write-Warning "  - $_" }

    if ($AllowUnsyncedSource) {
        Write-Warning "Continuing because -AllowUnsyncedSource was given."
    }
    else {
        throw "Source is not the latest version (see the warnings above). Switch to the default branch, git pull, and commit or stash local changes; or rerun with -AllowUnsyncedSource to copy the working directory as it is."
    }
}

# 每個衍生專案都要有自己的開發連接埠，否則同一台機器同時開兩個專案會搶埠；
# Google OAuth 的 redirect URI 也是依埠註冊。範圍比照 VS／dotnet new（http 5000–5300、https 7000–7300）。
# 來源埠從來源的 launchSettings.json 讀，不寫死：從衍生專案再衍生時，來源埠已經不是腳手架的 5109／7144，
# 寫死會讓新專案沿用上一代的埠，而且殘留掃描也找不到。
$sourceLaunchSettings = Join-Path $repoRoot "src/$SourceProjectName/$SourceProjectName.Web/Properties/launchSettings.json"
if (-not (Test-Path -LiteralPath $sourceLaunchSettings)) {
    throw "Could not find the source launchSettings.json to read the development ports: $sourceLaunchSettings"
}

$sourceLaunchContent = [System.IO.File]::ReadAllText($sourceLaunchSettings)
$sourceHttpMatch = [regex]::Match($sourceLaunchContent, 'http://localhost:(\d+)')
$sourceHttpsMatch = [regex]::Match($sourceLaunchContent, 'https://localhost:(\d+)')
if (-not $sourceHttpMatch.Success -or -not $sourceHttpsMatch.Success) {
    throw "Could not read http/https localhost ports from applicationUrl in: $sourceLaunchSettings"
}

$sourceHttpPort = [int]$sourceHttpMatch.Groups[1].Value
$sourceHttpsPort = [int]$sourceHttpsMatch.Groups[1].Value

function Get-FreeDevPort {
    param(
        [Parameter(Mandatory = $true)][int]$Minimum,
        [Parameter(Mandatory = $true)][int]$Maximum,
        [Parameter(Mandatory = $true)][int]$Exclude
    )

    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        $candidate = Get-Random -Minimum $Minimum -Maximum ($Maximum + 1)
        if ($candidate -eq $Exclude) {
            continue
        }

        # 用實際試綁判斷：同時涵蓋「已被占用」與 Windows（Hyper-V）保留的排除埠範圍。
        $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $candidate)
        try {
            $listener.Start()
            return $candidate
        }
        catch {
            continue
        }
        finally {
            $listener.Stop()
        }
    }

    throw "Could not find a free port between $Minimum and $Maximum."
}

$httpPort = Get-FreeDevPort -Minimum 5000 -Maximum 5300 -Exclude $sourceHttpPort
$httpsPort = Get-FreeDevPort -Minimum 7000 -Maximum 7300 -Exclude $sourceHttpsPort

if ((Test-Path -LiteralPath $destinationFullPath) -and -not $Force) {
    throw "Destination already exists. Use -Force to overwrite: $destinationFullPath"
}

if (Test-Path -LiteralPath $destinationFullPath) {
    Remove-Item -LiteralPath $destinationFullPath -Recurse -Force
}

function Copy-TreeExcluding {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$TargetDirectory
    )

    if (-not (Test-Path -LiteralPath $TargetDirectory)) {
        New-Item -ItemType Directory -Path $TargetDirectory | Out-Null
    }

    foreach ($item in Get-ChildItem -LiteralPath $SourceDirectory -Force) {
        if ($item.PSIsContainer) {
            if ($excludedDirectories -contains $item.Name) {
                continue
            }

            Copy-TreeExcluding -SourceDirectory $item.FullName -TargetDirectory (Join-Path $TargetDirectory $item.Name)
            continue
        }

        $isExcludedFile = $false
        foreach ($pattern in $excludedFilePatterns) {
            if ($item.Name -like $pattern) {
                $isExcludedFile = $true
                break
            }
        }

        if (-not $isExcludedFile -and ($excludedRelativeFiles -contains (Join-Path $item.Directory.Name $item.Name))) {
            $isExcludedFile = $true
        }

        if ($isExcludedFile) {
            continue
        }

        Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $TargetDirectory $item.Name) -Force
    }
}

Copy-TreeExcluding -SourceDirectory $repoRoot -TargetDirectory $destinationFullPath

# 排除後可能留下空資料夾（例如已移除的 SqlServerMigrations 專案只剩 bin/obj），由深到淺清掉。
Get-ChildItem -LiteralPath $destinationFullPath -Recurse -Directory -Force |
    Sort-Object { $_.FullName.Length } -Descending |
    Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

# .gitignore 的 Extension 就是 ".gitignore"；它的 !src/CustomerHelpAgent/CustomerHelpAgent.Web/Backup/ 例外不換名，
# 衍生專案的 Backup/ 程式碼會被 Backup*/ 忽略而提交不進 git，乾淨 clone 建置失敗。
$textExtensions = @(
    ".cs", ".csproj", ".slnx", ".json", ".md", ".razor", ".css", ".js",
    ".ps1", ".yml", ".yaml", ".config", ".xml", ".tmpl", ".gitignore", ".props"
)

# 每個衍生專案都必須擁有自己的 UserSecretsId，否則會共用同一份 secrets.json 互相污染。
# 除了 csproj，文件裡的路徑範例也要一起換掉，否則會把開發者導回原腳手架的 secrets 目錄。
$sourceUserSecretsId = "93aff4dd-e822-4569-ac29-89f53416029e"

# 衍生專案有自己的版本線：沿用腳手架的 0.9.x 會讓「關於」視窗與文件頭都指向別人的歷史。
# 日期一定要用 InvariantCulture —— 版本格式固定為 Major.Minor.Patch (YYYY/MM/DD)，
# 某些文化的日期分隔符不是 /。
$initialVersion = "1.0.0"
$initialVersionDate = (Get-Date).ToString('yyyy/MM/dd', [System.Globalization.CultureInfo]::InvariantCulture)
$systemVersionPattern = '"SystemVersion"\s*:\s*"[^"]*"'

function Test-Utf8Bom {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $head = New-Object byte[] 3
        $read = $stream.Read($head, 0, 3)
        return ($read -eq 3 -and $head[0] -eq 0xEF -and $head[1] -eq 0xBB -and $head[2] -eq 0xBF)
    }
    finally {
        $stream.Dispose()
    }
}

function Test-StarterHistoryLink {
    param(
        [Parameter(Mandatory = $true)][string]$FileDirectory,
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$DocsRoot,
        [Parameter(Mandatory = $true)][string[]]$HistoryDirectories
    )

    # 只處理指向被清理目錄、而且真的已經不存在的連結；
    # 其他死連結（若原本就有）不在本次職責範圍，誤改反而難追。
    if ($Target -match '^[A-Za-z][A-Za-z0-9+.-]*:') {
        return $false
    }

    $relativePath = $Target.Split('#')[0]
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        return $false
    }

    $relativePath = [uri]::UnescapeDataString($relativePath)
    try {
        $full = [System.IO.Path]::GetFullPath((Join-Path $FileDirectory $relativePath))
    }
    catch {
        return $false
    }

    $inHistory = $false
    foreach ($name in $HistoryDirectories) {
        $historyPath = [System.IO.Path]::GetFullPath((Join-Path $DocsRoot $name))
        if ($full -eq $historyPath -or $full.StartsWith($historyPath + [System.IO.Path]::DirectorySeparatorChar)) {
            $inHistory = $true
            break
        }
    }

    if (-not $inHistory) {
        return $false
    }

    return -not (Test-Path -LiteralPath $full)
}

Get-ChildItem -LiteralPath $destinationFullPath -Recurse -File |
    Where-Object { $textExtensions -contains $_.Extension } |
    ForEach-Object {
        # 逐檔保留原本的 BOM 狀態：docs/**/*.md 必須維持 UTF-8 含 BOM，
        # 否則複製出來的專案會直接卡在 scripts/Test-DocsEncoding.ps1。
        # 注意：Set-Content -Encoding utf8 在 PowerShell 7 是「不含 BOM」。
        $hasBom = Test-Utf8Bom -Path $_.FullName
        $content = [System.IO.File]::ReadAllText($_.FullName)
        $content = $content.Replace($SourceProjectName, $ProjectName)
        # 全小寫的形式（例如文件裡的範例信箱 customerhelpagent.noreply@gmail.com）另外換成新名稱的小寫；String.Replace 區分大小寫。
        $content = $content.Replace($SourceProjectName.ToLowerInvariant(), $ProjectName.ToLowerInvariant())
        $content = $content.Replace($sourceUserSecretsId, $UserSecretsId)
        # launchSettings.json 與文件裡的網址一起換；只換帶 localhost: 的形式，避免誤傷其他數字。
        $content = $content.Replace("localhost:$sourceHttpsPort", "localhost:$httpsPort")
        $content = $content.Replace("localhost:$sourceHttpPort", "localhost:$httpPort")
        if ($_.Name -eq "appsettings.json") {
            # 註：這裡刻意**不**動 BootstrapSettings:SupportPassword。
            # 換成另一個固定佔位值並不會比較安全，只是把弱值換成另一個弱值；
            # 真正的防線是 StartupSafetyValidator —— 本機開發仍可用文件記載的預設帳密登入，
            # Production 則會因為「仍是範本預設密碼」被擋下啟動。
            $content = $content.Replace("DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars", "$ProjectName-ChangeThisJwtSigningKey-AtLeast32Chars")
            $content = [regex]::Replace($content, $systemVersionPattern, "`"SystemVersion`": `"$initialVersion ($initialVersionDate)`"")
        }
        if ($_.Extension -eq ".md") {
            # 文件頭的「現行系統版本」跟著新版本線走；「首次實作版本」是功能的歷史，保留原值。
            # ⚠️ 用 [^\r\n]* 而非 .*$：.NET 的 . 會吃掉 CRLF 的 \r，把行尾弄壞。
            $content = [regex]::Replace($content, '(?m)^- 現行系統版本：[^\r\n]*', "- 現行系統版本：$initialVersion")
        }
        [System.IO.File]::WriteAllText($_.FullName, $content, (New-Object System.Text.UTF8Encoding($hasBom)))
    }

Get-ChildItem -LiteralPath $destinationFullPath -Recurse -Directory |
    Sort-Object FullName -Descending |
    Where-Object { $_.Name.Contains($SourceProjectName) } |
    ForEach-Object {
        $newName = $_.Name.Replace($SourceProjectName, $ProjectName)
        Rename-Item -LiteralPath $_.FullName -NewName $newName
    }

Get-ChildItem -LiteralPath $destinationFullPath -Recurse -File |
    Where-Object { $_.Name.Contains($SourceProjectName) } |
    ForEach-Object {
        $newName = $_.Name.Replace($SourceProjectName, $ProjectName)
        Rename-Item -LiteralPath $_.FullName -NewName $newName
    }

# csproj 是 UserSecretsId 的權威來源：即使有人改過腳手架的預設值（上面的字串取代因此沒命中），
# 這段 regex 也一定會把新的 Id 寫進去。
$webCsproj = Get-ChildItem -LiteralPath $destinationFullPath -Recurse -File -Filter "$ProjectName.Web.csproj" |
    Select-Object -First 1

if ($webCsproj) {
    $hasBom = Test-Utf8Bom -Path $webCsproj.FullName
    $content = [System.IO.File]::ReadAllText($webCsproj.FullName)
    $content = [regex]::Replace($content, '<UserSecretsId>[^<]*</UserSecretsId>', "<UserSecretsId>$UserSecretsId</UserSecretsId>")
    [System.IO.File]::WriteAllText($webCsproj.FullName, $content, (New-Object System.Text.UTF8Encoding($hasBom)))
    Write-Host "UserSecretsId set to $UserSecretsId in $($webCsproj.Name)"
}
else {
    Write-Warning "Could not locate $ProjectName.Web.csproj; UserSecretsId was not replaced."
}

$webAppSettings = Join-Path $destinationFullPath "src/$ProjectName/$ProjectName.Web/appsettings.json"
if ((Test-Path -LiteralPath $webAppSettings) -and ([System.IO.File]::ReadAllText($webAppSettings).Contains("`"$initialVersion ($initialVersionDate)`""))) {
    Write-Host "SystemVersion reset to $initialVersion ($initialVersionDate) in $ProjectName.Web/appsettings.json"
}
else {
    Write-Warning "Could not locate SystemVersion in $ProjectName.Web/appsettings.json; set SystemSettings:SystemInformation:SystemVersion manually."
}

# 清空腳手架的 migration 歷史，改由新專案自己的第一次 migration（Init）起算。
# 名稱用大寫 Init：全小寫類別名會觸發 CS8981，在 TreatWarningsAsErrors 下直接建置失敗。
# 目錄本身保留：AccessDatas.csproj 有 <Folder Include="Migrations\" />。
$projectRoot = Join-Path $destinationFullPath "src/$ProjectName"
$accessDatasCsproj = Join-Path $projectRoot "$ProjectName.AccessDatas/$ProjectName.AccessDatas.csproj"
$webCsprojPath = Join-Path $projectRoot "$ProjectName.Web/$ProjectName.Web.csproj"
$migrationsDirectory = Join-Path $projectRoot "$ProjectName.AccessDatas/Migrations"

if (Test-Path -LiteralPath $migrationsDirectory) {
    Get-ChildItem -LiteralPath $migrationsDirectory -Force | Remove-Item -Recurse -Force
}

# 這些測試寫死了腳手架的舊 migration 名稱（驗證腳手架自己的升級路徑），重建後在新專案必定失效。
# 新增這類測試時請獨立成檔並加進這份清單，否則衍生專案一複刻就有測試失敗。
$starterMigrationTests = @("CategoryTeamUniqueIndexMigrationTests.cs", "TeamTreeMigrationTests.cs")
foreach ($testFile in $starterMigrationTests) {
    $starterMigrationTest = Join-Path $projectRoot "$ProjectName.Tests/$testFile"
    if (Test-Path -LiteralPath $starterMigrationTest) {
        Remove-Item -LiteralPath $starterMigrationTest -Force
    }
}

# dotnet ef 取專案中繼資料前需要 project.assets.json，全新複製的專案必須先還原套件。
$solutionFile = Join-Path $projectRoot "$ProjectName.slnx"
& dotnet restore $solutionFile
if ($LASTEXITCODE -ne 0) {
    throw "Failed to restore packages. Fix the error above, then run manually: dotnet restore `"$solutionFile`""
}

$migrationCommand = "dotnet ef migrations add Init --project `"$accessDatasCsproj`" --startup-project `"$webCsprojPath`""
Write-Host "Creating initial migration: $migrationCommand"
& dotnet ef migrations add Init --project $accessDatasCsproj --startup-project $webCsprojPath
if ($LASTEXITCODE -ne 0) {
    throw "Failed to create the initial migration. Fix the error above, then run manually: $migrationCommand"
}

Write-Host "Cleared starter migrations and created the initial migration 'Init'."

if ($KeepStarterHistory) {
    Write-Host "Kept the starter's own history docs (-KeepStarterHistory)."
}
else {
    # 只清內容、保留 README.md：docs/operations/維護規範.md 要求每一次異動寫一篇 changelog，
    # 新專案要從自己的第一篇開始，所以目錄與索引骨架必須留著。
    $emptiedDocDirectories = @("changelog")
    $historyDirectories = $emptiedDocDirectories

    $docsRoot = Join-Path $destinationFullPath "docs"
    $removedFiles = 0

    foreach ($name in $emptiedDocDirectories) {
        $path = Join-Path $docsRoot $name
        if (Test-Path -LiteralPath $path) {
            Get-ChildItem -LiteralPath $path -Recurse -File |
                Where-Object { $_.Name -ne "README.md" } |
                ForEach-Object {
                    Remove-Item -LiteralPath $_.FullName -Force
                    $removedFiles++
                }
        }
    }

    # 清完之後，索引與內文裡會留下指向已刪檔案的連結。一條通則處理兩種情況：
    # 整行只是一條索引項 -> 刪整行；夾在敘述句裡 -> 降級為純文字，句子仍然讀得通。
    $linkPattern = '\[(?<text>[^\]]*)\]\((?<target>[^)\s]+)\)'
    $removedLines = 0
    $downgradedLinks = 0

    $markdownFiles = @(Get-ChildItem -LiteralPath $docsRoot -Recurse -File -Filter "*.md")
    $rootReadme = Join-Path $destinationFullPath "readme.md"
    if (Test-Path -LiteralPath $rootReadme) {
        $markdownFiles += Get-Item -LiteralPath $rootReadme
    }

    foreach ($file in $markdownFiles) {
        $hasBom = Test-Utf8Bom -Path $file.FullName
        $original = [System.IO.File]::ReadAllText($file.FullName)
        $newline = if ($original.Contains("`r`n")) { "`r`n" } else { "`n" }

        $kept = New-Object System.Collections.Generic.List[string]

        foreach ($line in ($original -split "`r`n|`n")) {
            $links = [regex]::Matches($line, $linkPattern)
            $stale = @($links | Where-Object {
                Test-StarterHistoryLink -FileDirectory $file.Directory.FullName `
                    -Target $_.Groups['target'].Value `
                    -DocsRoot $docsRoot -HistoryDirectories $historyDirectories
            })

            if ($stale.Count -eq 0) {
                $kept.Add($line)
                continue
            }

            $trimmed = $line.TrimStart()
            $isIndexRow = $stale.Count -eq $links.Count -and
                ($trimmed.StartsWith("- ") -or $trimmed.StartsWith("* ") -or $trimmed.StartsWith("|"))

            if ($isIndexRow) {
                $removedLines++
                continue
            }

            $updated = $line
            foreach ($link in $stale) {
                $updated = $updated.Replace($link.Value, $link.Groups['text'].Value)
                $downgradedLinks++
            }

            $kept.Add($updated)
        }

        $result = $kept -join $newline
        if ($result -ne $original) {
            [System.IO.File]::WriteAllText($file.FullName, $result, (New-Object System.Text.UTF8Encoding($hasBom)))
        }
    }

    Write-Host "Pruned $removedFiles starter history doc files (docs/changelog entries)."
    Write-Host "Removed $removedLines stale index lines and downgraded $downgradedLinks stale links. Use -KeepStarterHistory to keep them."
}

$remainingMatches = Get-ChildItem -LiteralPath $destinationFullPath -Recurse -File |
    Where-Object { $textExtensions -contains $_.Extension } |
    Select-String -Pattern $SourceProjectName, "DevelopmentOnly-ChangeThisJwtSigningKey", $sourceUserSecretsId, "localhost:$sourceHttpPort", "localhost:$sourceHttpsPort" -SimpleMatch

if ($remainingMatches) {
    Write-Warning "Scaffold completed, but safety checks found values that still need review:"
    $remainingMatches | ForEach-Object {
        Write-Warning "$($_.Path):$($_.LineNumber): $($_.Line.Trim())"
    }
}

Write-Host "Created starter project at $destinationFullPath"
Write-Host "Dev URLs: https://localhost:$httpsPort / http://localhost:$httpPort (Properties/launchSettings.json)"
Write-Host "Google OAuth redirect URIs to register: https://localhost:$httpsPort/signin-google , http://localhost:$httpPort/signin-google"
Write-Host "Next: see docs/guides/VS Code 開發環境與新專案上手指南.md - section 7 (branding: favicon, brand image, product name/description) and sections 8.1 / 8.3 for the manual follow-up items."
