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

    [HttpPost("email")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
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

    [HttpGet]
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

    [HttpGet("{id:guid}")]
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
