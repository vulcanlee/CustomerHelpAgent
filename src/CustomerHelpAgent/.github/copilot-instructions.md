# copilot-instructions.md

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

## 專案指導方針
- **圖示一律使用 BlazorMaterialIcons（Google Material Icons），不要使用 emoji，也不要使用 Ant Design icons。** 專案載入的是 **classic** Material Icons 字型（非 Material Symbols），使用 Material Symbols 專有名稱會渲染成破圖方塊。
- **按鈕圖示一律透過共用元件**，不要自己寫 `<Button>` 加圖示：
  - 工具列（新增／重新整理／搜尋／清空／匯出）→ `<ToolbarIconButton Title="…" Icon="…" OnClick="…" />`
  - 表格操作欄（修改／刪除／移除）→ `<CrudActionButton Title="…" Icon="…" [Danger] OnClick="…" />`
  - 兩者皆自帶 `<Tooltip>` 與無障礙隱藏標籤，外層不需再包 `<Tooltip>`。
  - `CustomerHelpAgent.Tests/ButtonIconConventionTests.cs` 會擋下 emoji 圖示。
- 此 Blazor 專案的 sidebar 視覺偏好使用亮灰色底與深色字體，圖示同樣使用 BlazorMaterialIcons。
- **日誌是本系統可觀測性的唯一來源，寫程式時一併把日誌寫好**：
  - 選等級問三個問題：使用者正常操作的後果 → **Information**（純查詢 → Debug）；非預期但已處理（授權被拒、設定異常）→ **Warning**；操作失敗需要介入 → **Error**。
  - **使用者操作一律 Information**（新增／修改／刪除／匯出／登入登出），記「意圖」與「結果」兩筆。畫面切換由 `ApplicationCircuitHandler` 集中處理，**頁面不要自己寫**。
  - 排錯細節（查詢條件、分頁、快取命中、權限判定）一律 **Debug**。
  - 訊息**英文** ＋ PascalCase 佔位（`Account={Account}`），例外放第一個參數。
  - **絕不記錄**：密碼、token、金鑰、TOTP secret、驗證碼、連線字串、Email、姓名、搜尋關鍵字內容。可記錄的身分只有 `Account` 與 `UserId`。
  - **不得使用 `{@Model}` 解構整包物件。**
  - 注意 claim 對應在 Cookie 與 JWT 中相反：Blazor 端 `ClaimTypes.Name` 是**使用者姓名（個資）**，帳號在 `NameIdentifier`。
  - `CustomerHelpAgent.Tests/LoggingConventionTests.cs` 會擋下違規。完整規範見 `docs/operations/日誌與設定檔說明.md` §2。
- 專案的變更記錄與文件應寫入 docs 目錄，不使用 docx 目錄。當使用者指定變更記錄目錄時，將其寫入 docs 目錄，不要寫入 docx 目錄。