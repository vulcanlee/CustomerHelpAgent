using AntDesign;
using AntDesign.TableModels;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace CustomerHelpAgent.Tests.Components;

/// <summary>
/// 鎖住「刪除選取」（0.9.132 起）依賴的 AntDesign Table 行為，套件升級改了行為時這裡會先紅：
/// <list type="bullet">
/// <item>表頭全選只選目前資料、略過 <c>Disabled</c> 的列（使用者頁的自己與 support、預設角色）。</item>
/// <item>各頁在 <c>ReloadAsync</c> 把綁定的 <c>SelectedRows</c> 設為空、再換 <c>DataSource</c>，勾選就會清掉。</item>
/// <item>畫面上分頁的表格（公告）：全選只選目前這一頁；換頁時以 <c>SetSelection</c>（空集合）清掉勾選。</item>
/// <item>例外紀錄自訂的勾選格（擋掉列點擊）照樣會更新綁定的勾選。</item>
/// </list>
/// AntDesign 回報勾選（<c>SelectedRowsChanged</c>）不一定在點擊的同一輪完成，斷言一律用 <c>WaitForAssertion</c>；
/// 要驗「清空」時先等前一步的勾選確實回報，否則晚到的回報會在斷言之後才把勾選填回去。
/// </summary>
public sealed class TableSelectionContractTests : ComponentTestBase
{
    public sealed record Row(int Id, string Name, bool Locked);

    private IEnumerable<Row> selected = [];

    private IRenderedComponent<Table<Row>> RenderTable(List<Row> rows, int pageSize = 10, bool customCell = false)
        => Render<Table<Row>>(p => p
            .Add(x => x.DataSource, rows)
            .Add(x => x.PageSize, pageSize)
            .Add(x => x.SelectedRows, selected)
            .Add(x => x.SelectedRowsChanged, EventCallback.Factory.Create<IEnumerable<Row>>(this, v => selected = v))
            .Add(x => x.ChildContent, row => builder =>
            {
                builder.OpenComponent<Selection>(0);
                builder.AddComponentParameter(1, nameof(Selection.Disabled), row.Locked);
                if (customCell)
                {
                    builder.AddComponentParameter(2, nameof(Selection.CellRender), CustomCell());
                }

                builder.CloseComponent();
            }));

    /// <summary>同例外紀錄頁的勾選格：外包一層（頁面上是擋掉列點擊的 span），裡面自己放 Checkbox。</summary>
    private RenderFragment<CellData> CustomCell()
        => cell => b =>
        {
            b.OpenElement(0, "span");
            b.AddAttribute(1, "class", "custom-cell");
            b.OpenComponent<Checkbox>(2);
            b.AddComponentParameter(3, nameof(Checkbox.Checked), cell.RowData.Selected);
            b.AddComponentParameter(4, nameof(Checkbox.CheckedChanged),
                EventCallback.Factory.Create<bool>(this, v => cell.RowData.Selected = v));
            b.CloseComponent();
            b.CloseElement();
        };

    [Fact]
    public void SelectAll_ShouldSkipDisabledRows()
    {
        var rows = new List<Row> { new(1, "甲", false), new(2, "自己", true), new(3, "丙", false) };
        var table = RenderTable(rows);

        table.Find("thead .ant-checkbox-input").Change(true);

        table.WaitForAssertion(() => Assert.Equal([1, 3], selected.Select(x => x.Id).Order()));
    }

    [Fact]
    public void SelectAll_WithClientSidePaging_ShouldOnlySelectTheCurrentPage()
    {
        var rows = Enumerable.Range(1, 3).Select(i => new Row(i, $"公告{i}", false)).ToList();
        var table = RenderTable(rows, pageSize: 2);

        table.Find("thead .ant-checkbox-input").Change(true);

        table.WaitForAssertion(() => Assert.Equal([1, 2], selected.Select(x => x.Id).Order()));
    }

    [Fact]
    public async Task SetSelectionWithEmpty_ShouldClearAndNotify()
    {
        var rows = new List<Row> { new(1, "甲", false), new(2, "乙", false) };
        var table = RenderTable(rows);
        table.Find("thead .ant-checkbox-input").Change(true);
        table.WaitForAssertion(() => Assert.Equal(2, selected.Count()));

        await table.InvokeAsync(() => table.Instance.SetSelection(Array.Empty<Row>()));

        table.WaitForAssertion(() => Assert.Empty(selected));
    }

    [Fact]
    public void CustomCheckboxCell_ShouldUpdateTheBoundSelection()
    {
        var rows = new List<Row> { new(1, "甲", false), new(2, "乙", false) };
        var table = RenderTable(rows, customCell: true);

        table.FindAll("tbody .custom-cell .ant-checkbox-input")[1].Change(true);

        table.WaitForAssertion(() => Assert.Equal([2], selected.Select(x => x.Id)));
    }

    [Fact]
    public void ResettingSelectedRows_WithNewData_ShouldClearTheCheckboxes()
    {
        var rows = new List<Row> { new(1, "甲", false), new(2, "乙", false) };
        var table = RenderTable(rows);
        table.Find("thead .ant-checkbox-input").Change(true);
        table.WaitForAssertion(() => Assert.Equal(2, selected.Count()));

        selected = [];
        table.Render(p => p
            .Add(x => x.SelectedRows, selected)
            .Add(x => x.DataSource, new List<Row>(rows)));

        table.WaitForAssertion(() =>
        {
            Assert.Empty(table.FindAll("tbody .ant-checkbox-checked"));
            Assert.Empty(selected);
        });
    }
}
