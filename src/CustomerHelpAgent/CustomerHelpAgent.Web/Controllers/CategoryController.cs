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
public class CategoryController : ControllerBase
{
    private readonly ILogger<CategoryController> logger;
    private readonly CategoryRepository categoryRepository;
    private readonly IMapper mapper;

    public CategoryController(
        ILogger<CategoryController> logger,
        CategoryRepository categoryRepository,
        IMapper mapper)
    {
        this.logger = logger;
        this.categoryRepository = categoryRepository;
        this.mapper = mapper;
    }

    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<CategoryDto>>> GetById(int id)
    {
        try
        {
            logger.LogDebug("Received category get request. CategoryId={CategoryId}", id);

            var category = await categoryRepository.GetByIdAsync(id);
            if (category == null)
            {
                logger.LogInformation("Category get request could not find record. CategoryId={CategoryId}", id);
                return NotFound(ApiResult<CategoryDto>.NotFoundResult($"找不到 ID 為 {id} 的分類"));
            }

            var categoryDto = mapper.Map<CategoryDto>(category);
            logger.LogInformation("Category retrieved successfully. CategoryId={CategoryId}", id);
            return Ok(ApiResult<CategoryDto>.SuccessResult(categoryDto, "取得分類成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get category. CategoryId={CategoryId}", id);
            return this.ApiServerError<CategoryDto>("取得分類失敗", ex);
        }
    }

    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<CategoryDto>>>> Search([FromBody] CategorySearchRequestDto request)
    {
        try
        {
            logger.LogDebug(
                "Received category search request. HasKeyword={HasKeyword}, KeywordLength={KeywordLength}, IsEnabled={IsEnabled}, PageIndex={PageIndex}, PageSize={PageSize}, SortBy={SortBy}, SortDescending={SortDescending}",
                string.IsNullOrWhiteSpace(request.Keyword) == false,
                request.Keyword?.Length ?? 0,
                request.IsEnabled,
                request.PageIndex,
                request.PageSize,
                request.SortBy,
                request.SortDescending);

            var pagedResult = await categoryRepository.GetPagedAsync(request);
            var categoryDtos = mapper.Map<List<CategoryDto>>(pagedResult.Items);

            var result = new PagedResult<CategoryDto>
            {
                Items = categoryDtos,
                TotalCount = pagedResult.TotalCount,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(pagedResult.TotalCount / (double)request.PageSize)
            };

            logger.LogInformation(
                "Category search completed. ReturnedCount={ReturnedCount}, TotalCount={TotalCount}",
                result.Items.Count,
                result.TotalCount);

            return Ok(ApiResult<PagedResult<CategoryDto>>.SuccessResult(result, "搜尋分類成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search categories");
            return this.ApiServerError<PagedResult<CategoryDto>>("搜尋分類失敗", ex);
        }
    }

    [HttpPost]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<CategoryDto>>> Create([FromBody] CategoryCreateUpdateDto categoryDto)
    {
        try
        {
            logger.LogDebug("Received category create request. Name={Name}", categoryDto.Name);

            if (await categoryRepository.ExistsByNameAsync(categoryDto.Name))
            {
                logger.LogInformation("Category create request rejected because name already exists. Name={Name}", categoryDto.Name);
                return Conflict(ApiResult<CategoryDto>.ConflictResult($"分類名稱 '{categoryDto.Name}' 已存在"));
            }

            var category = mapper.Map<Category>(categoryDto);
            if (await categoryRepository.CheckTeamAssignmentAsync(null, category.Teams) is { } teamError)
            {
                logger.LogInformation("Category create request rejected by team assignment rule.");
                return BadRequest(ApiResult<CategoryDto>.ValidationError(teamError));
            }

            var created = await categoryRepository.AddAsync(category);
            var createdDto = mapper.Map<CategoryDto>(created);

            logger.LogInformation("Category created successfully. CategoryId={CategoryId}, Name={Name}", createdDto.Id, createdDto.Name);
            await this.WriteAuditAsync(AuditActions.Category.Create, "Category", createdDto.Id.ToString(), $"name={createdDto.Name}");
            return Ok(ApiResult<CategoryDto>.SuccessResult(createdDto, "新增分類成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create category. Name={Name}", categoryDto.Name);
            return this.ApiServerError<CategoryDto>("新增分類失敗", ex);
        }
    }

    [HttpPut("{id}")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] CategoryCreateUpdateDto categoryDto)
    {
        try
        {
            logger.LogDebug("Received category update request. RouteId={RouteId}, PayloadId={PayloadId}, Name={Name}", id, categoryDto.Id, categoryDto.Name);

            if (id != categoryDto.Id)
            {
                logger.LogWarning("Category update request rejected because route id and payload id do not match. RouteId={RouteId}, PayloadId={PayloadId}", id, categoryDto.Id);
                return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
            }

            // 樂觀並行（0.9.93 起）：PUT 必須帶上 GET 取得的版本號，否則無法判斷是否覆蓋了別人的修改。
            if (string.IsNullOrWhiteSpace(categoryDto.ConcurrencyStamp))
            {
                logger.LogInformation("Category update request rejected because concurrency stamp is missing. CategoryId={CategoryId}", id);
                return BadRequest(ApiResult.ValidationError("ConcurrencyStamp 為必填：請帶上 GET 取得的版本號（用來避免覆蓋別人的修改）。"));
            }

            if (await categoryRepository.ExistsByNameAsync(categoryDto.Name, id))
            {
                logger.LogInformation("Category update request rejected because name is already in use. CategoryId={CategoryId}, Name={Name}", id, categoryDto.Name);
                return Conflict(ApiResult.ConflictResult($"分類名稱 '{categoryDto.Name}' 已被其他分類使用"));
            }

            var category = mapper.Map<Category>(categoryDto);
            if (await categoryRepository.CheckTeamAssignmentAsync(id, category.Teams) is { } teamError)
            {
                logger.LogInformation("Category update request rejected by team assignment rule. CategoryId={CategoryId}", id);
                return BadRequest(ApiResult.ValidationError(teamError));
            }

            var outcome = await categoryRepository.UpdateAsync(category);
            if (!outcome.Updated)
            {
                logger.LogWarning("Category update request could not find record. CategoryId={CategoryId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的分類"));
            }

            logger.LogInformation("Category updated successfully. CategoryId={CategoryId}, Name={Name}", id, categoryDto.Name);
            await this.WriteAuditAsync(AuditActions.Category.Update, "Category", id.ToString(), $"name={categoryDto.Name}", content: outcome.Diff);
            return Ok(ApiResult.SuccessResult("更新分類成功"));
        }
        catch (DbUpdateConcurrencyException)
        {
            // 別人在這段期間先更新或刪除了這筆：使用者情境（LOG-11），記 Information、不進系統例外紀錄。
            logger.LogInformation("Category update request rejected by concurrency conflict. CategoryId={CategoryId}", id);
            return Conflict(ApiResult.ConflictResult(ConcurrencyStampHelper.ConflictMessage));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update category. CategoryId={CategoryId}", id);
            return this.ApiServerError("更新分類失敗", ex);
        }
    }

    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        try
        {
            logger.LogDebug("Received category delete request. CategoryId={CategoryId}", id);

            // 軟刪除（0.9.94 起），刪除者記入 DeletedBy。
            var success = await categoryRepository.DeleteAsync(id, RequestActorResolver.Resolve(User).Account);
            if (!success)
            {
                logger.LogWarning("Category delete request could not find record. CategoryId={CategoryId}", id);
                return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的分類"));
            }

            logger.LogInformation("Category deleted successfully. CategoryId={CategoryId}", id);
            await this.WriteAuditAsync(AuditActions.Category.Delete, "Category", id.ToString());
            return Ok(ApiResult.SuccessResult("刪除分類成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete category. CategoryId={CategoryId}", id);
            return this.ApiServerError("刪除分類失敗", ex);
        }
    }
}
