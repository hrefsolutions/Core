using Href.Core.Api.Modules.Audit;

namespace Href.Core.Tests.Modules.Audit;

public sealed class AuditLogCsvTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("line\r\nbreak", "\"line\r\nbreak\"")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("\rcmd", "\"'\rcmd\"")]
    [InlineData("=1,2", "\"'=1,2\"")]
    [InlineData("a=1", "a=1")]
    [InlineData("{\"a\":-1}", "\"{\"\"a\"\":-1}\"")]
    public void Field_EscapesFormulasAndRfc4180SpecialCharacters(string? value, string expected)
    {
        Assert.Equal(expected, AuditLogCsv.Field(value));
    }
}
