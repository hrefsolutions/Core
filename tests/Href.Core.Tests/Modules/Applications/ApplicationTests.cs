using Href.Core.Api.Modules.Applications;
using Microsoft.Extensions.Time.Testing;

namespace Href.Core.Tests.Modules.Applications;

public sealed class ApplicationTests
{
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Create_WithValidInputs_CreatesActiveApplicationAndVerifiableSecret()
    {
        var result = Application.Create("billing-portal", "Billing Portal", "Customer billing", true, _timeProvider);

        Assert.True(result.IsSuccess);
        var (application, clientSecret) = result.Value;
        Assert.Equal("billing-portal", application.AppKey);
        Assert.Equal("Billing Portal", application.Name);
        Assert.Equal("Customer billing", application.Description);
        Assert.Equal(ApplicationStatus.Active, application.Status);
        Assert.True(application.AllowSelfRegistration);
        Assert.Equal(43, clientSecret.Length);
        Assert.Equal(64, application.ClientSecretHash.Length);
        Assert.NotEqual(clientSecret, application.ClientSecretHash);
        Assert.Equal(_timeProvider.GetUtcNow().UtcDateTime, application.ClientSecretRotatedAtUtc);
        Assert.True(application.VerifySecret(clientSecret));
        Assert.False(application.VerifySecret("wrong-secret"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("UpperCase")]
    [InlineData("-leading-hyphen")]
    [InlineData("trailing-hyphen-")]
    [InlineData("double--hyphen")]
    [InlineData("has_underscore")]
    [InlineData("has space")]
    [InlineData("a23456789012345678901234567890123456789012345678901")]
    public void Create_WithInvalidAppKey_ReturnsInvalidAppKeyError(string? invalidKey)
    {
        var result = Application.Create(invalidKey, "Valid Name", null, false, _timeProvider);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationErrors.InvalidAppKey, result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankName_ReturnsInvalidNameError(string? invalidName)
    {
        var result = Application.Create("valid-key", invalidName, null, false, _timeProvider);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationErrors.InvalidName, result.Error);
    }

    [Fact]
    public void Create_WithTooLongName_ReturnsInvalidNameError()
    {
        var tooLongName = new string('a', Application.NameMaxLength + 1);

        var result = Application.Create("valid-key", tooLongName, null, false, _timeProvider);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationErrors.InvalidName, result.Error);
    }

    [Fact]
    public void Create_WithTooLongDescription_ReturnsInvalidDescriptionError()
    {
        var tooLongDescription = new string('a', Application.DescriptionMaxLength + 1);

        var result = Application.Create("valid-key", "Valid Name", tooLongDescription, false, _timeProvider);

        Assert.True(result.IsFailure);
        Assert.Equal(ApplicationErrors.InvalidDescription, result.Error);
    }

    [Fact]
    public void Update_WithValidInputs_UpdatesMutableProperties()
    {
        var (application, _) = Application.Create("valid-key", "Old Name", "Old Description", false, _timeProvider).Value;

        var updateResult = application.Update("New Name", "New Description", true);

        Assert.True(updateResult.IsSuccess);
        Assert.Equal("valid-key", application.AppKey);
        Assert.Equal("New Name", application.Name);
        Assert.Equal("New Description", application.Description);
        Assert.True(application.AllowSelfRegistration);
    }

    [Fact]
    public void RotateSecret_GeneratesNewSecretAndInvalidatesPreviousSecret()
    {
        var (application, originalSecret) = Application.Create("valid-key", "App", null, false, _timeProvider).Value;
        _timeProvider.Advance(TimeSpan.FromHours(1));

        var rotatedSecret = application.RotateSecret(_timeProvider);

        Assert.Equal(43, rotatedSecret.Length);
        Assert.NotEqual(originalSecret, rotatedSecret);
        Assert.False(application.VerifySecret(originalSecret));
        Assert.True(application.VerifySecret(rotatedSecret));
        Assert.Equal(_timeProvider.GetUtcNow().UtcDateTime, application.ClientSecretRotatedAtUtc);
    }

    [Fact]
    public void SuspendAndActivate_TransitionsStatusAndRejectsRedundantTransitions()
    {
        var (application, _) = Application.Create("valid-key", "App", null, false, _timeProvider).Value;

        var firstSuspend = application.Suspend();
        var secondSuspend = application.Suspend();

        Assert.True(firstSuspend.IsSuccess);
        Assert.Equal(ApplicationStatus.Suspended, application.Status);
        Assert.True(secondSuspend.IsFailure);
        Assert.Equal(ApplicationErrors.AlreadySuspended, secondSuspend.Error);

        var firstActivate = application.Activate();
        var secondActivate = application.Activate();

        Assert.True(firstActivate.IsSuccess);
        Assert.Equal(ApplicationStatus.Active, application.Status);
        Assert.True(secondActivate.IsFailure);
        Assert.Equal(ApplicationErrors.AlreadyActive, secondActivate.Error);
    }
}
