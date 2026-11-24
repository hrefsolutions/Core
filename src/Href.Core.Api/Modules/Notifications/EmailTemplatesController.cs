using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Notifications;

/// <summary>
/// Administration of the global email templates. Requires the <c>Admin</c> policy (bearer token with <c>core.admin</c>).
/// </summary>
[ApiController]
[Route("api/notifications/templates")]
[Authorize(Policy = AdminAuthentication.PolicyName)]
public sealed class EmailTemplatesController(EmailTemplateService templateService) : ControllerBase
{
    private const int MaxRequestBodyBytes = NotificationsController.MaxRequestBodyBytes;

    /// <summary>
    /// Lists every email template ordered by <c>key</c> (ordinal), without bodies and without paging.
    /// </summary>
    /// <response code="200">The templates as `{ key, name, isSystem, updatedAtUtc }`.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<EmailTemplateSummaryResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await templateService.ListAsync(cancellationToken));

    /// <summary>
    /// Returns the full email template with its placeholders.
    /// </summary>
    /// <response code="200">The full template.</response>
    /// <response code="404">`Templates.NotFound` for an unknown key, a key that is not a lowercase slug or a different case.</response>
    [HttpGet("{key}")]
    [ProducesResponseType<EmailTemplateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> GetByKey(string key, CancellationToken cancellationToken)
    {
        var result = await templateService.GetByKeyAsync(key, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Creates a custom email template; a client <c>isSystem</c> is ignored.
    /// </summary>
    /// <response code="201">The full template; `Location` points to `GET /api/notifications/templates/{key}`.</response>
    /// <response code="400">`Templates.InvalidKey`, `Templates.InvalidName`, `Templates.InvalidDescription`, `Templates.InvalidSubject`, `Templates.InvalidHtmlBody`, `Templates.InvalidTextBody` or `Templates.ContentTooLarge`; a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="409">`Templates.KeyTaken`.</response>
    [HttpPost]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [ProducesResponseType<EmailTemplateResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Create(
        [FromBody] CreateEmailTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var result = await templateService.CreateAsync(request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : CreatedAtAction(nameof(GetByKey), new { key = result.Value.Key }, result.Value);
    }

    /// <summary>
    /// Replaces the <c>name</c>, <c>description</c>, <c>subject</c>, <c>htmlBody</c> and <c>textBody</c> of a template
    /// (last write wins); the key comes from the route.
    /// </summary>
    /// <response code="200">The full template.</response>
    /// <response code="400">The create content codes (`Templates.InvalidName`, `Templates.InvalidDescription`, `Templates.InvalidSubject`, `Templates.InvalidHtmlBody`, `Templates.InvalidTextBody`, `Templates.ContentTooLarge`), and for system templates `Templates.MissingRequiredPlaceholders`, `Templates.UnknownPlaceholders` or `Templates.SystemTemplateNotRenderable`; a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="404">`Templates.NotFound`.</response>
    [HttpPut("{key}")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [ProducesResponseType<EmailTemplateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Update(
        string key,
        [FromBody] UpdateEmailTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var result = await templateService.UpdateAsync(key, request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Deletes a custom email template; system templates cannot be deleted.
    /// </summary>
    /// <response code="204">The template was deleted.</response>
    /// <response code="404">`Templates.NotFound`.</response>
    /// <response code="409">`Templates.SystemTemplate` for a system template.</response>
    [HttpDelete("{key}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Delete(string key, CancellationToken cancellationToken)
    {
        var result = await templateService.DeleteAsync(key, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : NoContent();
    }

    /// <summary>
    /// Renders the stored template with the given <c>variables</c>; stores nothing.
    /// </summary>
    /// <response code="200">The rendered `{ subject, html, text }` (HTML values encoded).</response>
    /// <response code="400">Renderer errors: `Templates.MissingVariables`, `Templates.DuplicateVariables` or `Templates.RenderedContentTooLarge`; a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="404">`Templates.NotFound`.</response>
    [HttpPost("{key}/preview")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [ProducesResponseType<EmailTemplatePreviewResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Preview(
        string key,
        [FromBody] PreviewEmailTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var result = await templateService.PreviewAsync(key, request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }
}
