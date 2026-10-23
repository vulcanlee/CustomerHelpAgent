using AntDesign;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CustomerHelpAgent.Web.Components.Commons;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 表單對話窗按「取消」時的未儲存變更確認（<see cref="FormModalFlow.ConfirmCloseAsync"/>）。
///
/// 示範如何測「由 ModalService 開出來的確認窗」：先渲染 <see cref="AntContainer"/>
/// （正式環境它在 App 的版面裡，確認窗就畫在它底下），再呼叫會開窗的方法，
/// 然後在畫面上找到確認窗的按鈕點下去，最後檢查方法的回傳值。
/// </summary>
public sealed class FormModalFlowTests : ComponentTestBase
{
    [Fact]
    public async Task ConfirmCloseAsync_WhenNotDirty_ClosesWithoutAsking()
    {
        var container = Render<AntContainer>();

        var stayOpen = await FormModalFlow.ConfirmCloseAsync(Services.GetRequiredService<ModalService>(), isDirty: false);

        Assert.False(stayOpen);
        Assert.DoesNotContain("尚有未儲存的變更", container.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmCloseAsync_WhenDirty_AsksAndKeepsEditing()
    {
        var container = Render<AntContainer>();

        var pending = FormModalFlow.ConfirmCloseAsync(Services.GetRequiredService<ModalService>(), isDirty: true);

        var keepEditing = container.WaitForElement(".ant-modal-confirm .ant-btn:not(.ant-btn-dangerous)");
        Assert.Contains("尚有未儲存的變更", container.Markup, StringComparison.Ordinal);
        Assert.Contains("繼續編輯", keepEditing.TextContent, StringComparison.Ordinal);

        keepEditing.Click();

        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ConfirmCloseAsync_WhenDirty_DiscardsChangesAndCloses()
    {
        var container = Render<AntContainer>();

        var pending = FormModalFlow.ConfirmCloseAsync(Services.GetRequiredService<ModalService>(), isDirty: true);

        // 「放棄變更」是破壞性動作，ConfirmDialog 一律帶 Danger，所以它是 .ant-btn-dangerous。
        var discard = container.WaitForElement(".ant-modal-confirm .ant-btn-dangerous");
        Assert.Contains("放棄變更", discard.TextContent, StringComparison.Ordinal);

        discard.Click();

        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
