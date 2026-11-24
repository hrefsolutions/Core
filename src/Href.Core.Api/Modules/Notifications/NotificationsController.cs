using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Applications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Notifications;

/// <summary>
/// Client-facing notification API. Every endpoint is scoped to the authenticated application.
/// </summary>
[ApiController]
[Route("api/notifications")]
[Authorize(Policy = AppKeyAuthentication.PolicyName)]
public sealed class NotificationsController(NotificationService notificationService) : ControllerBase
{
    /// <summary>
    /// Request body limit. Each body allows 512 KB of UTF-8 after decoding, but a JSON client may escape every
    /// <c>&lt;</c>, <c>&gt;</c> or non-ASCII character as a 6-byte sequence, so html plus text can reach
    /// 2 x 512 KB x 6 = 6 MB before decoding. 8 MB leaves room for the other fields.
    /// </summary>
    public const int MaxRequestBodyBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Queues one email for the calling application, either from a stored template (<c>templateKey</c> + <c>variables</c>)
    /// or from caller content (<c>html</c> + <c>subject</c>, optional <c>text</c>), never both.
    /// </summary>
    /// <response code="202">`{ id, status }` of the queued notification; `Location` points to `GET /api/notifications/{id}`.</response>
    /// <response code="400">`Notifications.InvalidContent`, `Notifications.InvalidRecipient`, `Notifications.InvalidRecipientName`, `Notifications.InvalidSubject`, `Notifications.InvalidNotifyOnFailureTo`, `Notifications.ContentTooLarge`, `Templates.MissingVariables`, `Templates.DuplicateVariables` or `Templates.RenderedContentTooLarge`; a missing or malformed body returns the framework `ValidationProblemDetails`.</response>
    /// <response code="404">`Templates.NotFound` (the key is exact and case-sensitive).</response>
    /// <response code="413">The request body is larger than 8 MB.</response>
    [HttpPost("email")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [ProducesResponseType<QueuedNotificationResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> QueueEmail(
        [FromBody] QueueEmailRequest request,
        CancellationToken cancellationToken)
    {
        var appKey = User.GetAppKey();

        if (string.IsNullOrEmpty(appKey))
        {
            return AppKeyAuthentication.InvalidCredentialsError.ToProblem(this);
        }

        var correlationId = HttpContext.Items[CorrelationIdMiddleware.HeaderName] as string;
        var result = await notificationService.QueueEmailAsync(appKey, request, correlationId, cancellationToken);

        if (result.IsFailure)
        {
            return result.ToProblem(this);
        }

        var notification = result.Value;

        return AcceptedAtAction(
            nameof(GetById),
            new { id = notification.Id },
            new QueuedNotificationResponse(notification.Id, notification.Status.ToString()));
    }

    /// <summary>
    /// Lists the notifications of the calling application, newest first, without bodies.
    /// </summary>
    /// <response code="200">`{ items, page, pageSize, totalCount }`.</response>
    /// <response code="400">`Notifications.InvalidStatus`, `Notifications.InvalidDateRange`, `Notifications.InvalidPage` or `Notifications.InvalidPageSize`; unparsable dates or numbers return the framework `ValidationProblemDetails` without `errorCode`.</response>
    [HttpGet]
    [ProducesResponseType<NotificationPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery(Name = "from")] DateTimeOffset? createdFrom,
        [FromQuery(Name = "to")] DateTimeOffset? createdTo,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = NotificationService.DefaultPageSize)
    {
        var appKey = User.GetAppKey();

        if (string.IsNullOrEmpty(appKey))
        {
            return AppKeyAuthentication.InvalidCredentialsError.ToProblem(this);
        }

        var result = await notificationService.ListAsync(
            appKey,
            new ListNotificationsQuery(status, createdFrom, createdTo, page, pageSize),
            cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    /// <summary>
    /// Returns one notification of the calling application, without bodies.
    /// </summary>
    /// <response code="200">The notification.</response>
    /// <response code="404">`Notifications.NotFound` for an unknown id or another application's notification.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<NotificationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var appKey = User.GetAppKey();

        if (string.IsNullOrEmpty(appKey))
        {
            return AppKeyAuthentication.InvalidCredentialsError.ToProblem(this);
        }

        var result = await notificationService.GetByIdAsync(appKey, id, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }
}
