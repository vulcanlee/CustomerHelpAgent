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
public class TeamController : ControllerBase
{
    private readonly ILogger<TeamController> logger;
    private readonly TeamRepository teamRepository;
    private readonly IMapper mapper;

    public TeamController(
        ILogger<TeamController> logger,
        TeamRepository teamRepository,
        IMapper mapper)
    {
        this.logger = logger;
        this.teamRepository = teamRepository;
        this.mapper = mapper;
    }

    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<TeamDto>>> GetById(int id)
    {
        try
        {
            logger.LogDebug("Received team get request. TeamId={TeamId}", id);

            var team = await teamRepository.GetByIdAsync(id);
            if (team == null)
            {
                logger.LogInformation("Team get request could not find record. TeamId={TeamId}", id);
                return NotFound(ApiResult<TeamDto>.NotFoundResult($"找不到 ID 為 {id} 的團隊"));
            }

            var teamDto = mapper.Map<TeamDto>(team);
            logger.LogInformation("Team retrieved successfully. TeamId={TeamId}", id);
            return Ok(ApiResult<TeamDto>.SuccessResult(teamDto, "取得團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get team. TeamId={TeamId}", id);
            return this.ApiServerError<TeamDto>("取得團隊失敗", ex);
        }
    }

    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<TeamDto>>>> Search([FromBody] TeamSearchRequestDto request)
    {
        try
        {
            logger.LogDebug(
                "Received team search request. HasKeyword={HasKeyword}, KeywordLength={KeywordLength}, IsEnabled={IsEnabled}, PageIndex={PageIndex}, PageSize={PageSize}, SortBy={SortBy}, SortDescending={SortDescending}",
                string.IsNullOrWhiteSpace(request.Keyword) == false,
                request.Keyword?.Length ?? 0,
                request.IsEnabled,
                request.PageIndex,
                request.PageSize,
                request.SortBy,
                request.SortDescending);

            var pagedResult = await teamRepository.GetPagedAsync(request);
            var teamDtos = mapper.Map<List<TeamDto>>(pagedResult.Items);

            var result = new PagedResult<TeamDto>
            {
                Items = teamDtos,
                TotalCount = pagedResult.TotalCount,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(pagedResult.TotalCount / (double)request.PageSize)
            };

            logger.LogInformation(
                "Team search completed. ReturnedCount={ReturnedCount}, TotalCount={TotalCount}",
                result.Items.Count,
                result.TotalCount);

            return Ok(ApiResult<PagedResult<TeamDto>>.SuccessResult(result, "搜尋團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search teams");
            return this.ApiServerError<PagedResult<TeamDto>>("搜尋團隊失敗", ex);
        }
    }

    [HttpPost]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<TeamDto>>> Create([FromBody] TeamCreateUpdateDto teamDto)
    {
        try
        {
            logger.LogDebug("Received team create request. Name={Name}, Code={Code}", teamDto.Name, teamDto.Code);

            if (await teamRepository.ExistsByNameAsync(teamDto.Name))
            {
                logger.LogInformation("Team create request rejected because name already exists. Name={Name}", teamDto.Name);
                return Conflict(ApiResult<TeamDto>.ConflictResult($"團隊名稱 '{teamDto.Name}' 已存在"));
            }

            if (!string.IsNullOrWhiteSpace(teamDto.Code) && await teamRepository.ExistsByCodeAsync(teamDto.Code))
            {
                logger.LogInformation("Team create request rejected because code already exists. Code={Code}", teamDto.Code);
                return Conflict(ApiResult<TeamDto>.ConflictResult($"團隊代號 '{teamDto.Code}' 已存在"));
            }

            var team = mapper.Map<Team>(teamDto);
            var outcome = await teamRepository.AddAsync(team);
            if (outcome.Status == TeamWriteStatus.Invalid)
            {
                logger.LogInformation("Team create request rejected by parent rule. ParentId={ParentId}", teamDto.ParentId);
                return BadRequest(ApiResult<TeamDto>.ValidationError(outcome.Message!));
            }

            var createdDto = mapper.Map<TeamDto>(outcome.Team);

            logger.LogInformation("Team created successfully. TeamId={TeamId}, Name={Name}", createdDto.Id, createdDto.Name);
            await this.WriteAuditAsync(AuditActions.Team.Create, "Team", createdDto.Id.ToString(), $"name={createdDto.Name}; parentId={createdDto.ParentId}");
            return Ok(ApiResult<TeamDto>.SuccessResult(createdDto, "新增團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create team. Name={Name}", teamDto.Name);
            return this.ApiServerError<TeamDto>("新增團隊失敗", ex);
        }
    }

    [HttpPut("{id}")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] TeamCreateUpdateDto teamDto)
    {
        try
        {
            logger.LogDebug("Received team update request. RouteId={RouteId}, PayloadId={PayloadId}, Name={Name}", id, teamDto.Id, teamDto.Name);

            if (id != teamDto.Id)
            {
                logger.LogWarning("Team update request rejected because route id and payload id do not match. RouteId={RouteId}, PayloadId={PayloadId}", id, teamDto.Id);
                return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
            }

            // 樂觀並行（0.9.93 起）：PUT 必須帶上 GET 取得的版本號，否則無法判斷是否覆蓋了別人的修改。
            if (string.IsNullOrWhiteSpace(teamDto.ConcurrencyStamp))
            {
                logger.LogInformation("Team update request rejected because concurrency stamp is missing. TeamId={TeamId}", id);
                return BadRequest(ApiResult.ValidationError("ConcurrencyStamp 為必填：請帶上 GET 取得的版本號（用來避免覆蓋別人的修改）。"));
            }

            if (await teamRepository.ExistsByNameAsync(teamDto.Name, id))
            {
                logger.LogInformation("Team update request rejected because name is already in use. TeamId={TeamId}, Name={Name}", id, teamDto.Name);
                return Conflict(ApiResult.ConflictResult($"團隊名稱 '{teamDto.Name}' 已被其他團隊使用"));
            }

            if (!string.IsNullOrWhiteSpace(teamDto.Code) && await teamRepository.ExistsByCodeAsync(teamDto.Code, id))
            {
                logger.LogInformation("Team update request rejected because code is already in use. TeamId={TeamId}, Code={Code}", id, teamDto.Code);
                return Conflict(ApiResult.ConflictResult($"團隊代號 '{teamDto.Code}' 已被其他團隊使用"));
            }

            var team = mapper.Map<Team>(teamDto);
            var outcome = await teamRepository.UpdateAsync(team);
            if (outcome.Status == TeamWriteStatus.NotFound)
            {
                logger.LogWarning("Team update request could not find record. TeamId={TeamId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的團隊"));
            }

            if (outcome.Status == TeamWriteStatus.Invalid)
            {
                logger.LogInformation("Team update request rejected by team rule. TeamId={TeamId}", id);
                return BadRequest(ApiResult.ValidationError(outcome.Message!));
            }

            logger.LogInformation("Team updated successfully. TeamId={TeamId}, Name={Name}", id, teamDto.Name);
            await this.WriteAuditAsync(AuditActions.Team.Update, "Team", id.ToString(),
                $"name={teamDto.Name}; parentId={teamDto.ParentId}" + (outcome.Message is { } renamed ? $"; {renamed}" : string.Empty),
                content: outcome.Diff);
            return Ok(ApiResult.SuccessResult("更新團隊成功"));
        }
        catch (DbUpdateConcurrencyException)
        {
            // 別人在這段期間先更新或刪除了這筆：使用者情境（LOG-11），記 Information、不進系統例外紀錄。
            logger.LogInformation("Team update request rejected by concurrency conflict. TeamId={TeamId}", id);
            return Conflict(ApiResult.ConflictResult(ConcurrencyStampHelper.ConflictMessage));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update team. TeamId={TeamId}", id);
            return this.ApiServerError("更新團隊失敗", ex);
        }
    }

    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            logger.LogDebug("Received team delete request. TeamId={TeamId}", id);

            // 軟刪除（0.9.94 起），刪除者記入 DeletedBy。
            var outcome = await teamRepository.DeleteAsync(id, RequestActorResolver.Resolve(User).Account);
            if (outcome.Status == TeamWriteStatus.NotFound)
            {
                logger.LogWarning("Team delete request could not find record. TeamId={TeamId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的團隊"));
            }

            if (outcome.Status == TeamWriteStatus.Invalid)
            {
                logger.LogInformation("Team delete request rejected because it still has child teams. TeamId={TeamId}", id);
                return BadRequest(ApiResult.ValidationError(outcome.Message!));
            }

            logger.LogInformation("Team deleted successfully. TeamId={TeamId}", id);
            await this.WriteAuditAsync(AuditActions.Team.Delete, "Team", id.ToString());
            return Ok(ApiResult.SuccessResult("刪除團隊成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete team. TeamId={TeamId}", id);
            return this.ApiServerError("刪除團隊失敗", ex);
        }
    }
}
