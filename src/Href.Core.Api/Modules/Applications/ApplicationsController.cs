using Href.Core.Api.Common.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Applications;

[ApiController]
[Route("api/apps")]
public sealed class ApplicationsController(ApplicationService applicationService) : ControllerBase
{
    [HttpGet("me")]
    [Authorize(Policy = AppKeyAuthentication.PolicyName)]
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

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await applicationService.CreateAsync(request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : CreatedAtAction(nameof(GetByAppKey), new { appKey = result.Value.AppKey }, result.Value);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var applications = await applicationService.ListAsync(cancellationToken);
        return Ok(applications);
    }

    [HttpGet("{appKey}")]
    public async Task<IActionResult> GetByAppKey(string appKey, CancellationToken cancellationToken)
    {
        var result = await applicationService.GetByAppKeyAsync(appKey, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    [HttpPut("{appKey}")]
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

    [HttpPatch("{appKey}/status")]
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

    [HttpPost("{appKey}/secret/rotate")]
    public async Task<IActionResult> RotateSecret(string appKey, CancellationToken cancellationToken)
    {
        var result = await applicationService.RotateSecretAsync(appKey, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }
}
