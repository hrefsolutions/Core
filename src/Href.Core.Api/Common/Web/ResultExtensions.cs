using Href.Core.Api.Common.Results;
using Microsoft.AspNetCore.Mvc;

namespace Href.Core.Api.Common.Web;

/// <summary>
/// Maps expected domain and validation <see cref="Error"/> values to RFC 7807 <see cref="ProblemDetails"/> responses.
/// </summary>
public static class ResultExtensions
{
    public static ObjectResult ToProblem(this Result result, ControllerBase controller)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(controller);

        if (result.IsSuccess)
        {
            throw new InvalidOperationException("A successful result cannot be converted to a ProblemDetails response.");
        }

        return result.Error.ToProblem(controller);
    }

    public static ObjectResult ToProblem(this Error error, ControllerBase controller)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(controller);

        var (statusCode, title, type) = GetMetadata(error.Type);

        var problemDetails = controller.ProblemDetailsFactory.CreateProblemDetails(
            controller.HttpContext,
            statusCode: statusCode,
            title: title,
            type: type,
            detail: error.Message);

        problemDetails.Extensions["errorCode"] = error.Code;

        if (error is { Type: ErrorType.TooManyRequests, RetryAfter: { } retryAfter })
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            controller.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new ObjectResult(problemDetails)
        {
            StatusCode = statusCode,
        };
    }

    private static (int StatusCode, string Title, string Type) GetMetadata(ErrorType errorType) =>
        errorType switch
        {
            ErrorType.Validation => (StatusCodes.Status400BadRequest, "Validation error.", "https://tools.ietf.org/html/rfc9110#section-15.5.1"),
            ErrorType.Unauthorized => (StatusCodes.Status401Unauthorized, "Unauthorized.", "https://tools.ietf.org/html/rfc9110#section-15.5.2"),
            ErrorType.Forbidden => (StatusCodes.Status403Forbidden, "Forbidden.", "https://tools.ietf.org/html/rfc9110#section-15.5.4"),
            ErrorType.NotFound => (StatusCodes.Status404NotFound, "Resource not found.", "https://tools.ietf.org/html/rfc9110#section-15.5.5"),
            ErrorType.Conflict => (StatusCodes.Status409Conflict, "Conflict.", "https://tools.ietf.org/html/rfc9110#section-15.5.10"),
            ErrorType.TooManyRequests => (StatusCodes.Status429TooManyRequests, "Too many requests.", "https://tools.ietf.org/html/rfc6585#section-4"),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", "https://tools.ietf.org/html/rfc9110#section-15.6.1"),
        };
}
