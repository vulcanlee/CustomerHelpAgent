param(
    # 移除本腳本安裝的 pre-push hook（只刪帶有識別註解的那一個）。
    [switch]$Uninstall,

    # 已經有別的 pre-push hook 時強制覆寫。
    [switch]$Force
)

# 選用安裝：把 Invoke-QualityGate.ps1 -Quick 掛上 git pre-push hook。
# 專案沒有 CI，這是唯一「自動」執行的關卡。刻意不在任何流程中自動安裝 —— 每次 push 都要等測試跑完，
# 是否接受由使用者自己決定。臨時要跳過時用 git push --no-verify。

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$marker = "# Installed by scripts/Install-GitHooks.ps1"

Push-Location $repoRoot
try {
    # 用 git 問 hooks 目錄：worktree 的 .git 是檔案不是目錄，也可能設定過 core.hooksPath。
    $hooksDir = & git rev-parse --git-path hooks
    if ($LASTEXITCODE -ne 0) {
        throw "Not a git repository: $repoRoot"
    }
    $hooksDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $hooksDir))
}
finally {
    Pop-Location
}

$hookPath = Join-Path $hooksDir "pre-push"
$existing = if (Test-Path -LiteralPath $hookPath) { [System.IO.File]::ReadAllText($hookPath) } else { $null }
$isOurs = $null -ne $existing -and $existing.Contains($marker)

if ($Uninstall) {
    if ($null -eq $existing) {
        Write-Host "No pre-push hook installed."
    }
    elseif (-not $isOurs) {
        throw "The existing pre-push hook was not installed by this script; leaving it untouched: $hookPath"
    }
    else {
        Remove-Item -LiteralPath $hookPath
        Write-Host "Removed pre-push hook: $hookPath"
    }
    return
}

if ($null -ne $existing -and -not $isOurs -and -not $Force) {
    throw "A different pre-push hook already exists: $hookPath. Use -Force to overwrite it."
}

New-Item -ItemType Directory -Path $hooksDir -Force | Out-Null

# Git for Windows 以 sh 執行 hook；行尾必須是 LF，否則 sh 會把 \r 當成指令的一部分。
$hook = @(
    "#!/bin/sh",
    $marker,
    "# Runs the quality gate (without the vulnerability scan) before every push. Skip once with: git push --no-verify",
    'exec pwsh -NoProfile -File "$(git rev-parse --show-toplevel)/scripts/Invoke-QualityGate.ps1" -Quick',
    ""
) -join "`n"
[System.IO.File]::WriteAllText($hookPath, $hook, [System.Text.UTF8Encoding]::new($false))

Write-Host "Installed pre-push hook: $hookPath"
Write-Host "Every git push now runs scripts/Invoke-QualityGate.ps1 -Quick. Skip once with: git push --no-verify"
Write-Host "Remove it with: pwsh scripts/Install-GitHooks.ps1 -Uninstall"
