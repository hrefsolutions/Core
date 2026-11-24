using Href.Core.Api.Common.Persistence;
using Microsoft.Extensions.Configuration;

namespace Href.Core.Tests.Common;

public sealed class DatabaseRuntimeRoleTests
{
    [Theory]
    [InlineData("href_core_app", true)]
    [InlineData("_runtime", true)]
    [InlineData("a", true)]
    [InlineData("Href", false)]
    [InlineData("1runtime", false)]
    [InlineData("run-time", false)]
    [InlineData("runtime;drop", false)]
    [InlineData("\"runtime\"", false)]
    [InlineData("runtime\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidRoleName_AcceptsOnlyLowercaseUnquotedIdentifiers(string? roleName, bool expected) =>
        Assert.Equal(expected, DatabaseRuntimeRole.IsValidRoleName(roleName));

    [Fact]
    public void IsValidRoleName_RejectsNamesLongerThan63Characters()
    {
        Assert.True(DatabaseRuntimeRole.IsValidRoleName(new string('a', 63)));
        Assert.False(DatabaseRuntimeRole.IsValidRoleName(new string('a', 64)));
    }

    [Theory]
    [InlineData("Host=owner", "Host=owner")]
    [InlineData(null, "Host=runtime")]
    [InlineData("  ", "Host=runtime")]
    public void GetOwnerConnectionString_PrefersMigrationsAndFallsBackToDefault(string? migrations, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=runtime",
                [$"ConnectionStrings:{DatabaseRuntimeRole.MigrationsConnectionName}"] = migrations,
            })
            .Build();

        Assert.Equal(expected, DatabaseRuntimeRole.GetOwnerConnectionString(configuration));
    }
}
