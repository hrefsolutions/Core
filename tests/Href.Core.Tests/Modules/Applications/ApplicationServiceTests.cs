using Href.Core.Api.Modules.Applications;

namespace Href.Core.Tests.Modules.Applications;

public sealed class ApplicationServiceTests
{
    [Theory]
    [InlineData("Active", ApplicationStatus.Active)]
    [InlineData("Suspended", ApplicationStatus.Suspended)]
    public void TryParseStatusName_WithExactName_ReturnsStatus(string value, ApplicationStatus expected)
    {
        var parsed = ApplicationService.TryParseStatusName(value, out var status);

        Assert.True(parsed);
        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("active")]
    [InlineData("Active,Suspended")]
    [InlineData(" Active")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseStatusName_WithAnythingElse_ReturnsFalse(string? value)
    {
        var parsed = ApplicationService.TryParseStatusName(value, out _);

        Assert.False(parsed);
    }
}
