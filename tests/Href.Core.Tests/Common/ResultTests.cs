using Href.Core.Api.Common.Results;

namespace Href.Core.Tests.Common;

public sealed class ResultTests
{
    private static readonly Error SampleError = Error.Validation("Sample.Invalid", "The sample is invalid.");

    [Fact]
    public void Success_HasNoError()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(Error.None, result.Error);
    }

    [Fact]
    public void Failure_ExposesError()
    {
        var result = Result.Failure(SampleError);

        Assert.True(result.IsFailure);
        Assert.Equal(SampleError, result.Error);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public void SuccessWithValue_ExposesValue()
    {
        Result<int> result = 42;

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void FailureWithValue_ThrowsWhenValueIsRead()
    {
        Result<int> result = SampleError;

        Assert.True(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Failure_WithNoneError_Throws()
    {
        Assert.Throws<ArgumentException>(() => Result.Failure(Error.None));
    }

    [Fact]
    public void Failure_WithNullError_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
    }

    [Theory]
    [InlineData(ErrorType.Failure)]
    [InlineData(ErrorType.Validation)]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    public void ErrorFactories_CreateExpectedType(ErrorType expectedType)
    {
        var error = expectedType switch
        {
            ErrorType.Validation => Error.Validation("Code", "Message"),
            ErrorType.Unauthorized => Error.Unauthorized("Code", "Message"),
            ErrorType.Forbidden => Error.Forbidden("Code", "Message"),
            ErrorType.NotFound => Error.NotFound("Code", "Message"),
            ErrorType.Conflict => Error.Conflict("Code", "Message"),
            _ => Error.Failure("Code", "Message"),
        };

        Assert.Equal("Code", error.Code);
        Assert.Equal("Message", error.Message);
        Assert.Equal(expectedType, error.Type);
    }
}
