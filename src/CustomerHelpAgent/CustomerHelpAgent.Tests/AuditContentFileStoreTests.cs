using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.Systems;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 稽核詳細內容檔的存放（0.9.131 起）。路徑來自資料庫，所以一定要擋住根目錄以外的路徑。
/// </summary>
public sealed class AuditContentFileStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "audit-content-store-" + Guid.NewGuid().ToString("N"));
    private readonly AuditContentFileStore store;

    public AuditContentFileStoreTests()
    {
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AuditContentPath = Path.Combine(root, "content");
        store = new AuditContentFileStore(Options.Create(settings), NullLogger<AuditContentFileStore>.Instance);
    }

    [Fact]
    public async Task WriteAsync_ShouldUseUtcMonthFolder_AndReadBack()
    {
        var path = await store.WriteAsync(new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc), "Name: A → B");

        Assert.NotNull(path);
        Assert.StartsWith("202610/", path, StringComparison.Ordinal);
        Assert.EndsWith(".txt", path, StringComparison.Ordinal);
        Assert.Equal("Name: A → B", await store.ReadAsync(path));
    }

    [Fact]
    public async Task Delete_ShouldRemoveTheFile()
    {
        var path = await store.WriteAsync(DateTime.UtcNow, "content");

        store.Delete(path);

        Assert.Null(await store.ReadAsync(path));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("202610/../../outside.txt")]
    public async Task PathsOutsideTheRoot_ShouldBeRejected(string relativePath)
    {
        // 根目錄的上一層放一個檔，路徑穿越成功的話就讀得到、刪得掉。
        Directory.CreateDirectory(root);
        var outside = Path.Combine(root, "outside.txt");
        await File.WriteAllTextAsync(outside, "secret");

        Assert.Null(await store.ReadAsync(relativePath));
        store.Delete(relativePath);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task DeleteMonthsBefore_ShouldKeepTheThresholdMonth()
    {
        var old = await store.WriteAsync(new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc), "old");
        var kept = await store.WriteAsync(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), "kept");

        store.DeleteMonthsBefore(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.Null(await store.ReadAsync(old));
        Assert.Equal("kept", await store.ReadAsync(kept));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
