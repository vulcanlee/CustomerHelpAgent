using CustomerHelpAgent.Web.DataRetention;

namespace CustomerHelpAgent.Tests;

/// <summary>「資料保留與清理」頁的資料夾大小（0.9.133 起）。</summary>
public sealed class DirectorySizeProbeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CustomerHelpAgentSizeProbe", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Measure_ShouldSumNestedFiles()
    {
        Write("a.txt", 10);
        Write(Path.Combine("202610", "b.log"), 5);

        var usage = DirectorySizeProbe.Measure(root, CancellationToken.None);

        Assert.Equal(new FolderUsage(FolderUsageStatus.Ok, 15, 2), usage);
    }

    [Fact]
    public void Measure_WithFilter_ShouldOnlyCountMatchingFiles()
    {
        Write("a.txt", 10);
        Write("b.log", 5);

        var usage = DirectorySizeProbe.Measure(root, f => f.Extension == ".log", CancellationToken.None);

        Assert.Equal(new FolderUsage(FolderUsageStatus.Ok, 5, 1), usage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Measure_WithoutPath_ShouldBeNotConfigured(string? path)
        => Assert.Equal(FolderUsageStatus.NotConfigured, DirectorySizeProbe.Measure(path, CancellationToken.None).Status);

    [Fact]
    public void Measure_MissingFolder_ShouldBeMissing()
        => Assert.Equal(FolderUsageStatus.Missing, DirectorySizeProbe.Measure(Path.Combine(root, "nope"), CancellationToken.None).Status);

    [Fact]
    public void Measure_WhenCancelled_ShouldBePartial()
    {
        Write("a.txt", 10);

        var usage = DirectorySizeProbe.Measure(root, new CancellationToken(canceled: true));

        Assert.Equal(FolderUsageStatus.Partial, usage.Status);
    }

    private void Write(string relative, int bytes)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
