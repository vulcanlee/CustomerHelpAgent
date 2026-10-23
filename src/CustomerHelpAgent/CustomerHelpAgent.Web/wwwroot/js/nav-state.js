// 側邊選單的展開／收合狀態（0.9.112 起）。
// 寬螢幕：第一次開啟是展開，之後沿用使用者上次的選擇（存在 localStorage）。
// 窄螢幕（≤ 640px，與 MainLayout.razor.css／NavMenu.razor.css 的斷點一致）：一律從收合開始，
// 漢堡鈕是抽屜開關，不寫入偏好，免得蓋掉同一個瀏覽器在桌面寬度時的選擇。
// localStorage 在隱私模式或被封鎖時可能拋例外，讀不到就當作沒存過。
// ⚠️ 檔名刻意不含 "sidebar"：這支腳本會出現在未登入的空殼 HTML 裡，
//    PageAuthorizationTests 以「回應裡沒有 sidebar 字樣」確認版面沒有被渲染。
(() => {
    const storageKey = 'app.sidebarCollapsed';
    const isNarrow = () => window.matchMedia('(max-width: 640.98px)').matches;

    window.appNavState = {
        isInitiallyCollapsed: () => {
            if (isNarrow()) {
                return true;
            }

            try {
                return localStorage.getItem(storageKey) === 'true';
            } catch (error) {
                return false;
            }
        },

        save: (collapsed) => {
            if (isNarrow()) {
                return;
            }

            try {
                localStorage.setItem(storageKey, String(collapsed));
            } catch (error) {
                // 存不了只是下次回到預設（展開），不影響目前畫面。
            }
        }
    };
})();
