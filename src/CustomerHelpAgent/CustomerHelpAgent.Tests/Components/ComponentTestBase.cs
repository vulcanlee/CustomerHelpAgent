using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 元件測試（bUnit）的共用基底。每個測試類別繼承它，就有一個可以渲染 Blazor 元件的測試環境。
///
/// 刻意只放兩件事，需要登入身分或其他服務的測試出現時再擴充：
/// <list type="bullet">
/// <item>JSInterop 設為 Loose：AntDesign 的 Button、Tooltip、Modal 內部都會呼叫 JS
/// （量測元素位置、焦點、事件監聽），測試環境沒有瀏覽器，Loose 讓這些呼叫直接回傳預設值，
/// 而不是讓測試因為「沒有設定這個 JS 呼叫」而失敗。</item>
/// <item><c>AddAntDesign()</c>：註冊 ModalService、MessageService、NotificationService 等，
/// 與 Program.cs 的註冊方式相同。</item>
/// </list>
/// </summary>
public abstract class ComponentTestBase : BunitContext
{
    protected ComponentTestBase()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddAntDesign();
    }
}
