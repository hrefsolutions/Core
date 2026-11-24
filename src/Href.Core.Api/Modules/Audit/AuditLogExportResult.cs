using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Modules.Audit;

/// <summary>
/// Streams a prepared <see cref="AuditLogExport"/> as a <c>200</c> attachment (<c>Content-Disposition</c>, no caching,
/// response buffering off). Once the body has started, a failure can only abort the response.
/// </summary>
public sealed class AuditLogExportResult(AuditLogExport export, AuditLogExportService exportService) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var httpContext = context.HttpContext;
        var response = httpContext.Response;

        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = export.ContentType;
        response.Headers.ContentDisposition = $"attachment; filename=\"{export.FileName}\"";
        response.Headers.CacheControl = "no-store";

        await exportService.WriteAsync(export, response.Body, httpContext.RequestAborted);
    }
}
