using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CustomerHelpAgent.AccessDatas;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Services.DataAccess;
using CustomerHelpAgent.Business.Services.Other;

namespace CustomerHelpAgent.Tests;

/// <summary>測試用：組出 <see cref="AfterSalesDecisionService"/>，通知只記錄、不寫資料庫。</summary>
internal static class TestDecisionService
{
    public static AfterSalesDecisionService Create(
        IDbContextFactory<BackendDBContext> factory,
        IAuditLogService audit,
        CurrentUserService currentUser,
        TimeProvider time,
        INotificationSender? sender = null)
        => new(
            factory,
            sender ?? new RecordingNotificationSender(),
            audit,
            currentUser,
            time,
            NullLogger<AfterSalesDecisionService>.Instance);
}
