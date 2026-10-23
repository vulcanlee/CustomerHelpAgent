# PDF 內嵌中文字型

`AiReportPdfBuilder` 產生的 PDF 報告以本目錄的字型輸出繁體中文。

## 檔案

| 檔案 | 說明 |
|------|------|
| `NotoSansTC-Regular.ttf` | Noto Sans TC Regular 靜態實例（glyf 輪廓，20,950 字符，約 6.8 MB） |
| `OFL.txt` | SIL Open Font License 1.1 授權全文 |

## 來源與產生方式

上游是 Google Fonts 的可變字型：

```
https://github.com/google/fonts/blob/main/ofl/notosanstc/NotoSansTC%5Bwght%5D.ttf
```

該可變字型的 `wght` 字軸預設值是 **100（Thin）**，所以必須明確實例化到 400 才會得到 Regular：

```bash
python -c "
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer
f = TTFont('NotoSansTC[wght].ttf')
instancer.instantiateVariableFont(f, {'wght': 400}, inplace=False, updateFontNames=True).save('NotoSansTC-Regular.ttf')
"
```

下載日期：2026/09/11。

## 為什麼是這個格式（換字型前務必先讀）

- **必須是靜態實例 TTF。** PDFsharp 不會套用可變字型的字軸，直接嵌入可變字型會讓所有字重長得一樣。
- **不可用 `.otf`。** `notofonts/noto-cjk` 倉庫對繁中各字重只提供 CFF/PostScript 輪廓的 `.otf`，PDFsharp 官方文件明確說明只支援 TrueType 輪廓。
- **只有 Regular 一個字重。** PDFsharp 只實作斜體模擬，沒有粗體模擬，所以 `AiReportPdfBuilder` 一律不設 `Font.Bold`，標題與強調改用字級、顏色與框線表達。新增 Bold 字面會讓 repo 再肥約 7 MB。
- **不可改放 `wwwroot/`。** 那會讓字型變成任何人都能從 `/fonts/...` 下載的靜態資產，publish 後還得靠 `WebRootPath` 找路徑，測試專案也拿不到。本目錄的 `.ttf` 以 `EmbeddedResource` 內嵌進組件，由 `EmbeddedFontResolver` 讀取。
- **不可改用 Git LFS。** `actions/checkout` 預設不抓 LFS，CI 上只會拿到幾百位元組的指標檔，而 PDF 不會報錯，只會整片變成空白方框。`AiReportPdfBuilderTests` 因此斷言字型資源長度超過一百萬位元組。

輸出的 PDF 不會因為字型檔大而變大：PDFsharp 只嵌入實際用到的字符子集。

## 授權

Noto Sans TC 以 SIL Open Font License 1.1 釋出，明確允許內嵌與重新發行。完整條文見 `OFL.txt`。
