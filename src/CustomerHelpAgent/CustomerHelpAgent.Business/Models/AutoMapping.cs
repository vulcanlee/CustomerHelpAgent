using AutoMapper;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Dtos.Models;
using CustomerHelpAgent.Models.AdapterModel;
using CustomerHelpAgent.Models.Others;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace CustomerHelpAgent.Models.Systems;

public class AutoMapping : Profile
{
    public AutoMapping()
    {
        #region Blazor AdapterModel

        #region Project
        CreateMap<Project, ProjectAdapterModel>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToList(s.Categories)))
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToList(s.Teams)));
        CreateMap<ProjectAdapterModel, Project>().IgnoreSoftDeleteFields()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToStored(s.Categories)))
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToStored(s.Teams)));
        CreateMap<Project, ProjectDto>();
        CreateMap<ProjectDto, Project>().IgnoreSoftDeleteFields();
        CreateMap<Project, ProjectCreateUpdateDto>();
        CreateMap<ProjectCreateUpdateDto, Project>().IgnoreSoftDeleteFields();
        CreateMap<ProjectFile, ProjectFileAdapterModel>();
        CreateMap<ProjectFileAdapterModel, ProjectFile>();
        #endregion

        #region RoleView
        CreateMap<RoleView, RoleViewAdapterModel>()
            .ForMember(d => d.DefaultTeams, o => o.MapFrom(s => TeamJsonHelper.Deserialize(s.DefaultTeamsJson)));
        CreateMap<RoleViewAdapterModel, RoleView>().IgnoreSoftDeleteFields()
            .ForMember(d => d.DefaultTeamsJson, o => o.MapFrom(s => TeamJsonHelper.Serialize(s.DefaultTeams)));
        #endregion

        // 所有「→ Entity」的映射都以 NameNormalizer 正規化名稱／代號，
        // 讓不論從 Blazor 或 Web API 進來，寫進資料庫的值都已去除前後空白、
        // 空白代號歸一成 null。唯一性檢查才不會出現「比對的與儲存的不是同一個字串」。
        #region Category
        CreateMap<Category, CategoryAdapterModel>()
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToList(s.Teams)));
        CreateMap<CategoryAdapterModel, Category>().IgnoreSoftDeleteFields()
            .ForMember(d => d.Name, o => o.MapFrom(s => NameNormalizer.Normalize(s.Name)))
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToStored(s.Teams)));
        CreateMap<Category, CategoryDto>();
        CreateMap<CategoryDto, Category>().IgnoreSoftDeleteFields()
            .ForMember(d => d.Name, o => o.MapFrom(s => NameNormalizer.Normalize(s.Name)));
        CreateMap<Category, CategoryCreateUpdateDto>();
        CreateMap<CategoryCreateUpdateDto, Category>().IgnoreSoftDeleteFields()
            .ForMember(d => d.Name, o => o.MapFrom(s => NameNormalizer.Normalize(s.Name)));
        #endregion

        #region Team
        CreateMap<Team, TeamAdapterModel>();
        CreateMap<TeamAdapterModel, Team>().IgnoreSoftDeleteFields()
            .ForMember(d => d.Name, o => o.MapFrom(s => NameNormalizer.Normalize(s.Name)))
            .ForMember(d => d.Code, o => o.MapFrom(s => NameNormalizer.NormalizeOptional(s.Code)));
        CreateMap<Team, TeamDto>();
        CreateMap<TeamDto, Team>().IgnoreSoftDeleteFields()
            .ForMember(d => d.Name, o => o.MapFrom(s => NameNormalizer.Normalize(s.Name)))
            .ForMember(d => d.Code, o => o.MapFrom(s => NameNormalizer.NormalizeOptional(s.Code)));
        CreateMap<Team, TeamCreateUpdateDto>();
        CreateMap<TeamCreateUpdateDto, Team>().IgnoreSoftDeleteFields()
            .ForMember(d => d.Name, o => o.MapFrom(s => NameNormalizer.Normalize(s.Name)))
            .ForMember(d => d.Code, o => o.MapFrom(s => NameNormalizer.NormalizeOptional(s.Code)));
        #endregion

        #region TokenUsageLog
        // Token 用量為唯讀頁面，只需要 Entity → AdapterModel 單向映射。
        CreateMap<TokenUsageLog, TokenUsageLogAdapterModel>();
        #endregion

        #region AiCallLog
        // AI 對話紀錄為唯讀頁面，只需要 Entity → AdapterModel 單向映射；內文在內容檔，不在這裡。
        CreateMap<AiCallLog, AiCallLogAdapterModel>()
            .ForMember(d => d.HasContent, o => o.MapFrom(s => s.ContentFile != null));
        #endregion

        #region ExceptionLog
        // 系統例外紀錄為唯讀頁面，只需要 Entity → AdapterModel 單向映射。
        CreateMap<ExceptionLog, ExceptionLogAdapterModel>();
        #endregion

        #region AuditLog
        // 稽核紀錄為唯讀頁面，只需要 Entity → AdapterModel 單向映射。
        // ⚠️ OccurredAt 在資料表是 UTC，映射只做原值搬運；UTC → 本地的換算
        //    統一由 AuditLogQueryService 在映射之後處理，不要在這裡加 ConvertUsing。
        CreateMap<AuditLog, AuditLogAdapterModel>();

        // 排程作業的狀態與執行紀錄（0.9.96 起）：唯讀，只需要 Entity → AdapterModel。
        CreateMap<JobRun, JobRunAdapterModel>();
        CreateMap<ScheduledJobState, ScheduledJobStateAdapterModel>();
        #endregion

        #region MyUser
        CreateMap<MyUser, MyUserAdapterModel>()
            .ForMember(dest => dest.HasLocalPassword, opt => opt.MapFrom(src => !string.IsNullOrEmpty(src.Password)));
        // 鎖定與密碼設定時間只由登入、解鎖與密碼原則寫入，畫面模型上的值不寫回實體（0.9.101 起）。
        CreateMap<MyUserAdapterModel, MyUser>().IgnoreSoftDeleteFields()
            .ForMember(dest => dest.LockoutEndUtc, opt => opt.Ignore())
            .ForMember(dest => dest.PasswordChangedAtUtc, opt => opt.Ignore())
            .ForMember(dest => dest.SecurityStamp, opt => opt.Ignore())
            .ForMember(dest => dest.TwoFactorEnabled, opt => opt.Ignore())
            .ForMember(dest => dest.TwoFactorSecret, opt => opt.Ignore())
            .ForMember(dest => dest.TwoFactorLastStep, opt => opt.Ignore());
        CreateMap<MyUserAdapterModel, CurrentUser>()
            .ForMember(dest => dest.RoleJson, opt => opt.Ignore())
            .ForMember(dest => dest.RoleList, opt => opt.Ignore())
            .ForMember(dest => dest.IsAuthenticated, opt => opt.Ignore());
        #endregion
        #endregion
    }
}

/// <summary>
/// 「→ 實體」的對應一律不帶軟刪除欄位：畫面模型與 DTO 上的值（或預設值）不能決定一筆資料是否已刪除，
/// 刪除與還原只能經由服務的刪除／還原方法（見 SoftDeleteHelper）。
/// </summary>
internal static class SoftDeleteMappingExtensions
{
    public static IMappingExpression<TSource, TEntity> IgnoreSoftDeleteFields<TSource, TEntity>(this IMappingExpression<TSource, TEntity> map)
        where TEntity : ISoftDeletable
        => map
            .ForMember(d => d.IsDeleted, o => o.Ignore())
            .ForMember(d => d.DeletedAt, o => o.Ignore())
            .ForMember(d => d.DeletedBy, o => o.Ignore());
}
