// 由 Blazor Server 觸發的剪貼簿複製。
// navigator.clipboard 只在安全內容（https 或 localhost）可用，內網以 http 部署時
// 會是 undefined，所以保留 textarea + execCommand 的退路。
// 回傳布林讓 C# 端能提示成功或請使用者手動複製。
window.appClipboard = {
    copyText: async (text) => {
        const value = text ?? '';

        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(value);
                return true;
            }
        } catch (error) {
            // 使用者拒絕權限或瀏覽器不支援，落到下面的退路。
        }

        const area = document.createElement('textarea');
        area.value = value;
        area.setAttribute('readonly', '');
        area.style.position = 'fixed';
        area.style.top = '-1000px';
        area.style.opacity = '0';
        document.body.appendChild(area);
        area.select();

        let copied = false;
        try {
            copied = document.execCommand('copy');
        } catch (error) {
            copied = false;
        }

        area.remove();
        return copied;
    }
};
