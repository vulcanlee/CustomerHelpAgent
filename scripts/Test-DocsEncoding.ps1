param(
    [string]$DocsPath = (Join-Path $PSScriptRoot "../docs")
)

# 檢查 docs/**/*.md（遞迴）與 repo 根目錄的 *.md（readme、CLAUDE、AGENTS…）：
# 一律須為 UTF-8 含 BOM，且不得含替代字元 U+FFFD（亂碼）。
# 路徑以本腳本位置為基準，從任何目錄執行都可以。只印出失敗的檔案，避免數百行 OK 淹沒真正的錯誤。

$ErrorActionPreference = "Stop"

$docsRoot = Resolve-Path $DocsPath
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

$files = @(Get-ChildItem -Path $docsRoot -Filter "*.md" -File -Recurse) +
    @(Get-ChildItem -Path $repoRoot -Filter "*.md" -File)

$failedCount = 0

foreach ($file in $files) {
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    $hasReplacementCharacter = $text.Contains([char]0xFFFD)

    if (-not $hasBom -or $hasReplacementCharacter) {
        $failedCount++
        Write-Host "FAILED $($file.FullName): BOM=$hasBom ReplacementCharacter=$hasReplacementCharacter"
    }
}

if ($failedCount -gt 0) {
    throw "Documentation encoding check failed: $failedCount of $($files.Count) files."
}

Write-Host "Documentation encoding check passed: $($files.Count) files."
