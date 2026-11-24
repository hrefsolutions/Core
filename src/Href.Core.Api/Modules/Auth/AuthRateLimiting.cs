using System.Globalization;
using System.Threading.RateLimiting;
using Href.Core.Api.Modules.Applications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// In-memory fixed-window rate limits for the M2M auth endpoints (Railway runs a single instance). Each window is
/// partitioned by the calling application's <c>AppKey</c> plus the end user's IP (<see cref="EndUserIp"/>; an IPv6 address
/// counts as its /64 network), so a satellite that does not forward <c>X-End-User-Ip</c> shares one budget among all its users. The limiter runs after
/// <c>UseAuthorization</c>, where the <c>AppKey</c> is known: requests without valid M2M credentials get <c>401</c> before
/// it. A rejected request gets <c>429</c> as <see cref="ProblemDetails"/> (<c>errorCode</c> <see cref="ErrorCode"/>) with
/// <c>Retry-After</c>.
/// </summary>
public static class AuthRateLimiting
{
    public const string StrictPolicy = "auth-strict";
    public const string RefreshPolicy = "auth-refresh";
    public const int StrictPermitLimit = 10;
    public const int RefreshPermitLimit = 60;
    public const string ErrorCode = "RateLimit.Exceeded";

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(StrictPolicy, context => CreatePartition(context, StrictPermitLimit));
            options.AddPolicy(RefreshPolicy, context => CreatePartition(context, RefreshPermitLimit));
            options.OnRejected = WriteRejectionAsync;
        });

    private static RateLimitPartition<string> CreatePartition(HttpContext context, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"{context.User.GetAppKey()}|{EndUserIp.ToRateLimitKey(EndUserIp.Resolve(context))}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = Window,
                QueueLimit = 0,
                AutoReplenishment = true,
            });

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var metadata) ? metadata : Window;
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));

        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests.",
            Type = "https://tools.ietf.org/html/rfc6585#section-4",
            Detail = $"Too many requests. Try again in {retryAfterSeconds} seconds.",
        };
        problemDetails.Extensions["errorCode"] = ErrorCode;

        var written = await httpContext.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        });

        if (!written)
        {
            await httpContext.Response.WriteAsJsonAsync(problemDetails, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", cancellationToken);
        }
    }
}
