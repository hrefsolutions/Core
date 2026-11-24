using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Authorization;

/// <summary>
/// Admin endpoints for the permissions of one application. Requires the <c>Admin</c> policy (bearer token with <c>core.admin</c>).
/// </summary>
[ApiController]
[Route("api/apps/{appKey}/permissions")]
[Authorize(Policy = AdminAuthentication.PolicyName)]
public sealed class PermissionsController(PermissionAdminService permissionService) : ControllerBase
{
    /// <summary>
    /// Lists the permissions of the application ordered by <c>key</c> (ordinal).
    /// </summary>
    /// <response code="200">The permissions of the application.</response>
    /// <response code="404">`Applications.NotFound`.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PermissionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> List(string appKey, CancellationToken cancellationToken)
    {
        var result = await permissionService.ListAsync(appKey, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Returns one permission of the application by its exact key.
    /// </summary>
    /// <response code="200">The permission.</response>
    /// <response code="404">`Applications.NotFound` or `Authorization.PermissionNotFound`.</response>
    [HttpGet("{key}")]
    [ProducesResponseType<PermissionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string appKey, string key, CancellationToken cancellationToken)
    {
        var result = await permissionService.GetAsync(appKey, key, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Creates a permission in the application.
    /// </summary>
    /// <response code="201">The created permission; `Location` points to `GET /api/apps/{appKey}/permissions/{key}`.</response>
    /// <response code="400">`Authorization.InvalidPermissionKey` or `Authorization.InvalidDescription`; a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="404">`Applications.NotFound`.</response>
    /// <response code="409">`Authorization.PermissionKeyTaken` (the same key in another application is allowed).</response>
    [HttpPost]
    [ProducesResponseType<PermissionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
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

    /// <summary>
    /// Replaces the <c>description</c> of a permission; the key never changes.
    /// </summary>
    /// <response code="200">The updated permission.</response>
    /// <response code="400">`Authorization.InvalidDescription`; a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="404">`Applications.NotFound` or `Authorization.PermissionNotFound`.</response>
    [HttpPut("{key}")]
    [ProducesResponseType<PermissionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
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

    /// <summary>
    /// Deletes a permission and removes it from every role that held it.
    /// </summary>
    /// <response code="204">The permission was deleted.</response>
    /// <response code="404">`Applications.NotFound` or `Authorization.PermissionNotFound`.</response>
    /// <response code="409">`Authorization.Reserved`: the `core.admin` permission of the `core` application cannot be deleted.</response>
    [HttpDelete("{key}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Delete(string appKey, string key, CancellationToken cancellationToken)
    {
        var result = await permissionService.DeleteAsync(appKey, key, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : NoContent();
    }
}
