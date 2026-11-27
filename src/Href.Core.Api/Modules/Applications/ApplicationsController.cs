using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Applications;

[ApiController]
[Route("api/apps")]
public sealed class ApplicationsController(ApplicationService applicationService) : ControllerBase
{
    /// <summary>
    /// Returns the client application authenticated by the M2M credentials (<c>X-App-Key</c> and <c>X-App-Secret</c>).
    /// </summary>
    /// <response code="200">The authenticated application.</response>
    [HttpGet("me")]
    [Authorize(Policy = AppKeyAuthentication.PolicyName)]
    [ProducesResponseType<CurrentApplicationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMe(CancellationToken cancellationToken)
    {
        var appKey = User.GetAppKey();

        if (string.IsNullOrEmpty(appKey))
        {
            return AppKeyAuthentication.InvalidCredentialsError.ToProblem(this);
        }

        var result = await applicationService.GetCurrentAsync(appKey, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Creates a client application and returns its one-time <c>clientSecret</c>, which is never shown again.
    /// </summary>
    /// <response code="201">The created application with its one-time `clientSecret`; `Location` points to `GET /api/apps/{appKey}`.</response>
    /// <response code="400">Validation failed: `Applications.InvalidAppKey`, `Applications.InvalidName` or `Applications.InvalidDescription`; a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="409">`Applications.AppKeyTaken`.</response>
    [HttpPost]
    [Authorize(Policy = AdminAuthentication.PolicyName)]
    [ProducesResponseType<ApplicationCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Create(
        [FromBody] CreateApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await applicationService.CreateAsync(request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : CreatedAtAction(nameof(GetByAppKey), new { appKey = result.Value.AppKey }, result.Value);
    }

    /// <summary>
    /// Lists every client application ordered by <c>name</c>.
    /// </summary>
    /// <response code="200">The client applications.</response>
    [HttpGet]
    [Authorize(Policy = AdminAuthentication.PolicyName)]
    [ProducesResponseType<IReadOnlyList<ApplicationResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var applications = await applicationService.ListAsync(cancellationToken);
        return Ok(applications);
    }

    /// <summary>
    /// Returns a client application by its <c>appKey</c>.
    /// </summary>
    /// <response code="200">The application.</response>
    /// <response code="404">`Applications.NotFound`.</response>
    [HttpGet("{appKey}")]
    [Authorize(Policy = AdminAuthentication.PolicyName)]
    [ProducesResponseType<ApplicationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> GetByAppKey(string appKey, CancellationToken cancellationToken)
    {
        var result = await applicationService.GetByAppKeyAsync(appKey, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Updates the <c>name</c>, <c>description</c> and <c>allowSelfRegistration</c> of a client application.
    /// </summary>
    /// <response code="200">The updated application.</response>
    /// <response code="400">Validation failed: `Applications.InvalidName` or `Applications.InvalidDescription`; a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="404">`Applications.NotFound`.</response>
    /// <response code="409">`Applications.Reserved`: the reserved `core` application cannot allow self-registration.</response>
    [HttpPut("{appKey}")]
    [Authorize(Policy = AdminAuthentication.PolicyName)]
    [ProducesResponseType<ApplicationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Update(
        string appKey,
        [FromBody] UpdateApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await applicationService.UpdateAsync(appKey, request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Activates or suspends a client application (<c>Active</c> or <c>Suspended</c>).
    /// </summary>
    /// <response code="200">The application with its new status.</response>
    /// <response code="400">`Applications.InvalidStatus`; a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="404">`Applications.NotFound`.</response>
    /// <response code="409">`Applications.AlreadyActive`, `Applications.AlreadySuspended`, or `Applications.Reserved` (the reserved `core` application cannot be suspended).</response>
    [HttpPatch("{appKey}/status")]
    [Authorize(Policy = AdminAuthentication.PolicyName)]
    [ProducesResponseType<ApplicationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> UpdateStatus(
        string appKey,
        [FromBody] UpdateApplicationStatusRequest request,
        CancellationToken cancellationToken)
    {
        var result = await applicationService.UpdateStatusAsync(appKey, request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Rotates the <c>clientSecret</c> of a client application; the previous secret stops working immediately.
    /// </summary>
    /// <response code="200">The new one-time `clientSecret`.</response>
    /// <response code="404">`Applications.NotFound`.</response>
    [HttpPost("{appKey}/secret/rotate")]
    [Authorize(Policy = AdminAuthentication.PolicyName)]
    [ProducesResponseType<RotateClientSecretResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> RotateSecret(string appKey, CancellationToken cancellationToken)
    {
        var result = await applicationService.RotateSecretAsync(appKey, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Deletes a client application. It must be suspended and no user can have access to it (revoke each one with
    /// <c>DELETE /api/apps/{appKey}/users/{userId}</c>). Its roles, permissions and user tokens are deleted, and its pending or
    /// in-progress notifications are cancelled. The audit log and the notification history stay, tied to the application id,
    /// so a new application that later takes the same <c>appKey</c> does not inherit them.
    /// </summary>
    /// <response code="204">The application was deleted.</response>
    /// <response code="404">`Applications.NotFound`.</response>
    /// <response code="409">`Applications.Reserved` (the reserved `core` application cannot be deleted), `Applications.DeleteRequiresSuspended` or `Applications.HasUserAccesses`.</response>
    [HttpDelete("{appKey}")]
    [Authorize(Policy = AdminAuthentication.PolicyName)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Delete(string appKey, CancellationToken cancellationToken)
    {
        var result = await applicationService.DeleteAsync(appKey, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : NoContent();
    }
}
