# AGENTS.md

Behavioral guidelines to reduce common LLM coding mistakes. Merge with project-specific instructions as needed.

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

---

**These guidelines are working if:** fewer unnecessary changes in diffs, fewer rewrites due to overcomplication, and clarifying questions come before implementation rather than after mistakes.

* 每次產生出一個建置內容後，appsettings.json 內的版本編號，都要把**最後一碼（Patch）加 1**（例：`0.4.0 → 0.4.1`，不進位、不分異動性質），並且在 commit message 中說明版本編號的變更。

* 所有文件都要採用 UTF-8 繁體中文編碼，並且不能夠有亂碼存在（`docs/` 下與根目錄的 `.md` 須**含 BOM**；專案沒有 CI，提交前在本機跑 `pwsh ./scripts/Invoke-QualityGate.ps1`，它依序執行建置、格式、測試、文件編碼與弱點掃描）

* 需要 commit 的時候，不能自動去做 commit

* 每次有異動後，要確認相關文件也要進行更新

---

## 專案速查與文件入口（NET10-Blazor-Starter）

動手改本專案前，請先讀 **`docs/architecture/開發慣例與限制速查.md`**（相對 repo 根目錄）—— 集中列出設計慣例、不變量與踩雷點；完整文件索引見 `docs/README.md`。

**更新 AI 模型計費**（OpenAI 調價、新模型、模型 Deprecated、匯率）時，一律依 **`docs/operations/AI模型計費更新指南.md`** 執行：先輸出盤點表給使用者確認，確認後才改 `appsettings.json`。

最關鍵的不變量（違反會改壞功能或留下隱患）：
- 本系統**只支援 SQLite**；模型變更要在 `CustomerHelpAgent.AccessDatas/Migrations/` 產生 migration（0.4.24 起已移除 SQL Server 軌道）。
- 分層依賴一律向上：Web → Business → AccessDatas；`Share` 不相依其他專案，`Models`/`Dtos` 只相依 `Share`；UI 不直接 `using BackendDBContext`。
- Web API 一律回傳 `ApiResult<T>`，分頁包 `PagedResult<T>`；UI 用 Cookie 驗證、API 用 JWT Bearer。受保護 CRUD 另以 `[HasPermission("resource:action")]` + `IPermissionChecker` 做動作級授權（無權限回 403，維持 `ApiResult`；管理員短路）；UI 與 API 共用此單一 RBAC 權威來源。
- Blazor 檢視編輯前 `Clone()`。**不再需要手動清除 EF 追蹤** —— 0.4.36 起 Blazor 路徑的資料服務
  改注入 `IDbContextFactory<BackendDBContext>`，每個方法用完即棄，`CleanTrackingHelper` 已移除。
- 新增頁面權限採**宣告式**：`Menu.json`（每項唯一 `id`）＋ `SidebarMenuService.MenuPermissionMap`（`id→權限鍵`）＋ `MagicObjectHelper` 權限鍵常數；以 id 對應、重排 `Menu.json` 不錯位（舊「位置索引三處同步」已移除）。
- 單一版本來源 `SystemSettings.SystemInformation.SystemVersion`（每次異動一律 Patch +1，例：`0.4.0 → 0.4.1`；格式 `Major.Minor.Patch (YYYY/MM/DD)`）。
- **全站色票的唯一來源是 `src/CustomerHelpAgent/CustomerHelpAgent.Web/wwwroot/theme.css` 的 `:root`**；`--ov-*`（浮層）與
  `--login-*`（登入頁）只是別名層。樣式要寫哪裡的判準是 **DOM 位置**不是元件名稱：AntDesign 渲染在
  `AntContainer` 底下的浮層 → `OverlayStyles.razor`；渲染在頁面內的（Table／Pagination／Input／Tag）→
  `theme.css`。見速查表 §6.9 與 `docs/architecture/介面視覺設計規範.md`。由 `ThemeConventionTests` 守門。
- **每個登入後頁面都有「操作說明」**：新增 `@page` 必須登記 `CustomerHelpAgent.Web/Datas/HelpTopics.json` 並寫
  `Datas/Help/<路由>.md`（UTF-8 含 BOM、固定七段），或列入 `PageHelpCatalogTests.RoutesWithoutHelp`；
  改了頁面的按鈕／欄位要同步改說明第三段。見速查表 §6.14。
- `docs/*.md` 與根目錄 `*.md` 須 UTF-8 **含 BOM**（`scripts/Test-DocsEncoding.ps1` 檢查，已含在 `Invoke-QualityGate.ps1` 內）。

