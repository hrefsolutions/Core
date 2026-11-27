using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Href.Core.Api.Modules.Users;

/// <summary>
/// Admin endpoints for global users. There is no DELETE: users are locked, never deleted.
/// Requires the <c>Admin</c> policy (bearer token with <c>core.admin</c>).
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize(Policy = AdminAuthentication.PolicyName)]
public sealed class UsersController(UserService userService, InvitationService invitationService) : ControllerBase
{
    /// <summary>
    /// Invites a user to an application: creates it unverified and without a known password, grants the access and emails a
    /// one-time link (72 h) to <c>acceptInvitationUrl</c>, where the user picks the password.
    /// </summary>
    /// <response code="201">The invited user (email not verified); `Location` points to `GET /api/users/{id}`.</response>
    /// <response code="400">`Auth.InvalidCallbackUrl`, `Users.InvalidEmail`, `Users.InvalidFullName`, `Authorization.InvalidRoleIds` or `Authorization.UnknownRoles`; a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="404">`Applications.NotFound`.</response>
    /// <response code="409">`Users.EmailTaken` (any case).</response>
    [HttpPost("invitations")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Invite([FromBody] InviteUserRequest request, CancellationToken cancellationToken)
    {
        var result = await invitationService.InviteAsync(request, GetCorrelationId(), cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value);
    }

    /// <summary>
    /// Emails a new invitation link for an application the user already has access to; the previous links stop working.
    /// </summary>
    /// <response code="202">The new link was queued.</response>
    /// <response code="400">`Auth.InvalidCallbackUrl`; a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="404">`Applications.NotFound`, `Users.NotFound` or `Authorization.AccessNotFound`.</response>
    /// <response code="409">`Users.EmailAlreadyVerified` (the user already activated the account) or `Users.AlreadyLocked`.</response>
    [HttpPost("{id:guid}/invitations")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> ResendInvitation(Guid id, [FromBody] ResendInvitationRequest request, CancellationToken cancellationToken)
    {
        var result = await invitationService.ResendAsync(id, request, GetCorrelationId(), cancellationToken);

        return result.IsFailure ? result.ToProblem(this) : Accepted();
    }

    /// <summary>
    /// Creates a global user with the email already verified.
    /// </summary>
    /// <response code="201">The created user; `Location` points to `GET /api/users/{id}`.</response>
    /// <response code="400">Validation failed: `Users.InvalidEmail`, `Users.InvalidFullName` or `Users.WeakPassword`; a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="409">`Users.EmailTaken` (any case).</response>
    [HttpPost]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        var result = await userService.CreateAsync(request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value);
    }

    /// <summary>
    /// Lists users ordered by <c>email</c>, optionally filtered by <c>search</c> and <c>status</c>.
    /// </summary>
    /// <response code="200">`{ items, page, pageSize, totalCount }`.</response>
    /// <response code="400">`Users.InvalidSearch`, `Users.InvalidStatus`, `Users.InvalidPage` or `Users.InvalidPageSize`; unparsable numbers return the framework `ValidationProblemDetails`.</response>
    [HttpGet]
    [ProducesResponseType<UserPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string? status,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = UserService.DefaultPageSize)
    {
        var result = await userService.ListAsync(new ListUsersQuery(search, status, page, pageSize), cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Returns a user with the applications it can access and its roles in each one.
    /// </summary>
    /// <response code="200">The user plus `applications`.</response>
    /// <response code="404">`Users.NotFound`; a non-GUID id returns a plain `404` (route constraint).</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<UserDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await userService.GetByIdAsync(id, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Replaces the <c>fullName</c> and <c>email</c> of a user; a new email clears the verification.
    /// </summary>
    /// <response code="200">The updated user.</response>
    /// <response code="400">`Users.InvalidEmail` or `Users.InvalidFullName` (nothing changes); a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="404">`Users.NotFound`.</response>
    /// <response code="409">`Users.EmailTaken`, or `Authorization.LastAdmin` when a new email (which clears the verification) would leave Core without an active admin.</response>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var result = await userService.UpdateAsync(id, request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Locks a user (optional body <c>{ reason }</c>) and revokes every active session of the user.
    /// </summary>
    /// <response code="200">The locked user.</response>
    /// <response code="400">`Users.InvalidLockReason`.</response>
    /// <response code="404">`Users.NotFound`.</response>
    /// <response code="409">`Users.AlreadyLocked`, or `Authorization.LastAdmin` when the user is the last active admin of Core.</response>
    [HttpPost("{id:guid}/lock")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Lock(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] LockUserRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await userService.LockAsync(id, request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Unlocks a user.
    /// </summary>
    /// <response code="200">The unlocked user.</response>
    /// <response code="404">`Users.NotFound`.</response>
    /// <response code="409">`Users.NotLocked`.</response>
    [HttpPost("{id:guid}/unlock")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Unlock(Guid id, CancellationToken cancellationToken)
    {
        var result = await userService.UnlockAsync(id, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Deletes a user. The user must be locked and have no application access (revoke each one with
    /// <c>DELETE /api/apps/{appKey}/users/{userId}</c>). Its sessions and one-time links are deleted too; the audit log keeps
    /// its history.
    /// </summary>
    /// <response code="204">The user was deleted.</response>
    /// <response code="404">`Users.NotFound`.</response>
    /// <response code="409">`Users.DeleteRequiresLocked` or `Users.HasApplicationAccesses`.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await userService.DeleteAsync(id, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : NoContent();
    }

    private string? GetCorrelationId() => HttpContext.Items[CorrelationIdMiddleware.HeaderName] as string;
}
