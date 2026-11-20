using Href.Core.Api.Common.Web;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Authorization;

/// <summary>
/// Admin endpoints for the permissions of one application. Public until admin authentication exists (#53).
/// </summary>
[ApiController]
[Route("api/apps/{appKey}/permissions")]
public sealed class PermissionsController(PermissionAdminService permissionService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string appKey, CancellationToken cancellationToken)
    {
        var result = await permissionService.ListAsync(appKey, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string appKey, string key, CancellationToken cancellationToken)
    {
        var result = await permissionService.GetAsync(appKey, key, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        string appKey,
        [FromBody] CreatePermissionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await permissionService.CreateAsync(appKey, request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : CreatedAtAction(nameof(Get), new { appKey, key = result.Value.Key }, result.Value);
    }

    [HttpPut("{key}")]
    public async Task<IActionResult> Update(
        string appKey,
        string key,
        [FromBody] UpdatePermissionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await permissionService.UpdateAsync(appKey, key, request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    [HttpDelete("{key}")]
    public async Task<IActionResult> Delete(string appKey, string key, CancellationToken cancellationToken)
    {
        var result = await permissionService.DeleteAsync(appKey, key, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : NoContent();
    }
}
