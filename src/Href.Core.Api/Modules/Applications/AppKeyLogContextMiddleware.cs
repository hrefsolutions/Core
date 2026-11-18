using Serilog;
using Serilog.Context;

namespace Href.Core.Api.Modules.Applications;

/// <summary>
/// Adds the authenticated application's <c>AppKey</c> to every log written later in the request
/// (Serilog <see cref="LogContext"/>) and to the request completion log (<see cref="IDiagnosticContext"/>).
/// Must run after <c>UseAuthorization</c>, which is where the <c>AppKey</c> scheme sets <see cref="HttpContext.User"/>.
/// </summary>
public sealed class AppKeyLogContextMiddleware(RequestDelegate next)
{
    public const string PropertyName = "AppKey";

    public async Task InvokeAsync(HttpContext context, IDiagnosticContext diagnosticContext)
    {
        var appKey = context.User.GetAppKey();

        if (string.IsNullOrEmpty(appKey))
        {
            await next(context);
            return;
        }

        diagnosticContext.Set(PropertyName, appKey);

        using (LogContext.PushProperty(PropertyName, appKey))
        {
            await next(context);
        }
    }
}
