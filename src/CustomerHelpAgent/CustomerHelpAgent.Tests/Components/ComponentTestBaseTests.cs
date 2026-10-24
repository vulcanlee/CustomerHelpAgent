using AntDesign.JsInterop;
using Microsoft.Extensions.DependencyInjection;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 守住 <see cref="ComponentTestBase"/> 的測試環境設定：AntDesign 內建的 DOM 事件監聽器在元件非同步釋放時
/// 會並行修改同一份訂閱清單（偶發「Collection was modified」），必須被 <see cref="NoopDomEventListener"/> 取代。
/// </summary>
public sealed class ComponentTestBaseTests : ComponentTestBase
{
    [Fact]
    public void DomEventListener_IsReplacedByNoop()
    {
        Assert.IsType<NoopDomEventListener>(Services.GetRequiredService<IDomEventListener>());
    }
}
