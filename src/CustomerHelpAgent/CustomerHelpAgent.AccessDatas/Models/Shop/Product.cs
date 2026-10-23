using CustomerHelpAgent.Share.Shop;

namespace CustomerHelpAgent.AccessDatas.Models;

/// <summary>商品。</summary>
public class Product
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>類別，值見 <see cref="ProductCategories"/>。</summary>
    public string Category { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public List<ProductVariant> Variants { get; set; } = [];
}
