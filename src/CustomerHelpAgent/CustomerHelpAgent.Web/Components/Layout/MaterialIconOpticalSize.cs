namespace CustomerHelpAgent.Web.Components.Layout;

/// <summary>
/// 側邊欄圖示的「光學尺寸」補正。
///
/// Material Icons 各字面雖然共用同一個 em 方框，墨跡佔的高度卻差很多。在 100px 字級下
/// 實測側邊欄這組圖示的墨跡高度：
///
/// <code>
/// admin_panel_settings 84　work 80　analytics 76　logout 76　home 72　storage 68
/// </code>
///
/// <c>home</c> 是個尖頂三角形，上半部又細，所以即使字級一樣也看起來小一號
/// （<c>storage</c> 更矮，但它是三條滿版橫槓，視覺密度夠，不需要補）。
///
/// ⚠️ 這裡放大的是**光學尺寸而非字級**：目的是讓它跟鄰居看起來一樣大，
/// 不是把它變顯眼。改動前請先用同樣的方式量過墨跡高度，不要憑感覺調。
///
/// 另外，<c>MaterialIcon</c> 會輸出行內的 <c>style="font-size:…"</c>，CSS 的 font-size
/// 規則蓋不過去，所以補正只能從元件的 <c>Size</c> 參數走。
/// </summary>
public static class MaterialIconOpticalSize
{
    /// <summary>側邊欄圖示的基準尺寸（<c>MaterialIcon</c> 的預設值）。</summary>
    public const int Default = 24;

    /// <summary>取得某個字面在側邊欄應使用的尺寸（px）。</summary>
    public static int For(string? kind) => kind switch
    {
        "home" => 27,
        _ => Default,
    };
}
