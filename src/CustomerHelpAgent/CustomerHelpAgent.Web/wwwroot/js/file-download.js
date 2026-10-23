// 由 Blazor Server 觸發的檔案下載。
// 伺服器端以 DotNetStreamReference 把位元組經 SignalR circuit 串流過來，
// 這裡組成 Blob 後以隱藏連結觸發瀏覽器下載。
window.appFileDownload = {
    // contentType 是「選用」的第三參數：既有呼叫端（日誌匯出）只傳兩個引數，
    // 此時 contentType 為 undefined，沿用原本的 text/plain，行為完全不變。
    downloadFromStream: async (fileName, contentStreamReference, contentType) => {
        const arrayBuffer = await contentStreamReference.arrayBuffer();
        const url = URL.createObjectURL(new Blob([arrayBuffer], { type: contentType || 'text/plain' }));
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = fileName ?? 'download.log';
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        URL.revokeObjectURL(url);
    }
};
