using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Common.Web;

/// <summary>
/// Converts unhandled exceptions into an RFC 7807 <see cref="ProblemDetails"/> response with status 500.
/// A <see cref="BadHttpRequestException"/> with a 4xx status (for example a body over <c>[RequestSizeLimit]</c>, thrown by
/// Kestrel while the body is read) keeps its status instead: <c>413</c> with <c>errorCode</c> <see cref="RequestTooLargeErrorCode"/>,
/// any other 4xx with <see cref="InvalidRequestErrorCode"/>, logged as a warning without the exception.
/// The log entry carries no request data: the method and path, which the client controls, are in the request completion
/// log line with the same <c>CorrelationId</c>.
/// </summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public const string RequestTooLargeErrorCode = "Request.TooLarge";
    public const string InvalidRequestErrorCode = "Request.Invalid";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is BadHttpRequestException { StatusCode: >= 400 and < 500 } badRequest)
        {
            return await WriteClientErrorAsync(httpContext, badRequest);
        }

        logger.LogError(exception, "Unhandled exception while processing the request");

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
                Detail = environment.IsDevelopment() ? exception.Message : null,
            },
        });
    }

    private async ValueTask<bool> WriteClientErrorAsync(HttpContext httpContext, BadHttpRequestException exception)
    {
        var tooLarge = exception.StatusCode == StatusCodes.Status413PayloadTooLarge;
        logger.LogWarning("Rejected a request that the server could not read, with status {StatusCode}", exception.StatusCode);

        httpContext.Response.StatusCode = exception.StatusCode;

        var problemDetails = new ProblemDetails
        {
            Status = exception.StatusCode,
            Title = tooLarge ? "Request body too large." : "Bad request.",
            Type = tooLarge
                ? "https://tools.ietf.org/html/rfc9110#section-15.5.14"
                : "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            Detail = tooLarge
                ? "The request body exceeds the size limit of this endpoint."
                : "The request could not be read.",
        };
        problemDetails.Extensions["errorCode"] = tooLarge ? RequestTooLargeErrorCode : InvalidRequestErrorCode;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails,
        });
    }
}
