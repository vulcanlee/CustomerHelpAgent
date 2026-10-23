using Microsoft.Extensions.DependencyInjection;
using CustomerHelpAgent.Business.Startup;
using CustomerHelpAgent.Web.Extensions;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 守住 0.9.91 的資料庫初始化重構：migrate 與 seed 只能在 <c>CustomerHelpAgent.Business/Startup</c>（有測試、有跨行程鎖），
/// 不能再長回 Program.cs。
/// </summary>
public sealed class DatabaseInitializerConventionTests
{
    [Fact]
    public void ProgramCs_ShouldNotMigrateOrCreateTheDatabaseItself()
    {
        var program = File.ReadAllText(Path.Combine(FindSourceRoot(), "CustomerHelpAgent.Web", "Program.cs"));

        string[] forbidden = ["Database.Migrate(", "Database.MigrateAsync(", "EnsureCreated("];
        var found = forbidden.Where(x => program.Contains(x, StringComparison.Ordinal)).ToList();

        Assert.True(
            found.Count == 0,
            "Program.cs 不得自己 migrate 或建立資料庫（0.9.91 起由 IDatabaseInitializer 負責）。"
                + "種子資料請實作 IDatabaseSeeder，那裡有測試與跨行程鎖；寫在 Program.cs 兩者都沒有。發現："
                + string.Join("、", found));
    }

    [Fact]
    public void ProductionCode_ShouldNeverCallEnsureCreated()
    {
        var root = FindSourceRoot();
        var violations = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsExcluded(root, path))
            .Where(path => File.ReadAllText(path).Contains("EnsureCreated(", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        Assert.True(
            violations.Count == 0,
            "產品程式不得呼叫 EnsureCreated()：它依 model 直接建表、不寫 __EFMigrationsHistory，"
                + "建出來的資料庫之後永遠無法 migrate。測試專案可以用（in-memory fixture）。發現："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void EverySeederInBusiness_ShouldBeRegistered()
    {
        var implementations = typeof(IDatabaseSeeder).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDatabaseSeeder).IsAssignableFrom(t))
            .ToList();

        var services = new ServiceCollection();
        services.AddApplicationServices();
        var registered = services
            .Where(d => d.ServiceType == typeof(IDatabaseSeeder))
            .Select(d => d.ImplementationType)
            .ToHashSet();

        // 掃描失效時（例如介面被搬走）不要空跑綠燈。
        Assert.NotEmpty(implementations);

        var missing = implementations.Where(t => !registered.Contains(t)).Select(t => t.Name).ToList();
        Assert.True(
            missing.Count == 0,
            "實作了 IDatabaseSeeder 卻沒有在 ServiceCollectionExtensions.AddApplicationServices 註冊，啟動時不會執行："
                + string.Join("、", missing));

        Assert.All(
            services.Where(d => d.ServiceType == typeof(IDatabaseSeeder)),
            d => Assert.Equal(ServiceLifetime.Scoped, d.Lifetime));
    }

    private static bool IsExcluded(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var first = relative.Split(Path.DirectorySeparatorChar)[0];
        return first == "CustomerHelpAgent.Tests"
            || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CustomerHelpAgent.Web");
            if (Directory.Exists(candidate))
            {
                return dir.FullName;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "CustomerHelpAgent");
            if (Directory.Exists(Path.Combine(srcCandidate, "CustomerHelpAgent.Web")))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 src/CustomerHelpAgent。");
    }
}
