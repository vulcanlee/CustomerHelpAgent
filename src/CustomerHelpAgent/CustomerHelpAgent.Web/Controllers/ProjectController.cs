using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CustomerHelpAgent.AccessDatas.Models;
using CustomerHelpAgent.Business.Helpers;
using CustomerHelpAgent.Business.Repositories;
using CustomerHelpAgent.Dtos.Commons;
using CustomerHelpAgent.Dtos.Models;
using CustomerHelpAgent.Share.Helpers;
using CustomerHelpAgent.Web.Auth;
using CustomerHelpAgent.Web.Filters;

namespace CustomerHelpAgent.Web.Controllers;

[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class ProjectController : ControllerBase
{
    private readonly ILogger<ProjectController> logger;
    private readonly ProjectRepository projectRepository;
    private readonly IMapper mapper;

    public ProjectController(
        ILogger<ProjectController> logger,
        ProjectRepository projectRepository,
        IMapper mapper)
    {
        this.logger = logger;
        this.projectRepository = projectRepository;
        this.mapper = mapper;
    }

    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<ProjectDto>>> GetById(int id, [FromQuery] bool includeRelatedData = false)
    {
        try
        {
            logger.LogDebug(
                "Received project get request. ProjectId={ProjectId}, IncludeRelatedData={IncludeRelatedData}",
                id,
                includeRelatedData);

            var project = await projectRepository.GetByIdAsync(id, includeRelatedData);

            if (project == null)
            {
                logger.LogInformation("Project get request could not find record. ProjectId={ProjectId}", id);
                return NotFound(ApiResult<ProjectDto>.NotFoundResult($"找不到 ID 為 {id} 的專案"));
            }

            var projectDto = mapper.Map<ProjectDto>(project);
            logger.LogInformation("Project retrieved successfully. ProjectId={ProjectId}", id);

            return Ok(ApiResult<ProjectDto>.SuccessResult(projectDto, "取得專案成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get project. ProjectId={ProjectId}", id);
            return this.ApiServerError<ProjectDto>("取得專案失敗", ex);
        }
    }

    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<ProjectDto>>>> Search([FromBody] ProjectSearchRequestDto request)
    {
        try
        {
            logger.LogDebug(
                "Received project search request. HasKeyword={HasKeyword}, KeywordLength={KeywordLength}, HasOwner={HasOwner}, Status={Status}, Priority={Priority}, PageIndex={PageIndex}, PageSize={PageSize}, SortBy={SortBy}, SortDescending={SortDescending}, IncludeRelatedData={IncludeRelatedData}",
                string.IsNullOrWhiteSpace(request.Keyword) == false,
                request.Keyword?.Length ?? 0,
                string.IsNullOrWhiteSpace(request.Owner) == false,
                request.Status,
                request.Priority,
                request.PageIndex,
                request.PageSize,
                request.SortBy,
                request.SortDescending,
                request.IncludeRelatedData);

            var pagedResult = await projectRepository.GetPagedAsync(request);
            var projectDtos = mapper.Map<List<ProjectDto>>(pagedResult.Items);

            var result = new PagedResult<ProjectDto>
            {
                Items = projectDtos,
                TotalCount = pagedResult.TotalCount,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(pagedResult.TotalCount / (double)request.PageSize)
            };

            logger.LogInformation(
                "Project search completed. ReturnedCount={ReturnedCount}, TotalCount={TotalCount}, PageIndex={PageIndex}, PageSize={PageSize}",
                result.Items.Count,
                result.TotalCount,
                result.PageIndex,
                result.PageSize);

            return Ok(ApiResult<PagedResult<ProjectDto>>.SuccessResult(result, "搜尋專案成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search projects");
            return this.ApiServerError<PagedResult<ProjectDto>>("搜尋專案失敗", ex);
        }
    }

    [HttpPost]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<ProjectDto>>> Create([FromBody] ProjectCreateUpdateDto projectDto)
    {
        try
        {
            logger.LogDebug(
                "Received project create request. Title={Title}, Owner={Owner}, Status={Status}, Priority={Priority}",
                projectDto.Title,
                projectDto.Owner,
                projectDto.Status,
                projectDto.Priority);

            if (await projectRepository.ExistsByNameAsync(projectDto.Title))
            {
                logger.LogInformation(
                    "Project create request rejected because title already exists. Title={Title}",
                    projectDto.Title);
                return Conflict(ApiResult<ProjectDto>.ConflictResult($"專案名稱 '{projectDto.Title}' 已存在"));
            }

            var project = mapper.Map<Project>(projectDto);
            if (await projectRepository.CheckTeamAssignmentAsync(null, project.Teams) is { } teamError)
            {
                logger.LogInformation("Project create request rejected by team assignment rule.");
                return BadRequest(ApiResult<ProjectDto>.ValidationError(teamError));
            }

            var createdProject = await projectRepository.AddAsync(project);
            var createdProjectDto = mapper.Map<ProjectDto>(createdProject);

            logger.LogInformation(
                "Project created successfully. ProjectId={ProjectId}, Title={Title}",
                createdProjectDto.Id,
                createdProjectDto.Title);
            await this.WriteAuditAsync(AuditActions.Project.Create, "Project", createdProjectDto.Id.ToString(), $"title={createdProjectDto.Title}");

            return Ok(ApiResult<ProjectDto>.SuccessResult(createdProjectDto, "新增專案成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create project. Title={Title}", projectDto.Title);
            return this.ApiServerError<ProjectDto>("新增專案失敗", ex);
        }
    }

    [HttpPut("{id}")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] ProjectCreateUpdateDto projectDto)
    {
        try
        {
            logger.LogDebug(
                "Received project update request. RouteProjectId={RouteProjectId}, PayloadProjectId={PayloadProjectId}, Title={Title}",
                id,
                projectDto.Id,
                projectDto.Title);

            if (id != projectDto.Id)
            {
                logger.LogWarning(
                    "Project update request rejected because route id and payload id do not match. RouteProjectId={RouteProjectId}, PayloadProjectId={PayloadProjectId}",
                    id,
                    projectDto.Id);
                return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
            }

            // 樂觀並行（0.9.93 起）：PUT 必須帶上 GET 取得的版本號，否則無法判斷是否覆蓋了別人的修改。
            if (string.IsNullOrWhiteSpace(projectDto.ConcurrencyStamp))
            {
                logger.LogInformation("Project update request rejected because concurrency stamp is missing. ProjectId={ProjectId}", id);
                return BadRequest(ApiResult.ValidationError("ConcurrencyStamp 為必填：請帶上 GET 取得的版本號（用來避免覆蓋別人的修改）。"));
            }

            if (await projectRepository.ExistsByNameAsync(projectDto.Title, id))
            {
                logger.LogInformation(
                    "Project update request rejected because title is already in use. ProjectId={ProjectId}, Title={Title}",
                    id,
                    projectDto.Title);
                return Conflict(ApiResult.ConflictResult($"專案名稱 '{projectDto.Title}' 已被其他專案使用"));
            }

            var project = mapper.Map<Project>(projectDto);
            if (await projectRepository.CheckTeamAssignmentAsync(id, project.Teams) is { } teamError)
            {
                logger.LogInformation("Project update request rejected by team assignment rule. ProjectId={ProjectId}", id);
                return BadRequest(ApiResult.ValidationError(teamError));
            }

            var outcome = await projectRepository.UpdateAsync(project);

            if (!outcome.Updated)
            {
                logger.LogWarning("Project update request could not find record. ProjectId={ProjectId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的專案"));
            }

            logger.LogInformation("Project updated successfully. ProjectId={ProjectId}, Title={Title}", id, projectDto.Title);
            await this.WriteAuditAsync(AuditActions.Project.Update, "Project", id.ToString(), $"title={projectDto.Title}", content: outcome.Diff);
            return Ok(ApiResult.SuccessResult("更新專案成功"));
        }
        catch (DbUpdateConcurrencyException)
        {
            // 別人在這段期間先更新或刪除了這筆：使用者情境（LOG-11），記 Information、不進系統例外紀錄。
            logger.LogInformation("Project update request rejected by concurrency conflict. ProjectId={ProjectId}", id);
            return Conflict(ApiResult.ConflictResult(ConcurrencyStampHelper.ConflictMessage));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update project. ProjectId={ProjectId}", id);
            return this.ApiServerError("更新專案失敗", ex);
        }
    }

    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            logger.LogDebug("Received project delete request. ProjectId={ProjectId}", id);

            // 軟刪除（0.9.94 起），刪除者記入 DeletedBy。
            var success = await projectRepository.DeleteAsync(id, RequestActorResolver.Resolve(User).Account);

            if (!success)
            {
                logger.LogWarning("Project delete request could not find record. ProjectId={ProjectId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的專案"));
            }

            logger.LogInformation("Project deleted successfully. ProjectId={ProjectId}", id);
            await this.WriteAuditAsync(AuditActions.Project.Delete, "Project", id.ToString());
            return Ok(ApiResult.SuccessResult("刪除專案成功"));
        }
        catch (Exception ex)
        {
            // 0.9.94 起刪除是軟刪除，不再有「仍有關聯資料（FK）」的情況，原本攔截 FK 錯誤的分支已移除。
            logger.LogError(ex, "Failed to delete project. ProjectId={ProjectId}", id);
            return this.ApiServerError("刪除專案失敗", ex);
        }
    }
}
