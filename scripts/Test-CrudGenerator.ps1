<#
.SYNOPSIS
    驗證 CRUD 產生器（scripts/New-CrudModule.ps1）產出的模組能建置、全部測試通過，而且重跑不會重複登記（0.9.110 起）。

.DESCRIPTION
    在暫存的 git worktree 裡（以目前工作目錄的內容為準，含尚未提交的變更）依序：
      1. 產生三個範例模組：一般、-WithTeams、-AdminOnly（掛在系統管理群組）。
      2. 建置、格式檢查、執行全部測試、檢查文件編碼。
      3. 以 -Force 重跑三個產生指令，確認沒有任何檔案再被改動（登記不重複）。
      4. 刪除 worktree（-KeepWorktree 時保留，方便啟動網站實際操作）。
    不會改動你的工作目錄與 git index。耗時數分鐘，所以不在品質關卡裡；修改產生器或樣板後必跑。

.PARAMETER KeepWorktree
    結束後保留 worktree，並印出路徑。
#>
[CmdletBinding()]
param(
    [switch]$KeepWorktree
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$worktree = Join-Path ([IO.Path]::GetTempPath()) ("crudgen-wt-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$results = [System.Collections.Generic.List[object]]::new()

function Invoke-Step([string]$name, [scriptblock]$action) {
    Write-Host "==> $name" -ForegroundColor Cyan
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $action
    $results.Add([pscustomobject]@{ Step = $name; Seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1) })
}

function Invoke-Native([string]$file, [string[]]$arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$file $($arguments -join ' ') 失敗（exit $LASTEXITCODE）" }
}

function Get-TreeState {
    # 目前 worktree 的完整內容（含未追蹤檔案），用暫存 index 計算，不影響任何人的 index。
    $index = Join-Path ([IO.Path]::GetTempPath()) ("crudgen-index-" + [guid]::NewGuid().ToString('N'))
    try {
        $env:GIT_INDEX_FILE = $index
        Invoke-Native git @('-C', $worktree, '-c', 'core.safecrlf=false', 'add', '-A')
        return (git -C $worktree write-tree)
    } finally {
        Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $index -Force -ErrorAction SilentlyContinue
    }
}

# 以 hashtable 傳參數（陣列 splatting 只會變成位置參數）。
$modules = @(
    @{ Name = 'GenSample'; DisplayName = '產生器範例' },
    @{ Name = 'GenTeamSample'; DisplayName = '產生器團隊範例'; WithTeams = $true; Icon = 'inventory_2' },
    @{ Name = 'GenAdminSample'; DisplayName = '產生器管理範例'; AdminOnly = $true; MenuGroupId = 8 }
)

try {
    Invoke-Step '建立暫存 worktree（目前工作目錄的內容）' {
        $index = Join-Path ([IO.Path]::GetTempPath()) ("crudgen-index-" + [guid]::NewGuid().ToString('N'))
        try {
            $env:GIT_INDEX_FILE = $index
            Invoke-Native git @('-C', $repoRoot, 'read-tree', 'HEAD')
            Invoke-Native git @('-C', $repoRoot, '-c', 'core.safecrlf=false', 'add', '-A')
            $tree = git -C $repoRoot write-tree
        } finally {
            Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $index -Force -ErrorAction SilentlyContinue
        }
        $commit = git -C $repoRoot commit-tree $tree -p HEAD -m 'Test-CrudGenerator snapshot'
        Invoke-Native git @('-C', $repoRoot, 'worktree', 'add', '--detach', $worktree, $commit)
    }

    $generator = Join-Path $worktree 'scripts/New-CrudModule.ps1'
    $script:first = $true
    foreach ($module in $modules) {
        Invoke-Step "產生 $($module.Name)" {
            $arguments = $module.Clone()
            if (-not $script:first) { $arguments['Force'] = $true }
            & $generator @arguments
            $script:first = $false
        }
    }

    $solution = Join-Path $worktree 'src/CustomerHelpAgent/CustomerHelpAgent.slnx'
    Invoke-Step '建置' { Invoke-Native dotnet @('build', $solution, '-v', 'q', '-nologo') }
    Invoke-Step '格式檢查' { Invoke-Native dotnet @('format', 'whitespace', $solution, '--verify-no-changes') }
    Invoke-Step '全部測試' { Invoke-Native dotnet @('test', $solution, '--no-build', '-v', 'q', '-nologo') }
    Invoke-Step '文件編碼' { Invoke-Native pwsh @('-NoProfile', '-File', (Join-Path $worktree 'scripts/Test-DocsEncoding.ps1')) }

    Invoke-Step '重跑產生器（不可再改動任何檔案）' {
        $before = Get-TreeState
        foreach ($module in $modules) {
            $arguments = $module.Clone()
            $arguments['Force'] = $true
            & $generator @arguments | Out-Null
        }
        $after = Get-TreeState
        if ($before -ne $after) {
            git -C $worktree diff --stat
            throw '重跑產生器改動了檔案：登記不是冪等的。'
        }
    }

    Write-Host ''
    $results | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host 'CRUD 產生器驗證通過。' -ForegroundColor Green
} finally {
    if ($KeepWorktree) {
        Write-Host "worktree 保留在：$worktree（用完請執行 git worktree remove --force `"$worktree`"）"
    } elseif (Test-Path -LiteralPath $worktree) {
        git -C $repoRoot worktree remove --force $worktree
    }
}
