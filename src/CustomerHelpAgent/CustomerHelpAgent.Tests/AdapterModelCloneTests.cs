using System.Reflection;
using CustomerHelpAgent.Models.AdapterModel;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 畫面模型的 <c>Clone()</c> 必須複製每個可寫屬性。
///
/// 編輯視窗一律操作 Clone 出來的物件；漏掉一個屬性，存檔時那個值就變回預設值。
/// 0.9.93 加上 <c>ConcurrencyStamp</c> 時，手寫逐欄複製的 <see cref="RoleViewAdapterModel"/> 漏了它，
/// 結果從畫面修改任何角色都被當成並行衝突（0.9.95 修正）。
/// </summary>
public sealed class AdapterModelCloneTests
{
    public static TheoryData<Type> EditableModels() => new()
    {
        typeof(RoleViewAdapterModel),
        typeof(MyUserAdapterModel),
        typeof(CategoryAdapterModel),
        typeof(TeamAdapterModel),
        typeof(ProjectAdapterModel),
    };

    [Theory]
    [MemberData(nameof(EditableModels))]
    public void Clone_ShouldCopyEveryWritableScalarProperty(Type modelType)
    {
        var original = Activator.CreateInstance(modelType)!;
        var properties = modelType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .ToList();

        foreach (var property in properties)
        {
            if (SampleValue(property.PropertyType) is { } value)
            {
                property.SetValue(original, value);
            }
        }

        var clone = modelType.GetMethod("Clone", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!.Invoke(original, null)!;

        var missing = properties
            .Where(p => SampleValue(p.PropertyType) is not null && !Equals(p.GetValue(original), p.GetValue(clone)))
            .Select(p => p.Name)
            .ToList();
        Assert.True(missing.Count == 0, $"{modelType.Name}.Clone() 沒有複製：{string.Join("、", missing)}");
    }

    /// <summary>只比對純量屬性（集合與巢狀物件的深淺複製另有考量，不在這裡檢查）。</summary>
    private static object? SampleValue(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string))
        {
            return "sample-value";
        }

        if (t == typeof(int))
        {
            return 4242;
        }

        if (t == typeof(bool))
        {
            return true;
        }

        if (t == typeof(DateTime))
        {
            return new DateTime(2001, 2, 3, 4, 5, 6);
        }

        if (t == typeof(long))
        {
            return 4242L;
        }

        if (t == typeof(decimal))
        {
            return 42.42m;
        }

        return null;
    }
}
