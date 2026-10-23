<#
.SYNOPSIS
    一行指令產生一個符合本專案現行慣例的 CRUD 模組，並完成所有登記與 migration（0.9.110 起）。

.DESCRIPTION
    產生的模組包含：實體（軟刪除＋樂觀並行）、畫面模型、DTO、Repository（Web API）、資料服務（Blazor）、
    Web API（GET／search／POST／PUT／DELETE，各自 [HasPermission]）、頁面與檢視（權限閘門、依動作顯示按鈕、
    搜尋排序分頁、顯示已刪除／還原／永久刪除、匯出 Excel、稽核）、操作說明、服務測試。
    -WithTeams 另加「團隊」欄位與團隊範圍（RecordTeamScope）。

    並自動登記（重複執行不會重複插入，已存在就略過並列出）：
      DbSet 與名稱唯一索引、AutoMapper、DI、權限鍵常數、稽核動作、Menu.json（下一個可用 id）、選單權限對照、
      角色權限矩陣（或管理員專屬清單）、選單／權限一致性測試、操作說明目錄、_Imports.razor、
      已刪除資料清理（服務與作業）、並行／軟刪除實體清單、服務生命週期／畫面模型 Clone／API DI 測試清單、選單圖示允許清單。

    樣板在 scripts/crud-templates/（行首 #IF TEAMS／#IF ADMIN／#ELSE／#ENDIF 控制條件區塊）。
    ⚠️ 改了專案慣例就要同步改樣板，並跑 scripts/Test-CrudGenerator.ps1 確認產出的模組能建置、全部測試通過。

.PARAMETER Name
    實體名稱，PascalCase（例如 Equipment）。

.PARAMETER DisplayName
    顯示名稱，同時是權限鍵（例如 設備清單）。預設同 Name。

.PARAMETER Plural
    頁面與檢視的資料夾／命名空間，預設 <Name>s。

.PARAMETER Route
    頁面路由，預設 /<plural 小寫>。

.PARAMETER MenuGroupId
    掛在 Menu.json 的哪一個群組底下，預設 5（資料定義）。非管理員專屬頁面只能是 2（專案管理）或 5。
    管理員專屬頁面掛在「系統管理」的子群組：34（帳號與權限）、6（監控與診斷）、7（AI 管理）、8（系統設定與維運）；
    不可直接掛在 3（系統管理）—— 那一層只放子群組。

.PARAMETER Icon
    選單圖示（classic Material Icons 名稱），預設 description；不在 MenuIconTests 允許清單時自動加入。

.PARAMETER WithTeams
    加上「團隊」欄位與團隊範圍。

.PARAMETER AdminOnly
    管理員專屬：權限鍵不進角色矩陣，檢視以 CheckIsAdmin 判斷。

.PARAMETER SkipMigration
    不自動產生 migration（之後自己跑 dotnet ef migrations add Add<Name>）。

.PARAMETER Preview
    只把產出的檔案寫到 -OutputPath 底下，不改專案、不登記、不產生 migration。

.PARAMETER Force
    略過「工作目錄必須乾淨」的檢查；之前由本產生器寫出的檔案保留不覆蓋（用來重跑登記）。

.EXAMPLE
    ./scripts/New-CrudModule.ps1 -Name Equipment -DisplayName 設備清單

.EXAMPLE
    ./scripts/New-CrudModule.ps1 -Name Vendor -DisplayName 廠商清單 -WithTeams -Icon storefront
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Z][A-Za-z0-9]*$')]
    [string]$Name,

    [string]$DisplayName,

    [ValidatePattern('^[A-Z][A-Za-z0-9]*$')]
    [string]$Plural,

    [ValidatePattern('^/[a-z0-9][a-z0-9-/]*$')]
    [string]$Route,

    [int]$MenuGroupId = 5,

    [ValidatePattern('^[a-z0-9_]+$')]
    [string]$Icon = 'description',

    [switch]$WithTeams,

    [switch]$AdminOnly,

    [switch]$SkipMigration,

    [switch]$Preview,

    [string]$OutputPath = 'output/crud-modules',

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$GeneratedMarker = 'scripts/New-CrudModule.ps1'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$src = Join-Path $repoRoot 'src/CustomerHelpAgent'
$templateRoot = Join-Path $PSScriptRoot 'crud-templates'

#region 名稱與參數
if (-not $DisplayName) { $DisplayName = $Name }
if ($DisplayName -notmatch '^[\p{L}\p{Nd} ]+$') {
    throw "DisplayName 只能包含文字、數字與空白：$DisplayName"
}
if (-not $Plural) { $Plural = "${Name}s" }
if (-not $Route) { $Route = '/' + $Plural.ToLowerInvariant() }
$camel = $Name.Substring(0, 1).ToLowerInvariant() + $Name.Substring(1)
$permConst = '角色_' + ($DisplayName -replace ' ', '')
$routeKey = $Route.Trim('/').Replace('/', '-').ToLowerInvariant()
$item = $DisplayName -replace '清單$', ''
if (-not $item) { $item = $DisplayName }

$menuPath = Join-Path $src 'CustomerHelpAgent.Web/Datas/Menu.json'
$menu = Get-Content -LiteralPath $menuPath -Raw -Encoding utf8 | ConvertFrom-Json
function Find-MenuNode($nodes, [int]$id) {
    foreach ($node in $nodes) {
        if ($node.id -eq $id) { return $node }
        if ($node.PSObject.Properties.Name -contains 'subMenu') {
            $found = Find-MenuNode $node.subMenu $id
            if ($found) { return $found }
        }
    }
    return $null
}
function Get-MenuIds($nodes) {
    foreach ($node in $nodes) {
        $node.id
        if ($node.PSObject.Properties.Name -contains 'subMenu') { Get-MenuIds $node.subMenu }
    }
}
$group = Find-MenuNode $menu $MenuGroupId
if (-not $group -or -not ($group.PSObject.Properties.Name -contains 'subMenu')) {
    throw "Menu.json 找不到 id 為 $MenuGroupId 的群組（必須是有 subMenu 的節點）。"
}
if ($MenuGroupId -eq 3) {
    # 系統管理底下只放子群組（0.9.113 起）；直接掛在 3 會在子群組之間多出一個散落的項目。
    $subGroups = @($group.subMenu | Where-Object { $_.PSObject.Properties.Name -contains 'subMenu' } |
        ForEach-Object { "$($_.id)（$($_.name)）" }) -join '、'
    throw "不可直接掛在群組 3（系統管理），那一層只放子群組。請改用其中一個子群組：$subGroups。"
}
if (-not $AdminOnly -and $MenuGroupId -notin 2, 5) {
    throw "非管理員專屬的頁面只能掛在群組 2（專案管理）或 5（資料定義），角色權限矩陣只有這兩組；管理員專屬請加 -AdminOnly。"
}
$menuId = ((@(Get-MenuIds $menu) | Measure-Object -Maximum).Maximum) + 1
$menuPathText = "$($group.name) › $DisplayName"

$flags = @{ TEAMS = [bool]$WithTeams; ADMIN = [bool]$AdminOnly }
# 區分大小寫（__Name__ 與 __name__ 是不同的記號；PowerShell 的 @{} 不分大小寫）。
$tokens = [System.Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
$tokens['__PermConst__'] = $permConst
$tokens['__RouteKey__'] = $routeKey
$tokens['__MenuPath__'] = $menuPathText
$tokens['__Display__'] = $DisplayName
$tokens['__Plural__'] = $Plural
$tokens['__Route__'] = $Route
$tokens['__Item__'] = $item
$tokens['__Name__'] = $Name
$tokens['__name__'] = $camel
#endregion

#region 樣板展開
function Expand-Template([string]$text) {
    $out = [System.Collections.Generic.List[string]]::new()
    $stack = [System.Collections.Generic.Stack[object]]::new()
    $emit = $true
    foreach ($line in ($text -split "`r?`n")) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^#IF (!?)([A-Z]+)$') {
            $condition = [bool]$flags[$Matches[2]]
            if ($Matches[1]) { $condition = -not $condition }
            $stack.Push([pscustomobject]@{ Parent = $emit; Condition = $condition })
            $emit = $emit -and $condition
            continue
        }
        if ($trimmed -eq '#ELSE') {
            $top = $stack.Peek()
            $emit = $top.Parent -and (-not $top.Condition)
            continue
        }
        if ($trimmed -eq '#ENDIF') {
            $emit = $stack.Pop().Parent
            continue
        }
        if ($emit) { $out.Add($line) }
    }
    if ($stack.Count -ne 0) { throw '樣板的 #IF／#ENDIF 不成對。' }

    $result = $out -join "`n"
    foreach ($key in $tokens.Keys) { $result = $result.Replace($key, $tokens[$key]) }
    return $result
}

$projectFolders = @{
    AccessDatas = 'CustomerHelpAgent.AccessDatas'; Models = 'CustomerHelpAgent.Models'; Dtos = 'CustomerHelpAgent.Dtos'
    Business = 'CustomerHelpAgent.Business'; Web = 'CustomerHelpAgent.Web'; Tests = 'CustomerHelpAgent.Tests'
}

$outputs = foreach ($template in Get-ChildItem -LiteralPath $templateRoot -Recurse -File -Filter '*.tmpl') {
    $relative = [IO.Path]::GetRelativePath($templateRoot, $template.FullName).Replace('\', '/')
    $relative = $relative.Substring(0, $relative.Length - '.tmpl'.Length)
    foreach ($key in $tokens.Keys) { $relative = $relative.Replace($key, $tokens[$key]) }
    $first, $rest = $relative.Split('/', 2)
    $target = if ($Preview) {
        Join-Path (Join-Path (Join-Path $repoRoot $OutputPath) $Name) $relative
    } else {
        Join-Path (Join-Path $src $projectFolders[$first]) $rest
    }
    [pscustomobject]@{ Template = $template.FullName; Target = $target; Relative = $relative }
}
#endregion

#region 檢查
if (-not $Preview) {
    if (-not $Force) {
        $dirty = git -C $repoRoot status --porcelain
        if ($dirty) {
            throw '工作目錄有未提交的變更。請先提交或暫存，讓產生器的異動可以單獨檢視；確定要繼續請加 -Force。'
        }
    }

    foreach ($output in $outputs) {
        if (Test-Path -LiteralPath $output.Target) {
            $existing = Get-Content -LiteralPath $output.Target -Raw -Encoding utf8
            if (-not $existing.Contains($GeneratedMarker)) {
                throw "目標檔案已存在且不是本產生器寫的：$($output.Target)"
            }
            if (-not $Force) {
                throw "目標檔案已存在：$($output.Target)（重跑登記請加 -Force，已產生的檔案會保留不覆蓋）"
            }
        }
    }

    $magic = Get-Content -LiteralPath (Join-Path $src 'CustomerHelpAgent.Share/Helpers/MagicObjectHelper.cs') -Raw -Encoding utf8
    if (-not $magic.Contains("public const string $permConst ") -and $magic.Contains("= `"$DisplayName`";")) {
        throw "權限鍵「$DisplayName」已被其他頁面使用，請換一個 DisplayName。"
    }
    if (-not $Force -and (Get-Content -LiteralPath $menuPath -Raw -Encoding utf8).Contains("`"url`": `"$Route`"")) {
        throw "Menu.json 已有路由 $Route。"
    }
}
#endregion

#region 寫檔
$written = [System.Collections.Generic.List[string]]::new()
$kept = [System.Collections.Generic.List[string]]::new()
foreach ($output in $outputs) {
    if (-not $Preview -and (Test-Path -LiteralPath $output.Target)) {
        $kept.Add($output.Relative)
        continue
    }
    $content = Expand-Template (Get-Content -LiteralPath $output.Template -Raw -Encoding utf8)
    $directory = Split-Path -Parent $output.Target
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    # 原始碼 UTF-8 無 BOM；操作說明（Datas/Help/*.md）需要 BOM（PageHelpCatalogTests）。
    $bom = $output.Target.EndsWith('.md')
    [IO.File]::WriteAllText($output.Target, $content.TrimEnd("`n") + "`n", [Text.UTF8Encoding]::new($bom))
    $written.Add($output.Relative)
}

if ($Preview) {
    Write-Host "已產生 $($written.Count) 個檔案到 $(Join-Path $OutputPath $Name)（預覽模式：沒有改專案、沒有登記、沒有 migration）。"
    return
}
#endregion

#region 登記
$report = [System.Collections.Generic.List[string]]::new()

function Update-Source([string]$relativePath, [string]$label, [string]$marker, [scriptblock]$transform) {
    $path = Join-Path $src $relativePath
    $bytes = [IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $original = [Text.UTF8Encoding]::new($false).GetString($bytes, $(if ($hasBom) { 3 } else { 0 }), $bytes.Length - $(if ($hasBom) { 3 } else { 0 }))
    $crlf = $original.Contains("`r`n")
    $text = $original.Replace("`r`n", "`n")
    if ($text.Contains($marker)) {
        $report.Add("略過（已存在）：$label")
        return
    }
    $updated = & $transform $text
    if ($updated -eq $text) { throw "找不到登記位置：$label（$relativePath）。樣板或錨點需要更新。" }
    if ($crlf) { $updated = $updated.Replace("`n", "`r`n") }
    [IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($hasBom))
    $report.Add("已登記：$label")
}

function Insert-After([string]$text, [string]$anchor, [string]$snippet) {
    $index = $text.IndexOf($anchor, [StringComparison]::Ordinal)
    if ($index -lt 0) { return $text }
    return $text.Insert($index + $anchor.Length, $snippet)
}

function Insert-Before([string]$text, [string]$anchor, [string]$snippet) {
    $index = $text.IndexOf($anchor, [StringComparison]::Ordinal)
    if ($index -lt 0) { return $text }
    return $text.Insert($index, $snippet)
}

function Add-SortedName([string]$text, [string]$entity) {
    $pattern = 'new\[\] \{ (nameof\(\w+\)(?:, nameof\(\w+\))*) \}'
    $match = [regex]::Match($text, $pattern)
    if (-not $match.Success) { return $text }
    $names = [regex]::Matches($match.Groups[1].Value, 'nameof\((\w+)\)') | ForEach-Object { $_.Groups[1].Value }
    $sorted = [string[]](@($names) + $entity)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    $replacement = 'new[] { ' + (($sorted | ForEach-Object { "nameof($_)" }) -join ', ') + ' }'
    return $text.Remove($match.Index, $match.Length).Insert($match.Index, $replacement)
}

Update-Source 'CustomerHelpAgent.AccessDatas/BackendDBContext.cs' 'DbSet' "DbSet<$Name> $Name " {
    param($t)
    $last = [regex]::Matches($t, '(?m)^    public virtual DbSet<[^>]+> \w+ \{ get; set; \}\n') | Select-Object -Last 1
    $t.Insert($last.Index + $last.Length, "    public virtual DbSet<$Name> $Name { get; set; }`n")
}
Update-Source 'CustomerHelpAgent.AccessDatas/BackendDBContext.cs' '名稱唯一索引' "modelBuilder.Entity<$Name>(" {
    param($t)
    Insert-Before $t "        OnModelCreatingPartial(modelBuilder);" @"
        #region $DisplayName（$GeneratedMarker 產生）：名稱唯一（只約束未刪除的資料）
        modelBuilder.Entity<$Name>(entity =>
        {
            entity.HasIndex(x => x.Name).IsUnique().HasFilter(ActiveRowsOnly);
        });
        #endregion


"@
}

$teamsMapIn = if ($WithTeams) { "`n            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToList(s.Teams)))" } else { '' }
$teamsMapOut = if ($WithTeams) { "`n            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToStored(s.Teams)))" } else { '' }
Update-Source 'CustomerHelpAgent.Business/Models/AutoMapping.cs' 'AutoMapper' "CreateMap<$Name, ${Name}AdapterModel>" {
    param($t)
    Insert-Before $t "        #region TokenUsageLog" @"
        #region $Name（$GeneratedMarker 產生）
        CreateMap<$Name, ${Name}AdapterModel>()$teamsMapIn;
        CreateMap<${Name}AdapterModel, $Name>().IgnoreSoftDeleteFields()
            .ForMember(d => d.Name, o => o.MapFrom(s => NameNormalizer.Normalize(s.Name)))$teamsMapOut;
        CreateMap<$Name, ${Name}Dto>();
        CreateMap<$Name, ${Name}CreateUpdateDto>();
        CreateMap<${Name}CreateUpdateDto, $Name>().IgnoreSoftDeleteFields()
            .ForMember(d => d.Name, o => o.MapFrom(s => NameNormalizer.Normalize(s.Name)));
        #endregion


"@
}

Update-Source 'CustomerHelpAgent.Web/Extensions/ServiceCollectionExtensions.cs' 'DI' "AddScoped<${Name}Service>" {
    param($t)
    Insert-After $t "        services.AddScoped<CategoryRepository>();`n" "        services.AddScoped<${Name}Service>();`n        services.AddScoped<${Name}Repository>();`n"
}

$constDoc = if ($AdminOnly) { "`n    /// <inheritdoc cref=`"角色_系統管理`"/>`n" } else { '' }
Update-Source 'CustomerHelpAgent.Share/Helpers/MagicObjectHelper.cs' '權限鍵常數' "public const string $permConst " {
    param($t)
    Insert-After $t "    public const string 角色_團隊清單 = `"團隊清單`";`n" "$constDoc    public const string $permConst = `"$DisplayName`";`n"
}

Update-Source 'CustomerHelpAgent.Business/Helpers/AuditActions.cs' '稽核動作' "        public const string Create = `"$Name.Create`";" {
    param($t)
    Insert-Before $t "    public static class Team`n" @"
    /// <summary>$DisplayName（$GeneratedMarker 產生）。刪除為軟刪除；AutoPurge 由排程作業「已刪除資料清理」寫入。</summary>
    public static class $Name
    {
        public const string Create = "$Name.Create";
        public const string Update = "$Name.Update";
        public const string Enable = "$Name.Enable";
        public const string Disable = "$Name.Disable";
        public const string Delete = "$Name.Delete";
        public const string Restore = "$Name.Restore";
        public const string Purge = "$Name.Purge";
        public const string AutoPurge = "$Name.AutoPurge";
        public const string Export = "$Name.Export";
        public const string Search = "$Name.Search";
        public const string View = "$Name.View";
    }


"@
}

Update-Source 'CustomerHelpAgent.Web/Datas/Menu.json' "選單（id $menuId）" "`"url`": `"$Route`"" {
    param($t)
    # 找到群組節點的 subMenu 陣列，在它的最後一個元素後面加上新項目（保留原本的排版）。
    $groupMatch = [regex]::Match($t, "(?m)^(\s*)`"id`": $MenuGroupId,\s*$")
    if (-not $groupMatch.Success) { return $t }
    $subIndex = $t.IndexOf('"subMenu": [', $groupMatch.Index, [StringComparison]::Ordinal)
    $depth = 0
    for ($i = $subIndex + '"subMenu": '.Length; $i -lt $t.Length; $i++) {
        $c = $t[$i]
        if ($c -eq '[' -or $c -eq '{') { $depth++ }
        elseif ($c -eq ']' -or $c -eq '}') {
            $depth--
            if ($depth -eq 0) { break }
        }
    }
    $close = $i
    $lastBrace = $t.LastIndexOf('}', $close)
    $lineStart = $t.LastIndexOf("`n", $lastBrace) + 1
    $indent = $t.Substring($lineStart, $lastBrace - $lineStart)
    $inner = $indent + '  '
    $entry = ",`n$indent{`n$inner`"id`": $menuId,`n$inner`"name`": `"$DisplayName`",`n$inner`"icon`": `"$Icon`",`n$inner`"url`": `"$Route`"`n$indent}"
    $t.Insert($lastBrace + 1, $entry)
}

Update-Source 'CustomerHelpAgent.Web/Components/Layout/SidebarMenuService.cs' '選單權限對照' "] = MagicObjectHelper.$permConst," {
    param($t)
    Insert-Before $t "        [4] = MagicObjectHelper.角色_登出," "        [$menuId] = MagicObjectHelper.$permConst,`n"
}

if ($AdminOnly) {
    Update-Source 'CustomerHelpAgent.Tests/AdminOnlyPermissionTests.cs' '管理員專屬權限鍵清單' "MagicObjectHelper.$permConst," {
        param($t)
        Insert-After $t "        MagicObjectHelper.角色_公告管理,`n" "        MagicObjectHelper.$permConst,`n"
    }
    Update-Source 'CustomerHelpAgent.Tests/MenuPermissionConsistencyTests.cs' '管理員專屬檢視清單' "`"${Name}ViewView.razor.cs`"," {
        param($t)
        Insert-After $t "        `"AnnouncementView.razor.cs`",`n" "        `"${Name}ViewView.razor.cs`",`n"
    }
} else {
    $roleAnchor = if ($MenuGroupId -eq 2) { "                MagicObjectHelper.角色_專案項目,`n" } else { "                MagicObjectHelper.角色_團隊清單,`n" }
    Update-Source 'CustomerHelpAgent.Business/Services/Other/RolePermissionService.cs' '角色權限矩陣' "MagicObjectHelper.$permConst," {
        param($t)
        Insert-After $t $roleAnchor "                MagicObjectHelper.$permConst,`n"
    }
    Update-Source 'CustomerHelpAgent.Tests/MenuPermissionConsistencyTests.cs' '檢視與選單對照' "[`"${Name}ViewView.razor.cs`"]" {
        param($t)
        Insert-After $t "        [`"TeamViewView.razor.cs`"] = 52,`n" "        [`"${Name}ViewView.razor.cs`"] = $menuId,`n"
    }
}

Update-Source 'CustomerHelpAgent.Web/Datas/HelpTopics.json' '操作說明目錄' "`"route`": `"$Route`"" {
    param($t)
    $end = $t.LastIndexOf('}')
    $t.Insert($end + 1, ",`n  { `"route`": `"$Route`", `"title`": `"$DisplayName`", `"file`": `"$routeKey.md`" }")
}

Update-Source 'CustomerHelpAgent.Web/Components/_Imports.razor' '_Imports.razor' "@using CustomerHelpAgent.Web.Components.Views.$Plural`n" {
    param($t)
    Insert-After $t "@using CustomerHelpAgent.Web.Components.Views.Teams`n" "@using CustomerHelpAgent.Web.Components.Views.$Plural`n"
}

Update-Source 'CustomerHelpAgent.Business/Services/DataAccess/SoftDeletePurgeService.cs' '已刪除資料清理（服務）' "PurgeTypeAsync<$Name>(" {
    param($t)
    $anchor = "        await PurgeTypeAsync<Category>(result, nameof(Category), cutoffLocal, x => x.Id, x => x.Name,`n            isProtected: null, isInUse: null, include: null, attachmentsOf: null, order: null, cancellationToken);`n"
    Insert-After $t $anchor "`n        await PurgeTypeAsync<$Name>(result, nameof($Name), cutoffLocal, x => x.Id, x => x.Name,`n            isProtected: null, isInUse: null, include: null, attachmentsOf: null, order: null, cancellationToken);`n"
}

Update-Source 'CustomerHelpAgent.Web/Scheduling/Jobs/SoftDeletePurgeJob.cs' '已刪除資料清理（作業稽核）' "[`"$Name`"] = (AuditActions.$Name.AutoPurge" {
    param($t)
    Insert-After $t "        [`"Category`"] = (AuditActions.Category.AutoPurge, `"分類`"),`n" "        [`"$Name`"] = (AuditActions.$Name.AutoPurge, `"$item`"),`n"
}

foreach ($testFile in 'CustomerHelpAgent.Tests/OptimisticConcurrencyTests.cs', 'CustomerHelpAgent.Tests/SoftDeleteUserRoleTests.cs') {
    Update-Source $testFile "實體清單（$(Split-Path -Leaf $testFile)）" "nameof($Name)" { param($t) Add-SortedName $t $Name }
}

Update-Source 'CustomerHelpAgent.Tests/SoftDeletePurgeTests.cs' '已刪除資料清理測試' "db.$Name.AddRange(" {
    param($t)
    $t = Insert-After $t "            db.Category.AddRange(new Category { Name = `"過期分類`", IsDeleted = true, DeletedAt = Expired }, new Category { Name = `"未到期分類`", IsDeleted = true, DeletedAt = NotYet });`n" "            db.$Name.AddRange(new $Name { Name = `"過期資料`", IsDeleted = true, DeletedAt = Expired }, new $Name { Name = `"未到期資料`", IsDeleted = true, DeletedAt = NotYet });`n"
    Insert-After $t "        Assert.Equal([`"未到期分類`"], (await All<Category>(verify)).Select(x => x.Name).ToArray());`n" "        Assert.Equal([`"未到期資料`"], (await All<$Name>(verify)).Select(x => x.Name).ToArray());`n"
}

Update-Source 'CustomerHelpAgent.Tests/DataAccessServiceLifetimeTests.cs' '服務生命週期測試' "typeof(${Name}Service)," {
    param($t)
    Insert-After $t "        typeof(CategoryService),`n" "        typeof(${Name}Service),`n"
}
Update-Source 'CustomerHelpAgent.Tests/AdapterModelCloneTests.cs' '畫面模型 Clone 測試' "typeof(${Name}AdapterModel)," {
    param($t)
    Insert-After $t "        typeof(CategoryAdapterModel),`n" "        typeof(${Name}AdapterModel),`n"
}
Update-Source 'CustomerHelpAgent.Tests/ApiIntegrationTests.cs' 'API DI 解析測試' "[InlineData(typeof(${Name}Service))]" {
    param($t)
    Insert-After $t "    [InlineData(typeof(CategoryService))]`n" "    [InlineData(typeof(${Name}Service))]`n"
}
Update-Source 'CustomerHelpAgent.Tests/MenuIconTests.cs' "選單圖示允許清單（$Icon）" "        `"$Icon`",`n" {
    param($t)
    $setStart = $t.IndexOf('AllowedIcons', [StringComparison]::Ordinal)
    $close = $t.IndexOf("`n    };", $setStart, [StringComparison]::Ordinal)
    $t.Insert($close + 1, "        `"$Icon`",`n")
}
#endregion

#region migration
$migration = '略過（-SkipMigration）'
if (-not $SkipMigration) {
    $migrationName = "Add$Name"
    $existingMigration = Get-ChildItem -LiteralPath (Join-Path $src 'CustomerHelpAgent.AccessDatas/Migrations') -Filter "*_$migrationName.cs" -ErrorAction SilentlyContinue
    if ($existingMigration) {
        $migration = "略過（已存在 $($existingMigration[0].Name)）"
    } else {
        # 設計階段會建立主機：所有外部路徑與日誌一律導到暫存目錄，不寫進開發機的 C:\temp 或正式路徑。
        $isolated = Join-Path ([IO.Path]::GetTempPath()) ("crudgen-" + [guid]::NewGuid().ToString('N'))
        $environment = @{
            ASPNETCORE_ENVIRONMENT                               = 'Development'
            BootstrapSettings__SupportPassword                  = 'support'
            NLog__BasePath                                      = (Join-Path $isolated 'logs')
            SystemSettings__ExternalFileSystem__DatabasePath    = (Join-Path $isolated 'DB')
            SystemSettings__ExternalFileSystem__DownloadPath    = (Join-Path $isolated 'Download')
            SystemSettings__ExternalFileSystem__UploadPath      = (Join-Path $isolated 'Upload')
            SystemSettings__ExternalFileSystem__ProjectFilePath = (Join-Path $isolated 'ProjectFile')
            SystemSettings__ExternalFileSystem__ExceptionPath   = (Join-Path $isolated 'Exception')
            SystemSettings__ExternalFileSystem__TokenUsagePath  = (Join-Path $isolated 'TokenUsage')
            SystemSettings__ExternalFileSystem__AiCallLogPath   = (Join-Path $isolated 'AiCallLog')
            SystemSettings__ExternalFileSystem__AuditContentPath = (Join-Path $isolated 'AuditContent')
            SystemSettings__ExternalFileSystem__DataProtectionKeyPath = (Join-Path $isolated 'Keys')
            SystemSettings__ExternalFileSystem__BackupPath      = (Join-Path $isolated 'Backup')
        }
        $saved = @{}
        foreach ($key in $environment.Keys) {
            $saved[$key] = [Environment]::GetEnvironmentVariable($key)
            [Environment]::SetEnvironmentVariable($key, $environment[$key])
        }
        try {
            Push-Location (Join-Path $src 'CustomerHelpAgent.Web')
            # 先建置（含還原）：產出的程式碼有編譯錯誤時在這裡就看得到；dotnet ef 讀專案中繼資料前需要還原過。
            $buildOutput = & dotnet build -v q -nologo 2>&1
            if ($LASTEXITCODE -ne 0) {
                $buildOutput | Write-Host
                throw "建置失敗（見上方輸出），沒有產生 migration。檔案與登記已完成。"
            }
            $efOutput = & dotnet ef migrations add $migrationName --project ../CustomerHelpAgent.AccessDatas --startup-project . --no-build 2>&1
            if ($LASTEXITCODE -ne 0) {
                $efOutput | Write-Host
                throw "dotnet ef migrations add 失敗（見上方輸出）。檔案與登記已完成，修正後可以自己執行：dotnet ef migrations add $migrationName --project src/CustomerHelpAgent/CustomerHelpAgent.AccessDatas --startup-project src/CustomerHelpAgent/CustomerHelpAgent.Web"
            }
            $migration = "已產生 $migrationName"
        } finally {
            Pop-Location
            foreach ($key in $environment.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
            Remove-Item -LiteralPath $isolated -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
#endregion

Write-Host ''
Write-Host "CRUD 模組「$DisplayName」（$Name）—— 路由 $Route、選單 id $menuId（$menuPathText）、權限鍵 MagicObjectHelper.$permConst$(if ($AdminOnly) { '（管理員專屬）' })$(if ($WithTeams) { '、含團隊範圍' })"
Write-Host "寫入 $($written.Count) 個檔案$(if ($kept.Count) { "，保留既有 $($kept.Count) 個" })："
$written | ForEach-Object { Write-Host "  + $_" }
$kept | ForEach-Object { Write-Host "  = $_（已存在，未覆蓋）" }
Write-Host '登記：'
$report | ForEach-Object { Write-Host "  $_" }
Write-Host "migration：$migration"
Write-Host ''
Write-Host '接下來：'
Write-Host "  1. 補上實際的欄位（實體、畫面模型、DTO、表單、匯出欄位），改完欄位後重新產生 migration。"
Write-Host "  2. 把操作說明 Datas/Help/$routeKey.md 第二段與第四段改成實際的業務說明。"
Write-Host '  3. pwsh ./scripts/Invoke-QualityGate.ps1'
