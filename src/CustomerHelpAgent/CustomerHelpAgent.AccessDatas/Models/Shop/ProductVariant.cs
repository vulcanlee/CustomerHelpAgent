namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>商品規格（顏色與尺寸）。</summary>
public class ProductVariant
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public string Color { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public int Stock { get; set; }
}
