using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Authorization;

/// <summary>
/// Admin endpoints for the roles of one application. Public until admin authentication exists (#53).
/// </summary>
[ApiController]
[Route("api/apps/{appKey}/roles")]
[Authorize(Policy = AdminAuthentication.PolicyName)]
public sealed class RolesController(RoleAdminService roleService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string appKey, CancellationToken cancellationToken)
    {
        var result = await roleService.ListAsync(appKey, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    [HttpGet("{roleId:guid}")]
    public async Task<IActionResult> Get(string appKey, Guid roleId, CancellationToken cancellationToken)
    {
        var result = await roleService.GetAsync(appKey, roleId, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    [HttpPost]
    public async Task<IActionResult> Create(string appKey, [FromBody] CreateRoleRequest request, CancellationToken cancellationToken)
    {
        var result = await roleService.CreateAsync(appKey, request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : CreatedAtAction(nameof(Get), new { appKey, roleId = result.Value.Id }, result.Value);
    }

    [HttpPut("{roleId:guid}")]
    public async Task<IActionResult> Update(
        string appKey,
        Guid roleId,
        [FromBody] UpdateRoleRequest request,
        CancellationToken cancellationToken)
    {
        var result = await roleService.UpdateAsync(appKey, roleId, request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    [HttpPut("{roleId:guid}/permissions")]
    public async Task<IActionResult> ReplacePermissions(
        string appKey,
        Guid roleId,
        [FromBody] ReplaceRolePermissionsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await roleService.ReplacePermissionsAsync(appKey, roleId, request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    [HttpDelete("{roleId:guid}")]
    public async Task<IActionResult> Delete(string appKey, Guid roleId, CancellationToken cancellationToken)
    {
        var result = await roleService.DeleteAsync(appKey, roleId, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : NoContent();
    }
}
