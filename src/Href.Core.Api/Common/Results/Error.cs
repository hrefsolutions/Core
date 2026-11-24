namespace Href.Core.Api.Common.Results;

public enum ErrorType
{
    Failure,
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    TooManyRequests,
}

public sealed record Error(string Code, string Message, ErrorType Type = ErrorType.Failure)
{
    public static readonly Error None = new(string.Empty, string.Empty);

    /// <summary>
    /// For <see cref="ErrorType.TooManyRequests"/>: how long the caller should wait, sent as the <c>Retry-After</c> header.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    public static Error Failure(string code, string message) => new(code, message);

    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error TooManyRequests(string code, string message, TimeSpan retryAfter) =>
        new(code, message, ErrorType.TooManyRequests) { RetryAfter = retryAfter };
}
