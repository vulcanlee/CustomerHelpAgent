param(
    # 跳過弱點掃描（需要連網查詢 nuget.org）。pre-push hook 用的就是這個模式。
    [switch]$Quick
)

# 提交前的品質關卡：專案沒有 CI，這支腳本就是唯一的關卡，也是日後任何 CI 平台唯一要呼叫的入口 ——
# 換平台時只要讓 CI 執行這支腳本，不用把關卡重寫一遍。
# 遇到第一個失敗就停止（建置失敗時跑測試沒有意義），結尾印出已執行步驟的結果與耗時。

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$solution = "src/CustomerHelpAgent/CustomerHelpAgent.slnx"

# restore 是最常被 nuget.org 偶發逾時打到的一步；沿用 2026-09-20 移除的 CI 的 180 秒，使用者自己設定過就不覆蓋。
if (-not $env:NUGET_HTTP_TIMEOUT_SECONDS) {
    $env:NUGET_HTTP_TIMEOUT_SECONDS = "180"
}

# 弱點掃描的允許清單：只放已經記錄決議的諮詢代號（決議見 docs/operations/CI-CD與品質檢查.md §4.1）。
# 清單只能縮短：某筆諮詢不再出現（上游修好了）時關卡會失敗，要求把它移除，避免豁免變成永久的。
$allowedAdvisories = @('GHSA-2m69-gcr7-jv3q')

function Invoke-VulnerabilityScan {
    # ⚠️ dotnet list package --vulnerable 找到弱點時**仍然回傳 0**，必須自己解析輸出。
    # 比對諮詢代號而非訊息文字：dotnet CLI 的輸出會隨執行環境語系改變。
    # dotnet CLI 輸出 UTF-8，PowerShell 擷取時卻以主控台字碼頁（繁中 Windows 是 cp950）解碼，中文會變亂碼；
    # 擷取期間暫時改成 UTF-8。
    $previousEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    try {
        $output = & dotnet list $solution package --vulnerable --include-transitive 2>&1 | Out-String
    }
    finally {
        [Console]::OutputEncoding = $previousEncoding
    }
    Write-Host $output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet list package failed with exit code $LASTEXITCODE."
    }

    $found = @([regex]::Matches($output, 'advisories/(?<id>[A-Za-z0-9-]+)') |
        ForEach-Object { $_.Groups['id'].Value } | Sort-Object -Unique)

    $unexpected = @($found | Where-Object { $allowedAdvisories -notcontains $_ })
    if ($unexpected.Count -gt 0) {
        throw "Vulnerable packages found that are not in the allow list: $($unexpected -join ', ')"
    }

    $stale = @($allowedAdvisories | Where-Object { $found -notcontains $_ })
    if ($stale.Count -gt 0) {
        throw "Allow-listed advisories no longer reported; remove them from this script and the docs: $($stale -join ', ')"
    }

    Write-Host "Vulnerability scan passed (allow list: $($allowedAdvisories -join ', '))."
}

# Release：與 2026-09-20 移除的 CI 一致；而且開發中的網站鎖住的是 Debug 的 bin，用 Release 建置不會跟它搶檔案。
$steps = @(
    @{ Name = "Restore"; Action = { & dotnet restore $solution } },
    @{ Name = "Build"; Action = { & dotnet build $solution --configuration Release --no-restore -v:minimal } },
    @{ Name = "Format"; Action = { & dotnet format $solution --verify-no-changes --no-restore } },
    @{ Name = "Test"; Action = { & dotnet test $solution --configuration Release --no-build } },
    @{ Name = "Docs encoding"; Action = { & (Join-Path $PSScriptRoot "Test-DocsEncoding.ps1") } },
    @{ Name = "Vulnerability"; Action = { Invoke-VulnerabilityScan }; SkipWhenQuick = $true }
)

$results = [System.Collections.Generic.List[object]]::new()
$failed = $false
$total = [System.Diagnostics.Stopwatch]::StartNew()

Push-Location $repoRoot
try {
    foreach ($step in $steps) {
        if ($Quick -and $step.SkipWhenQuick) {
            $results.Add([pscustomobject]@{ Step = $step.Name; Result = "Skipped"; Seconds = "-" })
            continue
        }

        Write-Host ""
        Write-Host "==> $($step.Name)" -ForegroundColor Cyan
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $result = "Passed"
        try {
            $global:LASTEXITCODE = 0
            & $step.Action
            if ($LASTEXITCODE -ne 0) {
                throw "exit code $LASTEXITCODE"
            }
        }
        catch {
            $result = "Failed"
            Write-Host "$($step.Name) failed: $($_.Exception.Message)" -ForegroundColor Red
        }

        $watch.Stop()
        $results.Add([pscustomobject]@{ Step = $step.Name; Result = $result; Seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1) })

        if ($result -eq "Failed") {
            $failed = $true
            break
        }
    }
}
finally {
    Pop-Location
}

$total.Stop()
Write-Host ""
Write-Host "Quality gate summary" -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host
Write-Host ("Total: {0:N1} s" -f $total.Elapsed.TotalSeconds)

if ($failed) {
    Write-Host "QUALITY GATE FAILED" -ForegroundColor Red
    exit 1
}

Write-Host "QUALITY GATE PASSED" -ForegroundColor Green
exit 0
