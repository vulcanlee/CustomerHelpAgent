using System.Net.Mail;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Factories;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.Other;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Systems;
using CustomerHelpAgent.Share.Helpers;

namespace CustomerHelpAgent.Business.Services.DataAccess;

public class MyUserService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IRbacWriteService rbacWriteService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly BootstrapSettings bootstrapSettings;
    private readonly IPasswordPolicy passwordPolicy;

    public IMapper Mapper { get; }
    public ILogger<MyUserService> Logger { get; }

    public MyUserService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IMapper mapper,
        ILogger<MyUserService> logger,
        IRbacWriteService rbacWriteService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        IOptions<BootstrapSettings> bootstrapOptions,
        IPasswordPolicy passwordPolicy)
    {
        this.contextFactory = contextFactory;
        Mapper = mapper;
        Logger = logger;
        this.rbacWriteService = rbacWriteService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        bootstrapSettings = bootstrapOptions.Value;
        this.passwordPolicy = passwordPolicy;
    }

    /// <summary>取得目前操作者作為稽核 actor；未登入（Id==0）時回 null。</summary>
    private (int? ActorUserId, string? ActorAccount) ResolveActor()
    {
        var user = currentUserService.CurrentUser;
        return user.Id > 0 ? (user.Id, user.Account) : (null, null);
    }

    /// <summary>
    /// 修改使用者的稽核差異（0.9.131 起）。⚠️ 密碼、Salt、SecurityStamp、兩步驟密鑰絕不寫值：密碼只記「已變更」；
    /// Google 綁定、登入失敗次數、鎖定與密碼時間是系統維護的，不是這次修改改的，不列入。
    /// </summary>
    private static string? BuildUpdateDiff(
        MyUser before,
        MyUser after,
        bool passwordChanged,
        (List<int> AdditionalRoleIds, List<string> TeamNames) beforeAssignments,
        MyUserAdapterModel paraObject)
        => AuditDiff.Compare(before, after,
                nameof(MyUser.Password), nameof(MyUser.Salt), nameof(MyUser.SecurityStamp),
                nameof(MyUser.TwoFactorSecret), nameof(MyUser.TwoFactorLastStep), nameof(MyUser.TwoFactorEnabled),
                nameof(MyUser.GoogleId), nameof(MyUser.OAuthProvider),
                nameof(MyUser.AccessFailedCount), nameof(MyUser.LockoutEndUtc), nameof(MyUser.PasswordChangedAtUtc))
            .Masked(nameof(MyUser.Password), passwordChanged)
            .Set("AdditionalRoleIds",
                beforeAssignments.AdditionalRoleIds.Select(x => x.ToString()),
                (paraObject.AdditionalRoleIds ?? []).Where(x => x != paraObject.RoleViewId).Select(x => x.ToString()))
            .Set("Teams", beforeAssignments.TeamNames, paraObject.TeamNames ?? [])
            .Render($"使用者 #{after.Id}（{before.Account}）修改");

    /// <summary>彙整使用者指派摘要（帳號、角色 Id、團隊）供稽核 Detail。</summary>
    private static string BuildAssignmentDetail(string account, MyUserAdapterModel paraObject)
    {
        var roleIds = new List<int>();
        if (paraObject.RoleViewId.HasValue)
        {
            roleIds.Add(paraObject.RoleViewId.Value);
        }
        if (paraObject.AdditionalRoleIds is not null)
        {
            roleIds.AddRange(paraObject.AdditionalRoleIds);
        }
        var teams = (paraObject.TeamNames ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x));
        return $"account={account}; roleIds=[{string.Join(",", roleIds.Distinct())}]; teams=[{string.Join(",", teams)}]";
    }

    public async Task<DataRequestResult<MyUserAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug(
            "Loading users. HasSearch={HasSearch}, SearchLength={SearchLength}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            string.IsNullOrWhiteSpace(dataRequest.Search) == false,
            dataRequest.Search?.Length ?? 0,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<MyUserAdapterModel> result = new();
        IQueryable<MyUser> dataSource = context.MyUser
            .AsNoTracking()
            .Include(x => x.RoleView);

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            dataSource = dataSource.Where(x =>
                x.Account.Contains(dataRequest.Search) ||
                x.Name.Contains(dataRequest.Search) ||
                (x.Email ?? string.Empty).Contains(dataRequest.Search) ||
                (x.RoleView != null && x.RoleView.Name.Contains(dataRequest.Search)));
        }

        IOrderedQueryable<MyUser>? sorted = null;

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(MyUserAdapterModel.Account))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Account).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Account).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.Name))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Name).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Name).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.Email))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Email).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Email).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.RoleViewName))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.RoleView != null ? x.RoleView.Name : string.Empty).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.RoleView != null ? x.RoleView.Name : string.Empty).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.StatusText))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Status).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Status).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.IsAdminText))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.IsAdmin).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.IsAdmin).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.CreateAt))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CreateAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CreateAt).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.UpdateAt))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.UpdateAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.UpdateAt).ThenBy(x => x.Id)
                        : null;
            }
        }

        // Skip/Take 一定要搭配 OrderBy，否則 SQLite 不保證回傳順序，分頁會重複或漏資料。
        // 未指定欄位、欄位不認得、方向為 null —— 三種情況一律退回預設排序。
        dataSource = sorted ?? dataSource.OrderByDescending(x => x.UpdateAt).ThenByDescending(x => x.Id);

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        List<MyUser> records = await dataSource.ToListAsync();
        List<MyUserAdapterModel> adapterModelObjects = Mapper.Map<List<MyUserAdapterModel>>(records);
        foreach (var adapterModelItem in adapterModelObjects)
        {
            await OtherDependencyData(adapterModelItem);
        }

        result.Result = adapterModelObjects;
        Logger.LogDebug("Loaded users successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<MyUserAdapterModel> GetAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Loading user by id. Id={UserId}", id);

        MyUser? item = await context.MyUser
            .AsNoTracking()
            .Include(x => x.RoleView)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogInformation("User not found. Id={UserId}", id);
            return new MyUserAdapterModel();
        }

        MyUserAdapterModel result = Mapper.Map<MyUserAdapterModel>(item);
        await OtherDependencyData(result);
        return result;
    }

    public async Task<List<RoleViewAdapterModel>> GetRoleViewsAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Loading role views for user maintenance.");

        List<RoleView> roleViews = await context.RoleView
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync();

        Logger.LogDebug("Loaded role views successfully. Count={Count}", roleViews.Count);
        return Mapper.Map<List<RoleViewAdapterModel>>(roleViews);
    }

    /// <param name="copiedFromAccount">由清單「複製」建立時的範本帳號（0.9.127 起），記在稽核 Detail。</param>
    public async Task<VerifyRecordResult> AddAsync(MyUserAdapterModel paraObject, string? copiedFromAccount = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Creating user. Account={Account}, RoleViewId={RoleViewId}", paraObject.Account, paraObject.RoleViewId);

        try
        {
            if (string.IsNullOrWhiteSpace(paraObject.Password))
            {
                Logger.LogInformation("User creation rejected because password is empty. Account={Account}", paraObject.Account);
                return VerifyRecordResultFactory.Build(false, "新增使用者時必須輸入密碼。");
            }

            if (passwordPolicy.Check(paraObject.Password) is { } passwordError)
            {
                Logger.LogInformation("User creation rejected because the password does not meet the policy. Account={Account}", paraObject.Account);
                return VerifyRecordResultFactory.Build(false, passwordError);
            }

            if (await ValidateRolesAsync(context, paraObject) is { } roleError)
            {
                return VerifyRecordResultFactory.Build(false, roleError);
            }

            MyUser itemParameter = Mapper.Map<MyUser>(paraObject);
            itemParameter.RoleView = null;
            itemParameter.ConcurrencyStamp = ConcurrencyStampHelper.New();
            await passwordPolicy.ApplyAsync(context, itemParameter, paraObject.Password, paraObject.MustChangePassword);

            // 主表與角色、團隊對應在同一個交易（0.9.136 起）：對應寫入失敗時整筆回復，不留下只存了一半的帳號。
            await using var transaction = await context.Database.BeginTransactionAsync();
            await context.MyUser.AddAsync(itemParameter);
            await context.SaveChangesAsync();

            await SyncAssignmentsAsync(context, rbacWriteService, itemParameter.Id, paraObject);
            await transaction.CommitAsync();

            var (actorUserId, actorAccount) = ResolveActor();
            await auditLogService.WriteAsync(
                AuditActions.User.Create, success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(MyUser), targetId: itemParameter.Id.ToString(),
                detail: BuildAssignmentDetail(itemParameter.Account, paraObject)
                    + (copiedFromAccount is null ? string.Empty : $"; copiedFrom={copiedFromAccount}"));

            Logger.LogInformation("User created successfully. UserId={UserId}, Account={Account}", itemParameter.Id, itemParameter.Account);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create user. Account={Account}", paraObject.Account);
            return VerifyRecordResultFactory.Build(false, "新增使用者失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(MyUserAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Updating user. UserId={UserId}, Account={Account}", paraObject.Id, paraObject.Account);

        try
        {
            MyUser? itemData = await context.MyUser
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (itemData == null)
            {
                Logger.LogWarning("User update rejected because record was not found. UserId={UserId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的使用者資料。");
            }

            if (await ValidateRolesAsync(context, paraObject) is { } roleError)
            {
                return VerifyRecordResultFactory.Build(false, roleError);
            }

            var settingPassword = !string.IsNullOrWhiteSpace(paraObject.Password);
            if (settingPassword && (passwordPolicy.Check(paraObject.Password)
                ?? (await passwordPolicy.IsReusedAsync(context, itemData, paraObject.Password) ? PasswordPolicy.ReusedMessage : null)) is { } passwordError)
            {
                Logger.LogInformation("User update rejected because the new password does not meet the policy. UserId={UserId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, passwordError);
            }

            // 0.9.120 起修改窗也擋停用 support 與自己（與清單上點狀態標籤同一條規則）。
            if (itemData.Status && !paraObject.Status && GetDisableBlockReason(itemData.Account, itemData.Id) is { } disableBlocked)
            {
                Logger.LogInformation("User update rejected because it would disable the support account or the current user. UserId={UserId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, disableBlocked);
            }

            // 停用、管理員身分或角色有變，這個人所有已登入的工作階段都要失效（0.9.103 起）。角色在存檔後才同步，
            // 所以先算出「原本有效的角色」與「這次要寫入的角色」來比較（已刪除角色的關聯會被保留，不算變更）。
            var sessionAffected = itemData.Status != paraObject.Status
                || itemData.IsAdmin != paraObject.IsAdmin
                || !(await GetActiveRoleIdsAsync(context, itemData.Id, itemData.RoleViewId)).SetEquals(DesiredRoleIds(paraObject));

            // 修改前的快照與指派（0.9.131 起，給稽核差異用）：要在逐欄指定與同步指派之前取。
            var before = (MyUser)context.Entry(itemData).CurrentValues.ToObject();
            var beforeAssignments = await GetUserAssignmentsAsync(itemData.Id);

            // 只複製編輯畫面上有的欄位（0.9.93 起）。0.9.92 之前是整筆覆蓋，而畫面模型沒有登入失敗次數、
            // 鎖定到期、兩步驟驗證、Google 綁定 —— 管理員只改姓名，被鎖定的帳號就解鎖了。
            itemData.Account = paraObject.Account;
            itemData.Name = paraObject.Name;
            itemData.Email = paraObject.Email;
            itemData.Status = paraObject.Status;
            itemData.IsAdmin = paraObject.IsAdmin;
            itemData.RoleViewId = paraObject.RoleViewId;
            itemData.UpdateAt = paraObject.UpdateAt;
            itemData.MustChangePassword = paraObject.MustChangePassword;

            if (settingPassword)
            {
                // 管理員替使用者設定新密碼視為解除鎖定（與「忘記密碼」重設後的行為一致）。
                // 0.9.92 之前這個效果是整筆覆蓋順帶造成的；改成只更新畫面欄位後要明確寫出來，
                // 否則「被鎖住時請管理員改密碼」這個操作方式會失效。只改其他欄位時不動鎖定狀態。0.9.101 起由 ApplyAsync 一併處理。
                await passwordPolicy.ApplyAsync(context, itemData, paraObject.Password, paraObject.MustChangePassword);
            }

            if (sessionAffected)
            {
                itemData.SecurityStamp = SecurityStamps.New();
            }

            var diff = BuildUpdateDiff(before, itemData, settingPassword, beforeAssignments, paraObject);

            var entry = context.Entry(itemData);
            ConcurrencyStampHelper.Apply(entry, paraObject.ConcurrencyStamp);
            SoftDeleteHelper.ProtectFlags(entry);
            await using var transaction = await context.Database.BeginTransactionAsync();
            await context.SaveChangesAsync();

            await SyncAssignmentsAsync(context, rbacWriteService, itemData.Id, paraObject);
            await transaction.CommitAsync();

            var (actorUserId, actorAccount) = ResolveActor();
            await auditLogService.WriteAsync(
                AuditActions.User.Update, success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(MyUser), targetId: itemData.Id.ToString(),
                detail: BuildAssignmentDetail(itemData.Account, paraObject),
                content: diff);

            Logger.LogInformation("User updated successfully. UserId={UserId}, Account={Account}", itemData.Id, itemData.Account);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // 別人在這段期間先存過或刪除了這筆：使用者情境而非系統錯誤（LOG-11），記 Information、不帶例外物件。
            Logger.LogInformation("User update rejected by concurrency conflict. UserId={UserId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update user. UserId={UserId}, Account={Account}", paraObject.Id, paraObject.Account);
            return VerifyRecordResultFactory.Build(false, "修改使用者失敗。", ex);
        }
    }

    /// <summary>
    /// 主要角色與額外角色必須存在且未刪除（0.9.95 起）：編輯視窗開著的期間角色可能被刪掉，
    /// 存進去的話，以它為主要角色的人每次換頁都會被登出（<c>AuthenticationStateHelper</c> 找不到角色）。
    /// 沒有主要角色（null）是允許的 —— Google 自動建立的帳號在預設角色不存在時就是 null。
    /// </summary>
    private async Task<string?> ValidateRolesAsync(BackendDBContext context, MyUserAdapterModel paraObject)
    {
        var requested = (paraObject.AdditionalRoleIds ?? []).ToList();
        if (paraObject.RoleViewId.HasValue)
        {
            requested.Add(paraObject.RoleViewId.Value);
        }

        requested = requested.Distinct().ToList();
        if (requested.Count == 0)
        {
            return null;
        }

        var activeCount = await context.RoleView.CountAsync(r => requested.Contains(r.Id));
        if (activeCount == requested.Count)
        {
            return null;
        }

        Logger.LogInformation("User save rejected because a selected role is deleted or missing. UserId={UserId}", paraObject.Id);
        return "選擇的角色已被刪除或不存在，請關閉視窗、重新開啟後再選擇角色。";
    }

    /// <summary>目前有效的角色：主要角色 ∪ 額外角色，排除已刪除的角色。</summary>
    private static async Task<HashSet<int>> GetActiveRoleIdsAsync(BackendDBContext context, int userId, int? primaryRoleId)
    {
        var ids = await context.UserRole.Where(x => x.MyUserId == userId).Select(x => x.RoleViewId).ToListAsync();
        if (primaryRoleId is { } primary)
        {
            ids.Add(primary);
        }

        return (await context.RoleView.Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync()).ToHashSet();
    }

    /// <summary>這次存檔要寫入的角色（與 <see cref="SyncAssignmentsAsync"/> 相同的組法；角色已先驗證存在且未刪除）。</summary>
    private static HashSet<int> DesiredRoleIds(MyUserAdapterModel paraObject)
    {
        var ids = (paraObject.AdditionalRoleIds ?? []).ToHashSet();
        if (paraObject.RoleViewId is { } primary)
        {
            ids.Add(primary);
        }

        return ids;
    }

    /// <summary>
    /// 雙寫：同步使用者的角色（主要 + 額外，多角色）與團隊（UserTeam）。
    /// 由 Add/Update 呼叫，**必須沿用呼叫端的 context 與交易**，不可自行 CreateDbContext。
    /// </summary>
    private static async Task SyncAssignmentsAsync(
        BackendDBContext context,
        IRbacWriteService rbacWriteService,
        int userId,
        MyUserAdapterModel paraObject)
    {
        var roleIds = new List<int>();
        if (paraObject.RoleViewId.HasValue)
        {
            roleIds.Add(paraObject.RoleViewId.Value);
        }
        if (paraObject.AdditionalRoleIds is not null)
        {
            roleIds.AddRange(paraObject.AdditionalRoleIds);
        }
        await rbacWriteService.SyncUserRolesAsync(context, userId, roleIds.Distinct());

        var teamNames = (paraObject.TeamNames ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct()
            .ToList();
        var teamIds = await context.Team
            .AsNoTracking()
            .Where(t => teamNames.Contains(t.Name))
            .Select(t => t.Id)
            .ToListAsync();
        await rbacWriteService.SyncUserTeamsAsync(context, userId, teamIds);
    }

    /// <summary>載入使用者現有的額外角色（主要角色以外）與團隊名稱，供編輯畫面回填。</summary>
    public async Task<(List<int> AdditionalRoleIds, List<string> TeamNames)> GetUserAssignmentsAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var primaryRoleId = await context.MyUser
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => x.RoleViewId)
            .FirstOrDefaultAsync();

        // 經 context.RoleView Join：已刪除的角色不帶回表單（0.9.95 起）。連結本身由 SyncUserRolesAsync 保留，角色還原後就回來。
        var allRoleIds = await context.UserRole
            .AsNoTracking()
            .Where(x => x.MyUserId == userId)
            .Join(context.RoleView, ur => ur.RoleViewId, r => r.Id, (ur, r) => ur.RoleViewId)
            .ToListAsync();

        var additional = allRoleIds
            .Where(id => !primaryRoleId.HasValue || id != primaryRoleId.Value)
            .Distinct()
            .ToList();

        var teamNames = await context.UserTeam
            .AsNoTracking()
            .Where(x => x.MyUserId == userId)
            .Join(context.Team, ut => ut.TeamId, t => t.Id, (ut, t) => t.Name)
            .ToListAsync();

        return (additional, teamNames);
    }

    /// <summary>
    /// 清單「複製」的範本（0.9.127 起）：帶入主要角色、額外角色、直接綁定的團隊、啟用狀態與管理員身分，
    /// 帳號、名稱、密碼、Email 一律留空，登入狀態（Google、兩步驟驗證、鎖定）不帶。
    /// 已停用的團隊不帶入，放在 SkippedTeamNames 讓畫面註明。來源不存在或是 support 時回 null。
    /// </summary>
    public async Task<(MyUserAdapterModel Draft, List<string> SkippedTeamNames)?> BuildCopyDraftAsync(int sourceUserId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var source = await context.MyUser.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sourceUserId);
        if (source is null || !CanCopy(source.Account))
        {
            Logger.LogInformation("User copy draft rejected. SourceUserId={SourceUserId}, Found={Found}", sourceUserId, source is not null);
            return null;
        }

        var (additionalRoleIds, teamNames) = await GetUserAssignmentsAsync(sourceUserId);
        var enabledTeams = await context.Team.AsNoTracking()
            .Where(t => teamNames.Contains(t.Name) && t.IsEnabled)
            .Select(t => t.Name)
            .ToListAsync();

        var draft = new MyUserAdapterModel
        {
            RoleViewId = source.RoleViewId,
            AdditionalRoleIds = additionalRoleIds,
            TeamNames = teamNames.Where(enabledTeams.Contains).ToList(),
            Status = source.Status,
            IsAdmin = source.IsAdmin,
            MustChangePassword = true,
        };
        return (draft, teamNames.Where(x => !enabledTeams.Contains(x)).ToList());
    }

    /// <summary>畫面用來決定要不要顯示「複製」鈕（0.9.127 起）：support 不可當範本，避免一鍵複製出第二個救援帳號。</summary>
    public bool CanCopy(MyUserAdapterModel user) => CanCopy(user.Account);

    private bool CanCopy(string account) => !string.Equals(account, bootstrapSettings.SupportAccount, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 已使用這個 Email 的帳號（未刪除，不分大小寫、忽略前後空白）。Email 不要求唯一，
    /// 只給「複製」建立前提醒用（0.9.127 起）。
    /// </summary>
    public async Task<List<string>> GetAccountsUsingEmailAsync(string email)
    {
        var normalized = email.Trim().ToLower();
        if (normalized.Length == 0)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.MyUser.AsNoTracking()
            .Where(x => x.Email != null && x.Email.Trim().ToLower() == normalized)
            .OrderBy(x => x.Account)
            .Select(x => x.Account)
            .ToListAsync();
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Deleting user. UserId={UserId}", id);

        try
        {
            MyUser? item = await context.MyUser.FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("User deletion rejected because record was not found. UserId={UserId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的使用者資料。");
            }

            if (GetDeletionBlockReason(item.Account, item.Id) is { } blocked)
            {
                Logger.LogInformation("User deletion rejected for the support account or the current user. UserId={UserId}", id);
                return VerifyRecordResultFactory.Build(false, blocked);
            }

            // 軟刪除（0.9.95 起）：角色與團隊的關聯保留，還原後原樣回來；登入、權限判斷都經全域過濾器而看不到這個人。
            var (actorUserId, actorAccount) = ResolveActor();
            SoftDeleteHelper.MarkDeleted(item, actorAccount);
            item.SecurityStamp = SecurityStamps.New();
            await context.SaveChangesAsync();

            // 密碼重設連結沒有還原的價值；留著也用不了（重設流程查不到已刪除的使用者），直接清掉。
            await context.PasswordResetToken.Where(t => t.MyUserId == id).ExecuteDeleteAsync();

            await auditLogService.WriteAsync(
                AuditActions.User.Delete, success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(MyUser), targetId: id.ToString(),
                detail: $"account={item.Account}");

            Logger.LogInformation("User deleted successfully. UserId={UserId}, Account={Account}", id, item.Account);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("User deletion rejected by concurrency conflict. UserId={UserId}", id);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete user. UserId={UserId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除使用者失敗。", ex);
        }
    }

    /// <summary>畫面用來決定要不要顯示「刪除」鈕；伺服器端的 <see cref="DeleteAsync"/> 仍會再檢查一次。</summary>
    public bool CanDelete(MyUserAdapterModel user) => GetDeletionBlockReason(user.Account, user.Id) is null;

    /// <summary>
    /// support 帳號與自己不可刪除（0.9.95 起）。比對設定檔的 <c>SupportAccount</c>（可改名），不分大小寫：
    /// 多擋一個不會出事，少擋一個就可能刪掉唯一的救援帳號。
    /// </summary>
    private string? GetDeletionBlockReason(string account, int id)
    {
        if (string.Equals(account, bootstrapSettings.SupportAccount, StringComparison.OrdinalIgnoreCase))
        {
            return $"「{account}」是系統預設的開發帳號，不可刪除。";
        }

        var currentUserId = currentUserService.CurrentUser.Id;
        return currentUserId > 0 && currentUserId == id ? "不可刪除自己的帳號。" : null;
    }

    /// <summary>畫面用來決定狀態標籤能不能點（0.9.120 起）；伺服器端的 <see cref="SetStatusAsync"/> 仍會再檢查一次。</summary>
    public bool CanToggleStatus(MyUserAdapterModel user) => !user.Status || GetDisableBlockReason(user.Account, user.Id) is null;

    /// <summary>
    /// support 帳號與自己不可停用（0.9.120 起，清單點標籤與修改窗共用）。support 被停用後重新啟動也不會恢復
    /// （<c>SupportUserSeeder</c> 不動 <c>Status</c>），可能讓所有管理員都進不來；停用自己則會立刻把自己登出。
    /// </summary>
    private string? GetDisableBlockReason(string account, int id)
    {
        if (string.Equals(account, bootstrapSettings.SupportAccount, StringComparison.OrdinalIgnoreCase))
        {
            return $"「{account}」是系統預設的開發帳號，不可停用。";
        }

        var currentUserId = currentUserService.CurrentUser.Id;
        return currentUserId > 0 && currentUserId == id ? "不可停用自己的帳號。" : null;
    }

    /// <summary>
    /// 清單上點狀態標籤切換啟用／停用（0.9.120 起）。⚠️ 不可用清單列呼叫 <see cref="UpdateAsync"/>：它會依畫面模型重寫角色與團隊，
    /// 而清單列沒有這兩項，等於清掉這個人的額外角色與團隊。
    /// 參數是目標值；已經是目標值時回成功、不寫稽核。換工作階段版本（停用者立刻被登出）與版本號（開著的修改窗存檔會衝突）。
    /// </summary>
    public async Task<VerifyRecordResult> SetStatusAsync(int userId, bool enabled)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        try
        {
            var item = await context.MyUser.AsNoTracking()
                .Where(x => x.Id == userId)
                .Select(x => new { x.Account, x.Status })
                .FirstOrDefaultAsync();
            if (item == null)
            {
                Logger.LogInformation("User status change rejected because record was not found. UserId={UserId}", userId);
                return VerifyRecordResultFactory.Build(false, "找不到這位使用者，可能已被刪除。");
            }

            if (!enabled && GetDisableBlockReason(item.Account, userId) is { } blocked)
            {
                Logger.LogInformation("User status change rejected for the support account or the current user. UserId={UserId}", userId);
                return VerifyRecordResultFactory.Build(false, blocked);
            }

            var rows = item.Status == enabled ? 0 : await context.MyUser
                .Where(x => x.Id == userId && x.Status != enabled)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, enabled)
                    .SetProperty(x => x.UpdateAt, DateTime.Now)
                    .SetProperty(x => x.SecurityStamp, SecurityStamps.New())
                    .SetProperty(x => x.ConcurrencyStamp, ConcurrencyStampHelper.New()));
            if (rows == 0)
            {
                return VerifyRecordResultFactory.Build(true);
            }

            var (actorUserId, actorAccount) = ResolveActor();
            await auditLogService.WriteAsync(
                enabled ? AuditActions.User.Enable : AuditActions.User.Disable, success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(MyUser), targetId: userId.ToString(),
                detail: $"account={item.Account}");
            Logger.LogInformation("User status changed. UserId={UserId}, Enabled={Enabled}", userId, enabled);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to change user status. UserId={UserId}, Enabled={Enabled}", userId, enabled);
            return VerifyRecordResultFactory.Build(false, "切換使用者狀態失敗。", ex);
        }
    }

    /// <summary>已刪除的使用者（「顯示已刪除」清單），固定依刪除時間由新到舊。</summary>
    public async Task<DataRequestResult<MyUserAdapterModel>> GetDeletedAsync(DataRequest dataRequest)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        // IgnoreQueryFilters 對整個查詢生效（含 Include）：主要角色若也被刪了，名稱仍會顯示出來。
        IQueryable<MyUser> dataSource = context.MyUser
            .IgnoreQueryFilters([ISoftDeletable.FilterName])
            .AsNoTracking()
            .Include(x => x.RoleView)
            .Where(x => x.IsDeleted);

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            dataSource = dataSource.Where(x =>
                x.Account.Contains(dataRequest.Search) ||
                x.Name.Contains(dataRequest.Search) ||
                (x.Email ?? string.Empty).Contains(dataRequest.Search));
        }

        dataSource = dataSource.OrderByDescending(x => x.DeletedAt).ThenByDescending(x => x.Id);

        var result = new DataRequestResult<MyUserAdapterModel> { Count = await dataSource.CountAsync() };
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        var items = Mapper.Map<List<MyUserAdapterModel>>(await dataSource.ToListAsync());
        foreach (var item in items)
        {
            await OtherDependencyData(item);
        }

        result.Result = items;
        return result;
    }

    public async Task<VerifyRecordResult> RestoreAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Restoring user. UserId={UserId}", id);

        try
        {
            MyUser? item = await context.MyUser
                .IgnoreQueryFilters([ISoftDeletable.FilterName])
                .FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted);

            if (item == null)
            {
                Logger.LogWarning("User restore rejected because deleted record was not found. UserId={UserId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要還原的使用者（可能已被還原或永久刪除）。");
            }

            // 刪除期間帳號可能已被別人使用；比對規則與新增時相同（完全比對，區分大小寫）。
            if (await context.MyUser.AnyAsync(x => x.Id != id && x.Account == item.Account))
            {
                Logger.LogInformation("User restore rejected because an active user has the same account. UserId={UserId}", id);
                return VerifyRecordResultFactory.Build(false, $"已有帳號為「{item.Account}」的使用者，無法還原。請先將現有的那位使用者改用其他帳號後再還原。");
            }

            if (item.GoogleId is { } googleId)
            {
                var linked = await context.MyUser
                    .Where(x => x.Id != id && x.GoogleId == googleId)
                    .Select(x => x.Account)
                    .FirstOrDefaultAsync();
                if (linked is not null)
                {
                    Logger.LogInformation("User restore rejected because another active user is linked to the same Google account. UserId={UserId}", id);
                    return VerifyRecordResultFactory.Build(false, $"這位使用者綁定的 Google 帳號已連結到使用者「{linked}」，無法還原。");
                }
            }

            // 主要角色已被刪除就不能還原：還原後他每次換頁都會被登出。沒有主要角色（null）則允許。
            if (item.RoleViewId is { } roleId && !await context.RoleView.AnyAsync(r => r.Id == roleId))
            {
                var roleName = await context.RoleView
                    .IgnoreQueryFilters([ISoftDeletable.FilterName])
                    .Where(r => r.Id == roleId)
                    .Select(r => r.Name)
                    .FirstOrDefaultAsync();
                Logger.LogInformation("User restore rejected because the primary role is deleted. UserId={UserId}, RoleViewId={RoleViewId}", id, roleId);
                return VerifyRecordResultFactory.Build(false, $"這位使用者的主要角色「{roleName}」已被刪除，無法還原。請先到角色管理還原該角色，再還原這位使用者。");
            }

            SoftDeleteHelper.Restore(item);
            await context.SaveChangesAsync();

            var (actorUserId, actorAccount) = ResolveActor();
            await auditLogService.WriteAsync(
                AuditActions.User.Restore, success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(MyUser), targetId: id.ToString(),
                detail: $"account={item.Account}");

            Logger.LogInformation("User restored successfully. UserId={UserId}, Account={Account}", id, item.Account);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("User restore rejected by concurrency conflict. UserId={UserId}", id);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to restore user. UserId={UserId}", id);
            return VerifyRecordResultFactory.Build(false, "還原使用者失敗。", ex);
        }
    }

    /// <summary>
    /// 永久刪除：只能對已刪除的使用者執行，無法復原；角色、團隊關聯與密碼重設 token 由資料庫 Cascade 一併刪除。
    /// ⚠️ 用追蹤載入後 <c>Remove</c>，不要改成 <c>ExecuteDelete</c> —— 它也會套用軟刪除過濾器，對已刪除的列會刪 0 筆。
    /// </summary>
    public async Task<VerifyRecordResult> PurgeAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Purging user. UserId={UserId}", id);

        try
        {
            MyUser? item = await context.MyUser
                .IgnoreQueryFilters([ISoftDeletable.FilterName])
                .FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted);

            if (item == null)
            {
                Logger.LogWarning("User purge rejected because deleted record was not found. UserId={UserId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要永久刪除的使用者（只能永久刪除已刪除的資料）。");
            }

            context.MyUser.Remove(item);
            await context.SaveChangesAsync();

            var (actorUserId, actorAccount) = ResolveActor();
            await auditLogService.WriteAsync(
                AuditActions.User.Purge, success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(MyUser), targetId: id.ToString(),
                detail: $"account={item.Account}");

            Logger.LogInformation("User purged successfully. UserId={UserId}, Account={Account}", id, item.Account);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("User purge rejected by concurrency conflict. UserId={UserId}", id);
            return VerifyRecordResultFactory.Build(false, "這位使用者已被其他人還原或變更，沒有執行永久刪除。", ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to purge user. UserId={UserId}", id);
            return VerifyRecordResultFactory.Build(false, "永久刪除使用者失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> BeforeAddCheckAsync(MyUserAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Running pre-create validation for Account={Account}", paraObject.Account);

        MyUser? searchItem = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Account == paraObject.Account);

        if (searchItem != null)
        {
            Logger.LogInformation("Pre-create validation failed because account already exists. Account={Account}", paraObject.Account);
            return VerifyRecordResultFactory.Build(false, "帳號已存在，無法新增。");
        }

        return ValidateEmail(paraObject);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(MyUserAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Running pre-update validation for UserId={UserId}, Account={Account}", paraObject.Id, paraObject.Account);

        MyUser? searchItem = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogWarning("Pre-update validation failed because record was not found. UserId={UserId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的使用者資料不存在。");
        }

        searchItem = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Account == paraObject.Account && x.Id != paraObject.Id);

        if (searchItem != null)
        {
            Logger.LogInformation("Pre-update validation failed because account already exists. Account={Account}, UserId={UserId}", paraObject.Account, paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "帳號已存在，無法修改。");
        }

        return ValidateEmail(paraObject);
    }

    /// <summary>
    /// Email 選填，有填就必須是合法格式。規則與 <see cref="PasswordResetService"/> 判斷能否寄重設信的完全相同，
    /// 存得進去就收得到忘記密碼的信。
    /// </summary>
    private VerifyRecordResult ValidateEmail(MyUserAdapterModel paraObject)
    {
        if (!string.IsNullOrWhiteSpace(paraObject.Email) && !MailAddress.TryCreate(paraObject.Email.Trim(), out _))
        {
            Logger.LogInformation("User validation failed because email is invalid. Account={Account}", paraObject.Account);
            return VerifyRecordResultFactory.Build(false, "Email 格式不正確；不使用可留空。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(MyUserAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for UserId={UserId}, Account={Account}", paraObject.Id, paraObject.Account);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    public async Task<VerifyRecordResult> ChangeOwnPasswordAsync(
        int userId,
        string currentPassword,
        string newPassword,
        string confirmPassword)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Changing own password. UserId={UserId}", userId);

        MyUser? user = await context.MyUser
            .FirstOrDefaultAsync(x => x.Id == userId);

        if (user is null)
        {
            Logger.LogWarning("Own password change rejected because user was not found. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, "找不到使用者資料。");
        }

        if (string.Equals(user.Account, MagicObjectHelper.開發者帳號, StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning("Own password change rejected for support account. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, "系統預設開發帳號 support 禁止變更密碼。");
        }

        if (SecurePasswordHasher.VerifyPassword(currentPassword, user.Password, user.Salt) == PasswordVerificationOutcome.Failed)
        {
            Logger.LogWarning("Own password change rejected because current password is invalid. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, "目前密碼不正確。");
        }

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            Logger.LogWarning("Own password change rejected because new password is empty. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, "新密碼不可為空白。");
        }

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            Logger.LogInformation("Own password change rejected because confirmation does not match. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, "新密碼與確認密碼不一致。");
        }

        if ((passwordPolicy.Check(newPassword) ?? (await passwordPolicy.IsReusedAsync(context, user, newPassword) ? PasswordPolicy.ReusedMessage : null)) is { } passwordError)
        {
            Logger.LogInformation("Own password change rejected because the new password does not meet the policy. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, passwordError);
        }

        await passwordPolicy.ApplyAsync(context, user, newPassword, mustChangeAtNextLogin: false);
        user.UpdateAt = DateTime.Now;

        await context.SaveChangesAsync();

        Logger.LogInformation("Own password changed successfully. UserId={UserId}", userId);
        await auditLogService.WriteAsync(
            AuditActions.Password.Changed, success: true, actorUserId: user.Id, actorAccount: user.Account,
            targetType: "MyUser", targetId: user.Id.ToString(), detail: "via=ChangePasswordPage");
        return VerifyRecordResultFactory.Build(true);
    }

    private Task OtherDependencyData(MyUserAdapterModel data)
    {
        data.Password = string.Empty;
        if (data.RoleView is not null)
        {
            data.RoleViewId = data.RoleView.Id;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 管理員解除鎖定（0.9.101 起）：只更新失敗次數與鎖定時間，不換版本號 —— 別人正開著這位使用者的編輯窗也不會因此衝突。
    /// </summary>
    public async Task<VerifyRecordResult> UnlockAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var rows = await context.MyUser
            .Where(x => x.Id == userId && x.LockoutEndUtc != null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AccessFailedCount, 0).SetProperty(x => x.LockoutEndUtc, (DateTime?)null));
        if (rows == 0)
        {
            Logger.LogInformation("Unlock skipped because the user is not locked or was not found. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, "這位使用者目前沒有被鎖定。");
        }

        var (actorUserId, actorAccount) = ResolveActor();
        await auditLogService.WriteAsync(
            AuditActions.User.Unlock, success: true, actorUserId: actorUserId, actorAccount: actorAccount,
            targetType: nameof(MyUser), targetId: userId.ToString());
        Logger.LogInformation("User unlocked. UserId={UserId}", userId);
        return VerifyRecordResultFactory.Build(true);
    }

    /// <summary>
    /// 管理員強制登出（0.9.103 起）：換掉工作階段版本，這個人所有已登入的瀏覽器在下一次換頁（最晚數分鐘）被登出、API 無法再 refresh。
    /// 只更新這一欄，不換 <c>ConcurrencyStamp</c>（別人開著這個人的編輯窗不會衝突）。
    /// </summary>
    public async Task<VerifyRecordResult> ForceLogoutAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var rows = await context.MyUser
            .Where(x => x.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, SecurityStamps.New()));
        if (rows == 0)
        {
            Logger.LogInformation("Force logout skipped because the user was not found. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, "找不到這位使用者。");
        }

        var (actorUserId, actorAccount) = ResolveActor();
        await auditLogService.WriteAsync(
            AuditActions.User.ForceLogout, success: true, actorUserId: actorUserId, actorAccount: actorAccount,
            targetType: nameof(MyUser), targetId: userId.ToString());
        Logger.LogInformation("User sessions revoked. UserId={UserId}", userId);
        return VerifyRecordResultFactory.Build(true);
    }
}
