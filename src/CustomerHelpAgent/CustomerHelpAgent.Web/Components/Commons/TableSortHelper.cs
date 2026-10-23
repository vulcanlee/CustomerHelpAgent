using AntDesign;
using AntDesign.TableModels;

namespace CustomerHelpAgent.Web.Components.Commons;

/// <summary>
/// AntDesign Table 的排序解析。
///
/// 這段原本在五個檢視各複製一份（Category / Team / RoleView / MyUser / Project）。
/// <see cref="ResolveSortFieldName"/> 以 reflection 讀取 AntDesign 的內部屬性，
/// 對套件升版**極為脆弱** —— 一旦 AntDesign 改了屬性名稱，五個地方要一起改。
/// 收斂到這裡之後只有一個修改點。
/// </summary>
public static class TableSortHelper
{
    /// <summary>取目前實際套用排序的欄位；都沒有排序時取最後一個，維持原有行為。</summary>
    public static ITableSortModel GetCurrentSortModel(IEnumerable<ITableSortModel> sortModels)
    {
        return sortModels.FirstOrDefault(model => HasSortDirection(model.SortDirection))
            ?? sortModels.Last();
    }

    public static bool HasSortDirection(SortDirection sortDirection)
    {
        return sortDirection == SortDirection.Ascending || sortDirection == SortDirection.Descending;
    }

    /// <summary>
    /// 取得排序欄位名稱。
    ///
    /// AntDesign 沒有公開穩定的取得方式，因此依序退回：
    /// <c>ITableSortModel.FieldName</c> → 內部 <c>Column.FieldName</c> → 內部 <c>Column.DataIndex</c>。
    /// 後兩者靠 reflection，屬於已知的脆弱點。
    /// </summary>
    public static string ResolveSortFieldName(ITableSortModel sortModel)
    {
        if (!string.IsNullOrWhiteSpace(sortModel.FieldName))
        {
            return sortModel.FieldName;
        }

        object? column = sortModel.GetType().GetProperty("Column")?.GetValue(sortModel);
        if (column is null)
        {
            return string.Empty;
        }

        string? columnFieldName = column.GetType().GetProperty("FieldName")?.GetValue(column)?.ToString();
        if (!string.IsNullOrWhiteSpace(columnFieldName))
        {
            return columnFieldName;
        }

        object? dataIndex = column.GetType().GetProperty("DataIndex")?.GetValue(column);
        return dataIndex?.ToString() ?? string.Empty;
    }
}
