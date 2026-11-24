using Href.Core.Api.Common.Results;
using Href.Core.Api.Common.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Href.Core.Tests.Common;

public sealed class ResultExtensionsTests
{
    [Theory]
    [InlineData(ErrorType.Validation, StatusCodes.Status400BadRequest, "Validation error.", "https://tools.ietf.org/html/rfc9110#section-15.5.1")]
    [InlineData(ErrorType.Unauthorized, StatusCodes.Status401Unauthorized, "Unauthorized.", "https://tools.ietf.org/html/rfc9110#section-15.5.2")]
    [InlineData(ErrorType.Forbidden, StatusCodes.Status403Forbidden, "Forbidden.", "https://tools.ietf.org/html/rfc9110#section-15.5.4")]
    [InlineData(ErrorType.NotFound, StatusCodes.Status404NotFound, "Resource not found.", "https://tools.ietf.org/html/rfc9110#section-15.5.5")]
    [InlineData(ErrorType.Conflict, StatusCodes.Status409Conflict, "Conflict.", "https://tools.ietf.org/html/rfc9110#section-15.5.10")]
    [InlineData(ErrorType.Failure, StatusCodes.Status500InternalServerError, "An unexpected error occurred.", "https://tools.ietf.org/html/rfc9110#section-15.6.1")]
    public void ToProblem_MapsErrorToProblemDetails(ErrorType errorType, int expectedStatus, string expectedTitle, string expectedType)
    {
        var controller = CreateController();
        var result = Result.Failure(new Error("Apps.Duplicate", "Application already exists.", errorType));

        var actionResult = result.ToProblem(controller);

        Assert.Equal(expectedStatus, actionResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(actionResult.Value);
        Assert.Equal(expectedStatus, problem.Status);
        Assert.Equal(expectedTitle, problem.Title);
        Assert.Equal(expectedType, problem.Type);
        Assert.Equal("Application already exists.", problem.Detail);
        Assert.Equal("Apps.Duplicate", problem.Extensions["errorCode"]);
    }

    [Theory]
    [InlineData(90.0, "90")]
    [InlineData(0.2, "1")]
    [InlineData(61.4, "62")]
    public void ToProblem_TooManyRequests_Returns429WithRetryAfterInWholeSeconds(double retryAfterSeconds, string expectedHeader)
    {
        var controller = CreateController();
        var error = Error.TooManyRequests("RateLimit.Exceeded", "Too many attempts.", TimeSpan.FromSeconds(retryAfterSeconds));

        var actionResult = error.ToProblem(controller);

        Assert.Equal(StatusCodes.Status429TooManyRequests, actionResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(actionResult.Value);
        Assert.Equal("Too many requests.", problem.Title);
        Assert.Equal("https://tools.ietf.org/html/rfc6585#section-4", problem.Type);
        Assert.Equal("RateLimit.Exceeded", problem.Extensions["errorCode"]);
        Assert.Equal(expectedHeader, controller.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public void ToProblem_OtherErrorTypes_DoNotSetRetryAfter()
    {
        var controller = CreateController();

        _ = new Error("Apps.Duplicate", "Application already exists.", ErrorType.Conflict).ToProblem(controller);

        Assert.False(controller.Response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public void ToProblem_OnSuccessResult_Throws()
    {
        var controller = CreateController();
        var result = Result.Success();

        Assert.Throws<InvalidOperationException>(() => result.ToProblem(controller));
    }

    private static TestController CreateController() =>
        new()
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
            ProblemDetailsFactory = new StubProblemDetailsFactory(),
        };

    private sealed class TestController : ControllerBase;

    private sealed class StubProblemDetailsFactory : ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(
            HttpContext httpContext,
            int? statusCode = null,
            string? title = null,
            string? type = null,
            string? detail = null,
            string? instance = null) =>
            new()
            {
                Status = statusCode,
                Title = title,
                Type = type,
                Detail = detail,
                Instance = instance,
            };

        public override ValidationProblemDetails CreateValidationProblemDetails(
            HttpContext httpContext,
            Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelStateDictionary,
            int? statusCode = null,
            string? title = null,
            string? type = null,
            string? detail = null,
            string? instance = null) =>
            new(modelStateDictionary)
            {
                Status = statusCode,
                Title = title,
                Type = type,
                Detail = detail,
                Instance = instance,
            };
    }
}
