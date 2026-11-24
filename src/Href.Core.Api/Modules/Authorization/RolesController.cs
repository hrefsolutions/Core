using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Authorization;

/// <summary>
/// Admin endpoints for the roles of one application. Requires the <c>Admin</c> policy (bearer token with <c>core.admin</c>).
/// </summary>
[ApiController]
[Route("api/apps/{appKey}/roles")]
[Authorize(Policy = AdminAuthentication.PolicyName)]
public sealed class RolesController(RoleAdminService roleService) : ControllerBase
{
    /// <summary>
    /// Lists the roles of the application ordered by <c>name</c> (ordinal), with their permission keys.
    /// </summary>
    /// <response code="200">The roles of the application.</response>
    /// <response code="404">`Applications.NotFound`.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<RoleResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> List(string appKey, CancellationToken cancellationToken)
    {
        var result = await roleService.ListAsync(appKey, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Returns one role of the application.
    /// </summary>
    /// <response code="200">The role.</response>
    /// <response code="404">`Applications.NotFound`, or `Authorization.RoleNotFound` (also for a role of another application).</response>
    [HttpGet("{roleId:guid}")]
    [ProducesResponseType<RoleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string appKey, Guid roleId, CancellationToken cancellationToken)
    {
        var result = await roleService.GetAsync(appKey, roleId, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Creates a role in the application; <c>isDefault: true</c> clears the previous default role.
    /// </summary>
    /// <response code="201">The created role; `Location` points to `GET /api/apps/{appKey}/roles/{roleId}`.</response>
    /// <response code="400">`Authorization.InvalidRoleName`, `Authorization.InvalidDescription`, `Authorization.InvalidPermissionKeys` or `Authorization.UnknownPermissions` (listed); a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="404">`Applications.NotFound`.</response>
    /// <response code="409">`Authorization.RoleNameTaken` (ignoring case).</response>
    [HttpPost]
    [ProducesResponseType<RoleResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Create(string appKey, [FromBody] CreateRoleRequest request, CancellationToken cancellationToken)
    {
        var result = await roleService.CreateAsync(appKey, request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : CreatedAtAction(nameof(Get), new { appKey, roleId = result.Value.Id }, result.Value);
    }

    /// <summary>
    /// Replaces the <c>name</c>, <c>description</c> and <c>isDefault</c> of a role.
    /// </summary>
    /// <response code="200">The updated role.</response>
    /// <response code="400">`Authorization.InvalidRoleName` or `Authorization.InvalidDescription`; a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="404">`Applications.NotFound` or `Authorization.RoleNotFound`.</response>
    /// <response code="409">`Authorization.RoleNameTaken` (ignoring case).</response>
    [HttpPut("{roleId:guid}")]
    [ProducesResponseType<RoleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
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

    /// <summary>
    /// Replaces the permissions of a role.
    /// </summary>
    /// <response code="200">The role with its new permissions.</response>
    /// <response code="400">`Authorization.InvalidPermissionKeys` or `Authorization.UnknownPermissions` (listed); a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="404">`Applications.NotFound` or `Authorization.RoleNotFound`.</response>
    /// <response code="409">`Authorization.Reserved`: the admin role of the `core` application must keep `core.admin`.</response>
    [HttpPut("{roleId:guid}/permissions")]
    [ProducesResponseType<RoleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
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

    /// <summary>
    /// Deletes a role and removes it from every user that held it.
    /// </summary>
    /// <response code="204">The role was deleted.</response>
    /// <response code="404">`Applications.NotFound` or `Authorization.RoleNotFound`.</response>
    /// <response code="409">`Authorization.Reserved`: the admin role of the `core` application cannot be deleted.</response>
    [HttpDelete("{roleId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Delete(string appKey, Guid roleId, CancellationToken cancellationToken)
    {
        var result = await roleService.DeleteAsync(appKey, roleId, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : NoContent();
    }
}
