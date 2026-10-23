using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AutoMapper;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Dtos.Auths;
using CustomerHelpAgent.Dtos.Commons;
using CustomerHelpAgent.Dtos.Models;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Others;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Web.Ai;
using CustomerHelpAgent.Web.Components.Views.Admins;
using CustomerHelpAgent.Web.Components.Views.Analytics;
using CustomerHelpAgent.Web.Export;

namespace CustomerHelpAgent.Tests;

/// <summary>
/// 通用匯出（0.9.107 起）：診斷頁 CSV 與改版前逐字相同、xlsx 的型別與格式、業務頁匯出只含看得到的資料、上限、使用者匯出不含機密欄位。
/// </summary>
public sealed class ExportTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly IMapper mapper;

    public ExportTests()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using var context = factory.CreateDbContext();
        context.Database.EnsureCreated();
        mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), LoggerFactory.Create(_ => { })).CreateMapper();
    }

    // ---------- 診斷頁 CSV：與 0.9.106 之前各頁手寫的輸出逐字相同 ----------

    private const string Tricky = "含\"引號\", 逗號\n與換行";

    [Fact]
    public void AuditLogCsv_ShouldMatchTheLegacyOutput()
    {
        AuditLogAdapterModel[] rows =
        [
            new() { OccurredAt = new DateTime(2026, 10, 4, 9, 5, 7), Success = true, Action = "Login.Success", ActorAccount = "alice", ActorUserId = 7, TargetType = "MyUser", TargetId = "7", Detail = Tricky, ClientIp = "203.0.113.5" },
            new() { OccurredAt = new DateTime(2026, 10, 4, 9, 6, 0), Success = false, Action = "Login.Failed" },
        ];

        var legacy = new StringBuilder();
        legacy.AppendLine("發生時間（本地）,結果,動作,操作者帳號,操作者Id,目標類型,目標識別,摘要,來源IP");
        foreach (var item in rows)
        {
            legacy.AppendLine(string.Join(',', Csv(item.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")), Csv(item.Success ? "成功" : "失敗"), Csv(item.Action),
                Csv(item.ActorAccount), Csv(item.ActorUserId?.ToString()), Csv(item.TargetType), Csv(item.TargetId), Csv(item.Detail), Csv(item.ClientIp)));
        }

        Assert.Equal(legacy.ToString(), TabularExport.ToCsvText(AuditLogView.CsvColumns, rows));
    }

    [Fact]
    public void ExceptionLogCsv_ShouldMatchTheLegacyOutput()
    {
        ExceptionLogAdapterModel[] rows =
        [
            new() { LastOccurredAt = new DateTime(2026, 10, 4, 10, 0, 0), OccurrenceCount = 12, ExceptionType = "System.InvalidOperationException", Message = Tricky, Source = "Server",
                Page = "/projects", Operation = "Save", LoggerName = "X", Account = "bob", FirstOccurredAt = new DateTime(2026, 10, 1, 8, 0, 0), LastTraceId = "ABCD1234" },
        ];

        var legacy = new StringBuilder();
        legacy.AppendLine("最後發生,次數,例外類型,訊息,來源,頁面,操作,記錄器,使用者,首次發生,最後追蹤碼");
        foreach (var item in rows)
        {
            legacy.AppendLine(string.Join(',', Csv(item.LastOccurredAt.ToString("yyyy-MM-dd HH:mm:ss")), Csv(item.OccurrenceCount.ToString()), Csv(item.ExceptionType),
                Csv(item.Message), Csv(item.Source), Csv(item.Page), Csv(item.Operation), Csv(item.LoggerName), Csv(item.Account),
                Csv(item.FirstOccurredAt.ToString("yyyy-MM-dd HH:mm:ss")), Csv(item.LastTraceId)));
        }

        Assert.Equal(legacy.ToString(), TabularExport.ToCsvText(ExceptionLogView.CsvColumns, rows));
    }

    [Fact]
    public void TokenUsageCsv_ShouldMatchTheLegacyOutput()
    {
        TokenUsageLogAdapterModel[] rows =
        [
            new() { OccurredAt = new DateTime(2026, 10, 4, 11, 0, 0), Account = "admin", Operation = "AI 日誌分析", CallKind = "Chat", Provider = "OpenAI", Model = "gpt-test",
                InputCount = 1200, OutputCount = 300, ReasoningCount = 10, CachedInputCount = 0, TotalCount = 1510, CostUsd = 0.0123456789, CostTwd = 0.39876, CostExchangeRate = 32.3,
                CostPriceKey = "gpt-test", CostLongContext = true, CharacterCount = 5000, ElapsedMilliseconds = 1234, Success = true },
            new() { OccurredAt = new DateTime(2026, 10, 4, 11, 1, 0), Operation = "系統健康檢測", CallKind = "Chat", Provider = "OpenAI", Model = "unknown", ElapsedMilliseconds = 5,
                Success = false, FailureReason = Tricky },
        ];

        static string? Cost(double? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture);
        var legacy = new StringBuilder();
        legacy.AppendLine("時間,使用者,作業,型別,供應商,模型,輸入,輸出,推理,快取,圖片輸入,圖片快取,圖片輸出,合計,費用USD,費用TWD,匯率,計價依據,長脈絡,字元數,音訊時長秒,耗時ms,結果,失敗原因");
        foreach (var item in rows)
        {
            legacy.AppendLine(string.Join(',',
                Csv(item.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")), Csv(item.Account ?? "（系統自動）"), Csv(item.Operation), Csv(item.CallKind), Csv(item.Provider), Csv(item.Model),
                Csv(item.InputCount?.ToString()), Csv(item.OutputCount?.ToString()), Csv(item.ReasoningCount?.ToString()), Csv(item.CachedInputCount?.ToString()),
                Csv(item.ImageInputCount?.ToString()), Csv(item.ImageCachedInputCount?.ToString()), Csv(item.ImageOutputCount?.ToString()), Csv(item.TotalCount?.ToString()),
                Csv(Cost(item.CostUsd, "F6")), Csv(Cost(item.CostTwd, "F4")), Csv(Cost(item.CostExchangeRate, "F4")), Csv(item.CostPriceKey),
                Csv(item.IsUnpriced ? null : (item.CostLongContext ? "是" : "否")), Csv(item.CharacterCount?.ToString()), Csv(item.DurationSeconds?.ToString()),
                Csv(item.ElapsedMilliseconds.ToString()), Csv(item.Success ? "成功" : "失敗"), Csv(item.FailureReason)));
        }

        Assert.Equal(legacy.ToString(), TabularExport.ToCsvText(TokenUsageView.CsvColumns, rows));
    }

    [Fact]
    public void AiCallLogCsv_ShouldMatchTheLegacyOutput()
    {
        AiCallLogAdapterModel[] rows =
        [
            new() { OccurredAt = new DateTime(2026, 10, 4, 12, 0, 0), Success = true, Operation = "AI 例外分析", Account = "admin", Provider = "OpenAI", Model = "gpt-test", HttpStatus = 200,
                FinishReason = "stop", ElapsedMilliseconds = 900, RequestCharacters = 4000, ResponseCharacters = 800, RelatedInfo = Tricky, CallId = Guid.Parse("11111111-2222-3333-4444-555555555555") },
        ];

        var legacy = new StringBuilder();
        legacy.AppendLine("送出時間（本地）,結果,失敗原因,作業,帳號,供應商,模型,HTTP,結束原因,耗時ms,送出字元,回應字元,關聯說明,呼叫識別碼");
        foreach (var item in rows)
        {
            legacy.AppendLine(string.Join(',', Csv(item.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")), Csv(AiCallLogPdfBuilder.DescribeOutcome(item)), Csv(item.FailureReason),
                Csv(item.Operation), Csv(item.Account), Csv(item.Provider), Csv(item.Model), Csv(item.HttpStatus?.ToString()), Csv(item.FinishReason),
                Csv(item.ElapsedMilliseconds.ToString()), Csv(item.RequestCharacters.ToString()), Csv(item.ResponseCharacters.ToString()), Csv(item.RelatedInfo), Csv(item.CallId.ToString())));
        }

        Assert.Equal(legacy.ToString(), TabularExport.ToCsvText(AiCallLogView.CsvColumns, rows));
    }

    [Fact]
    public void Csv_ShouldStartWithTheUtf8Bom()
    {
        var bytes = TabularExport.ToCsv(AuditLogView.CsvColumns, [new AuditLogAdapterModel { Action = "A" }]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
    }

    // ---------- xlsx ----------

    [Fact]
    public void Xlsx_ShouldWriteNativeTypes_BoldFrozenFilteredHeader_AndNeverFormulas()
    {
        ExportColumn<(string Text, DateTime At, int Count, bool Flag, DateTime? Missing)>[] columns =
        [
            new("文字", x => x.Text),
            new("時間", x => x.At, TabularExport.DateTimeFormat),
            new("數量", x => x.Count),
            new("旗標", x => x.Flag),
            new("空值", x => x.Missing, TabularExport.DateFormat),
        ];

        var bytes = TabularExport.ToXlsx("測試", columns, [("=1+1", new DateTime(2026, 10, 4, 9, 30, 0), 42, true, null), ("一般", DateTime.Today, 0, false, null)]);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheet("測試");
        Assert.Equal(["文字", "時間", "數量", "旗標", "空值"], Enumerable.Range(1, 5).Select(c => sheet.Cell(1, c).GetString()));
        Assert.True(sheet.Cell(1, 1).Style.Font.Bold);
        Assert.Equal(1, sheet.SheetView.SplitRow);
        Assert.NotNull(sheet.AutoFilter);
        Assert.False(sheet.Cell(2, 1).HasFormula);
        Assert.Equal("=1+1", sheet.Cell(2, 1).GetString());
        Assert.Equal(XLDataType.DateTime, sheet.Cell(2, 2).DataType);
        Assert.Equal(new DateTime(2026, 10, 4, 9, 30, 0), sheet.Cell(2, 2).GetDateTime());
        Assert.Equal(XLDataType.Number, sheet.Cell(2, 3).DataType);
        Assert.Equal("是", sheet.Cell(2, 4).GetString());
        Assert.True(sheet.Cell(2, 5).IsEmpty());
        Assert.Equal(3, sheet.LastRowUsed()!.RowNumber());
    }

    // ---------- 業務頁 ----------

    /// <summary>⭐ 非管理員匯出時只有看得到的專案（與畫面同一個查詢、同一套團隊範圍）。</summary>
    [Fact]
    public async Task ProjectExport_ShouldOnlyContainProjectsInTheTeamScope()
    {
        await using (var context = factory.CreateDbContext())
        {
            context.Project.AddRange(
                new Project { Title = "研發案", Owner = "a", Teams = TagStringHelper.ToStored(["研發處"]) },
                new Project { Title = "業務案", Owner = "b", Teams = TagStringHelper.ToStored(["業務處"]) },
                new Project { Title = "公開案", Owner = "c" });
            await context.SaveChangesAsync();
        }

        var service = ProjectService(new FakeRecordAccessScopeProvider(false, ["研發處"]));
        var outcome = await BusinessExports.BuildAsync(service.GetAsync, new DataRequest { SortField = nameof(ProjectAdapterModel.Title), SortDescending = false }, "專案", "projects", BusinessExports.ProjectColumns);

        Assert.Null(outcome.Error);
        Assert.Equal(2, outcome.Rows);
        Assert.EndsWith(".xlsx", outcome.FileName, StringComparison.Ordinal);
        using var workbook = new XLWorkbook(new MemoryStream(outcome.Content!));
        var titles = workbook.Worksheet(1).Column(1).CellsUsed().Skip(1).Select(x => x.GetString()).Order().ToList();
        Assert.Equal(["公開案", "研發案"], titles);
    }

    [Fact]
    public async Task Export_OverTheLimit_ShouldBeRefused_AndQueryWithTheCurrentFilters()
    {
        DataRequest? seen = null;
        var outcome = await BusinessExports.BuildAsync<CategoryAdapterModel>(
            r =>
            {
                seen = r;
                return Task.FromResult(new DataRequestResult<CategoryAdapterModel> { Count = BusinessExports.MaxRows + 1, Result = [] });
            },
            new DataRequest { Search = "財務", SortField = "Name", SortDescending = true, CategoryFilters = ["A"], TeamFilters = ["T"], CurrentPage = 3, PageSize = 8 },
            "分類", "categories", BusinessExports.CategoryColumns);

        Assert.Null(outcome.Content);
        Assert.Equal(BusinessExports.TooManyMessage(BusinessExports.MaxRows + 1), outcome.Error);
        Assert.Equal(("財務", "Name", (bool?)true, 1, BusinessExports.MaxRows, 1), (seen!.Search, seen.SortField, seen.SortDescending, seen.CurrentPage, seen.PageSize, seen.Take));
        Assert.Equal(["A"], seen.CategoryFilters);
        Assert.Equal(["T"], seen.TeamFilters);
    }

    [Fact]
    public void UserExport_ShouldNotContainSecrets()
    {
        var user = new MyUserAdapterModel
        {
            Account = "alice",
            Name = "Alice",
            Email = "alice@example.com",
            Password = "PBKDF2-SECRET-HASH",
            Salt = "SALT-VALUE",
            GoogleId = "google-sub-123",
            Status = true,
            TwoFactorEnabled = true,
        };

        var bytes = TabularExport.ToXlsx("使用者", BusinessExports.UserColumns, [user]);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var cells = workbook.Worksheet(1).CellsUsed().Select(x => x.GetString()).ToList();
        Assert.Contains("alice@example.com", cells);
        Assert.DoesNotContain(cells, x => x.Contains("SECRET", StringComparison.Ordinal) || x.Contains("SALT", StringComparison.Ordinal) || x.Contains("google-sub", StringComparison.Ordinal));
        Assert.DoesNotContain(BusinessExports.UserColumns, x => x.Header.Contains("密碼", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExportAudit_ShouldRecordTheRowCount_NotTheContent()
    {
        var audit = new RecordingAuditLogService();
        var currentUser = new CurrentUserService { CurrentUser = new CurrentUser { Id = 3, Account = "carol" } };

        await BusinessExports.WriteAuditAsync(audit, currentUser, AuditActions.Project.Export, "Project", 12, deleted: true);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal((AuditActions.Project.Export, 3, "carol", "format=xlsx; rows=12; deleted=true"), (entry.Action, entry.ActorUserId!.Value, entry.ActorAccount!, entry.Detail!));
    }

    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    private ProjectService ProjectService(FakeRecordAccessScopeProvider scope)
    {
        var settings = new SystemSettings();
        return new ProjectService(factory, mapper, NullLogger<ProjectService>.Instance, Options.Create(settings), scope, new RecordingAuditLogService(), new CurrentUserService(),
            new ProjectFileStore(Options.Create(settings), NullLogger<ProjectFileStore>.Instance));
    }

    public void Dispose() => connection.Dispose();
}

/// <summary>Web API：建立專案不帶負責人回 400（0.9.106 之前資料庫 NOT NULL 擋下而回 500）。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class ProjectApiOwnerTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public ProjectApiOwnerTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task CreateProject_WithoutOwner_ShouldBeABadRequest()
    {
        using var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = "support", Password = "support" }))
            .Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Data!.AccessToken);

        var response = await client.PostAsJsonAsync("/api/Project", new ProjectCreateUpdateDto
        {
            Id = 0,
            Title = $"無負責人-{Guid.NewGuid():N}",
            Status = "進行中",
            Priority = "中",
            StartDate = DateTime.Today,
            EndDate = DateTime.Today,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("負責人", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
