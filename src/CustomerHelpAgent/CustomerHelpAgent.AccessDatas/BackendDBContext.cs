using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using CustomerHelpAgent.AccessDatas.Models;

namespace CustomerHelpAgent.AccessDatas;

public partial class BackendDBContext : DbContext
{
    public BackendDBContext()
    {
    }

    public BackendDBContext(DbContextOptions<BackendDBContext> options)
    : base(options)
    {
    }

    public virtual DbSet<MyUser> MyUser { get; set; }
    public virtual DbSet<Project> Project { get; set; }
    public virtual DbSet<ProjectFile> ProjectFile { get; set; }
    public virtual DbSet<RoleView> RoleView { get; set; }
    public virtual DbSet<Category> Category { get; set; }
    public virtual DbSet<Team> Team { get; set; }
    public virtual DbSet<AuditLog> AuditLog { get; set; }
    public virtual DbSet<ExceptionLog> ExceptionLog { get; set; }
    public virtual DbSet<TokenUsageLog> TokenUsageLog { get; set; }
    public virtual DbSet<AiCallLog> AiCallLog { get; set; }
    public virtual DbSet<Permission> Permission { get; set; }
    public virtual DbSet<RolePermissionMap> RolePermissionMap { get; set; }
    public virtual DbSet<UserRole> UserRole { get; set; }
    public virtual DbSet<UserTeam> UserTeam { get; set; }
    public virtual DbSet<PasswordResetToken> PasswordResetToken { get; set; }
    public virtual DbSet<JobRun> JobRun { get; set; }
    public virtual DbSet<ScheduledJobState> ScheduledJobState { get; set; }
    public virtual DbSet<SystemParameter> SystemParameter { get; set; }
    public virtual DbSet<Notification> Notification { get; set; }
    public virtual DbSet<NotificationDispatch> NotificationDispatch { get; set; }
    public virtual DbSet<Announcement> Announcement { get; set; }
    public virtual DbSet<AnnouncementDismissal> AnnouncementDismissal { get; set; }
    public virtual DbSet<PasswordHistory> PasswordHistory { get; set; }
    public virtual DbSet<TwoFactorBackupCode> TwoFactorBackupCode { get; set; }
    public virtual DbSet<PromptTemplate> PromptTemplate { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            // 連線設定一律由 CustomerHelpAgent.Web 的 AddConfiguredDatabase 以 SQLite 註冊。
        }

        // 10622：「必要導覽的主體有全域過濾器」。從相依端（UserTeam、ProjectFile…）經導覽屬性查主體時，
        // 相依的資料列會隨主體被過濾而靜默消失。本專案規定關聯表一律用明確 Join（見 ISoftDeletable），
        // 程式中沒有這種查詢，因此忽略這個模型驗證警告，避免每次啟動都記一筆。
        optionsBuilder.ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseCollation("Chinese_Taiwan_Stroke_CI_AS");

        #region 設定階層級的刪除政策(預設若關聯子資料表有紀錄，父資料表不可強制刪除
        foreach (var relationship in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
        {
            relationship.DeleteBehavior = DeleteBehavior.Restrict;
        }
        #endregion

        #region 軟刪除：實作 ISoftDeletable 的實體一律套用具名全域過濾器（0.9.94 起）
        var applySoftDelete = typeof(BackendDBContext).GetMethod(nameof(ApplySoftDeleteFilter), BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(x => typeof(ISoftDeletable).IsAssignableFrom(x.ClrType))
                     .ToList())
        {
            applySoftDelete.MakeGenericMethod(entityType.ClrType).Invoke(null, [modelBuilder]);
        }
        #endregion

        #region 樂觀並行：實作 IConcurrencyStamped 的實體一律以 ConcurrencyStamp 為 concurrency token（0.9.93 起）
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(x => typeof(IConcurrencyStamped).IsAssignableFrom(x.ClrType)))
        {
            modelBuilder.Entity(entityType.ClrType)
                .Property(nameof(IConcurrencyStamped.ConcurrencyStamp))
                .IsConcurrencyToken()
                .HasMaxLength(32)
                .IsRequired();
        }
        #endregion

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasMany(x => x.Files)
                .WithOne(x => x.Project)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        #region 標籤主檔的名稱唯一性（最後一道防線）
        // 服務層的前置檢查與實際寫入各自開一個 DbContext、不在同一個交易裡，
        // 兩個並發請求可以同時通過檢查，因此需要資料庫層的唯一索引兜底。
        // 索引採 SQLite 預設的 BINARY 定序（區分大小寫）；服務層的不分大小寫判定更嚴格，
        // 會先擋下，兩者不衝突。刻意不改欄位 collation，以免影響既有查詢行為。
        // 0.9.94 起為部分索引（只約束未刪除的資料）：已刪除的名稱可以重新建立，還原時由服務層檢查衝突。
        modelBuilder.Entity<Category>(entity =>
        {
            entity.HasIndex(x => x.Name).IsUnique().HasFilter(ActiveRowsOnly);
        });

        modelBuilder.Entity<Team>(entity =>
        {
            entity.HasIndex(x => x.Name).IsUnique().HasFilter(ActiveRowsOnly);

            // Code 為選填。SQLite 的唯一索引視 NULL 互不相等，所以多筆「未填代號」沒問題；
            // 但空字串彼此相同，因此寫入前一律由 NameNormalizer.NormalizeOptional 歸一成 null。
            entity.HasIndex(x => x.Code).IsUnique().HasFilter(ActiveRowsOnly);

            // 上層部門（0.9.105 起）：自我參照、沒有導覽屬性；寫在 Restrict 迴圈之後，要明確設定 Restrict。
            entity.HasOne<Team>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });
        #endregion

        #region 系統例外紀錄
        modelBuilder.Entity<ExceptionLog>(entity =>
        {
            // 合併的唯一依據。寫入已由單一消費者序列化，這個索引是第二道防線。
            entity.HasIndex(x => x.Signature).IsUnique();

            // 預設排序（最後發生 desc）與「清除 N 天未再發生」都吃這個索引。
            entity.HasIndex(x => x.LastOccurredAt);
        });
        #endregion

        #region Token 用量紀錄
        modelBuilder.Entity<TokenUsageLog>(entity =>
        {
            // 預設排序（發生時間 desc）與「清除此日之前」都吃這個索引。
            entity.HasIndex(x => x.OccurredAt);

            // 從用量明細找對應的 AI 對話紀錄。
            entity.HasIndex(x => x.CallId);

            // AI 用量上限（0.9.109 起）：每次 AI 呼叫前加總某人今天、本月的費用。
            entity.HasIndex(x => new { x.UserId, x.OccurredAt });
        });
        #endregion

        #region AI 對話紀錄
        modelBuilder.Entity<AiCallLog>(entity =>
        {
            // 預設排序（送出時間 desc）、「清除此日之前」與自動過期都吃這個索引。
            entity.HasIndex(x => x.OccurredAt);
            entity.HasIndex(x => x.CallId).IsUnique();
            entity.HasIndex(x => x.ConversationId);
        });
        #endregion

        #region RBAC 關聯（多對多）與唯一鍵
        modelBuilder.Entity<Permission>(entity =>
        {
            entity.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<RolePermissionMap>(entity =>
        {
            entity.HasIndex(x => new { x.RoleViewId, x.PermissionId }).IsUnique();
            entity.HasOne(x => x.RoleView).WithMany().HasForeignKey(x => x.RoleViewId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Permission).WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.HasIndex(x => new { x.MyUserId, x.RoleViewId }).IsUnique();
            entity.HasOne(x => x.MyUser).WithMany().HasForeignKey(x => x.MyUserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.RoleView).WithMany().HasForeignKey(x => x.RoleViewId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserTeam>(entity =>
        {
            entity.HasIndex(x => new { x.MyUserId, x.TeamId }).IsUnique();
            entity.HasOne(x => x.MyUser).WithMany().HasForeignKey(x => x.MyUserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        });
        #endregion

        #region 忘記密碼的重設 token
        modelBuilder.Entity<PasswordResetToken>(entity =>
        {
            // 以雜湊查 token；唯一索引同時是「兩次產生出同一個值」的最後防線。
            entity.HasIndex(x => x.TokenHash).IsUnique();

            // ⚠️ 必須寫在上方 Restrict 迴圈之後並明確設 Cascade：MyUserService.DeleteAsync
            // 不會先刪相依資料，Restrict 會讓「有未用 token 的使用者」刪除失敗。
            entity.HasOne(x => x.MyUser).WithMany().HasForeignKey(x => x.MyUserId).OnDelete(DeleteBehavior.Cascade);
        });
        #endregion

        #region 排程作業（0.9.96 起）
        modelBuilder.Entity<JobRun>(entity =>
        {
            // 管理頁「每個作業的最近紀錄」與依保留天數清除各吃一個索引。
            entity.HasIndex(x => new { x.JobName, x.StartedAtUtc });
            entity.HasIndex(x => x.StartedAtUtc);
        });
        #endregion

        #region 站內通知與公告（0.9.100 起）
        modelBuilder.Entity<Notification>(entity =>
        {
            // 鈴鐺的未讀數與最近清單、去重各吃一個索引。
            entity.HasIndex(x => new { x.RecipientUserId, x.ReadAtUtc });
            entity.HasIndex(x => new { x.RecipientUserId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.RecipientUserId, x.SourceKey });
            entity.HasIndex(x => x.CreatedAtUtc);

            // ⚠️ 必須寫在上方 Restrict 迴圈之後並明確設 Cascade：永久刪除使用者時不會先刪他的通知。
            entity.HasOne(x => x.RecipientUser).WithMany().HasForeignKey(x => x.RecipientUserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationDispatch>(entity =>
        {
            entity.HasKey(x => new { x.SourceKey, x.RecipientUserId });
            entity.HasIndex(x => x.SentAtUtc);
            entity.HasOne(x => x.RecipientUser).WithMany().HasForeignKey(x => x.RecipientUserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AnnouncementDismissal>(entity =>
        {
            entity.HasKey(x => new { x.AnnouncementId, x.MyUserId });
            entity.HasOne(x => x.Announcement).WithMany().HasForeignKey(x => x.AnnouncementId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.MyUser).WithMany().HasForeignKey(x => x.MyUserId).OnDelete(DeleteBehavior.Cascade);
        });
        #endregion

        #region 稽核紀錄（0.9.102 起）
        // 個人資料頁依「操作者 Id」取最近的登入紀錄；稽核表會無限長大，沒有索引就是整表掃描。
        modelBuilder.Entity<AuditLog>().HasIndex(x => new { x.ActorUserId, x.OccurredAt });
        #endregion

        #region 兩步驟驗證備用碼（0.9.104 起）
        modelBuilder.Entity<TwoFactorBackupCode>(entity =>
        {
            entity.HasIndex(x => new { x.MyUserId, x.CodeHash });

            // ⚠️ 必須寫在上方 Restrict 迴圈之後並明確設 Cascade：永久刪除使用者時不會先刪他的備用碼。
            entity.HasOne(x => x.MyUser).WithMany().HasForeignKey(x => x.MyUserId).OnDelete(DeleteBehavior.Cascade);
        });
        #endregion

        #region 密碼歷史（0.9.101 起）
        modelBuilder.Entity<PasswordHistory>(entity =>
        {
            entity.HasIndex(x => new { x.MyUserId, x.CreatedAtUtc });

            // ⚠️ 必須寫在上方 Restrict 迴圈之後並明確設 Cascade：永久刪除使用者時不會先刪他的密碼歷史。
            entity.HasOne(x => x.MyUser).WithMany().HasForeignKey(x => x.MyUserId).OnDelete(DeleteBehavior.Cascade);
        });
        #endregion

        #region AI 提示詞（0.9.108 起）
        modelBuilder.Entity<PromptTemplate>(entity =>
        {
            entity.HasIndex(x => new { x.TemplateKey, x.Version }).IsUnique();

            // 同一個範本最多一個作用中的版本；切換時在同一個交易裡先全部取消再設定。
            entity.HasIndex(x => x.TemplateKey).IsUnique().HasFilter("\"IsActive\" = 1");
        });
        #endregion

        OnModelCreatingPartial(modelBuilder);
    }

    /// <summary>部分唯一索引的條件：只約束未軟刪除的資料列。</summary>
    private const string ActiveRowsOnly = "\"IsDeleted\" = 0";

    private static void ApplySoftDeleteFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ISoftDeletable
        => modelBuilder.Entity<TEntity>().HasQueryFilter(ISoftDeletable.FilterName, e => !e.IsDeleted);

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
