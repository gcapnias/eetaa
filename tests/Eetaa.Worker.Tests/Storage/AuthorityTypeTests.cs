using Eetaa.Worker.Storage;

namespace Eetaa.Worker.Tests.Storage;

public class AuthorityTypeTests
{
    [Theory]
    [InlineData("Δ", AuthorityType.Municipality)]
    [InlineData("Δήμος", AuthorityType.Municipality)]
    [InlineData("Κ", AuthorityType.Community)]
    [InlineData("Κοινότητα", AuthorityType.Community)]
    [InlineData("  Δ  ", AuthorityType.Municipality)]
    [InlineData("  Κ  ", AuthorityType.Community)]
    public void FromCode_ValidCodes_ReturnsExpectedAuthorityType(string code, AuthorityType expected)
    {
        var result = AuthorityTypeExtensions.FromCode(code);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown")]
    [InlineData("-")]
    public void FromCode_InvalidOrEmptyCodes_ReturnsNull(string? code)
    {
        var result = AuthorityTypeExtensions.FromCode(code);
        Assert.Null(result);
    }

    [Fact]
    public void ToDbCode_ConvertsToSingleGreekLetter()
    {
        Assert.Equal("Δ", AuthorityType.Municipality.ToDbCode());
        Assert.Equal("Κ", AuthorityType.Community.ToDbCode());
    }
}
