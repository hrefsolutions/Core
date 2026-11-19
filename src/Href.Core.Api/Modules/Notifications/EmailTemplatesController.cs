using Href.Core.Api.Common.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Notifications;

/// <summary>
/// Administration of the global email templates. These endpoints have no authentication until the user authentication work ships.
/// </summary>
[ApiController]
[Route("api/notifications/templates")]
[AllowAnonymous]
public sealed class EmailTemplatesController(EmailTemplateService templateService) : ControllerBase
{
    private const int MaxRequestBodyBytes = NotificationsController.MaxRequestBodyBytes;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await templateService.ListAsync(cancellationToken));

    [HttpGet("{key}")]
    public async Task<IActionResult> GetByKey(string key, CancellationToken cancellationToken)
    {
        var result = await templateService.GetByKeyAsync(key, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    [HttpPost]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    public async Task<IActionResult> Create(
        [FromBody] CreateEmailTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var result = await templateService.CreateAsync(request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : CreatedAtAction(nameof(GetByKey), new { key = result.Value.Key }, result.Value);
    }

    [HttpPut("{key}")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
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

    [HttpDelete("{key}")]
    public async Task<IActionResult> Delete(string key, CancellationToken cancellationToken)
    {
        var result = await templateService.DeleteAsync(key, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : NoContent();
    }

    [HttpPost("{key}/preview")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
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
