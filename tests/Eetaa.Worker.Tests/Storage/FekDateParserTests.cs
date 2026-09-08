using System.Text;

using Eetaa.Worker.Storage;

namespace Eetaa.Worker.Tests.Storage;

public class FekDateParserTests
{
    [Theory]
    [InlineData("14/03/1971", "1971-03-14", "14/03/1971")]
    [InlineData("21/05/1975", "1975-05-21", "21/05/1975")]
    [InlineData("05/04/1981", "1981-04-05", "05/04/1981")]
    [InlineData("08/12/1845", "1845-12-08", "08/12/1845")]
    [InlineData("24/08/1912", "1912-08-24", "24/08/1912")]
    [InlineData("30/08/1927", "1927-08-30", "30/08/1927")]
    [InlineData("04/12/1997", "1997-12-04", "04/12/1997")]
    [InlineData("07/06/2010", "2010-06-07", "07/06/2010")]
    public void Parse_StandardDdMmYyyy_NormalizesToIso8601(string input, string expectedIso, string expectedRaw)
    {
        var (normalized, raw) = FekDateParser.Parse(input);
        Assert.Equal(expectedIso, normalized);
        Assert.Equal(expectedRaw, raw);
    }

    [Theory]
    [InlineData("4/8/1912", "1912-08-04", "4/8/1912")]
    [InlineData("24/8/1912", "1912-08-24", "24/8/1912")]
    [InlineData("4/12/1997", "1997-12-04", "4/12/1997")]
    [InlineData(" 14/03/1971 ", "1971-03-14", "14/03/1971")]
    public void Parse_SingleDigitDaysMonthsAndWhitespace_NormalizesProperly(string input, string expectedIso, string expectedRaw)
    {
        var (normalized, raw) = FekDateParser.Parse(input);
        Assert.Equal(expectedIso, normalized);
        Assert.Equal(expectedRaw, raw);
    }

    [Theory]
    [InlineData("//1928", "//1928")]
    [InlineData("1928", "1928")]
    [InlineData("-", "-")]
    [InlineData("--", "--")]
    [InlineData("00/00/1900", "00/00/1900")]
    [InlineData("31/02/1920", "31/02/1920")]
    [InlineData("άγνωστο", "άγνωστο")]
    public void Parse_IrregularOrPartialDates_ReturnsNullNormalizedAndPreservesRaw(string input, string expectedRaw)
    {
        var (normalized, raw) = FekDateParser.Parse(input);
        Assert.Null(normalized);
        Assert.Equal(expectedRaw, raw);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrEmpty_ReturnsNulls(string? input)
    {
        var (normalized, raw) = FekDateParser.Parse(input);
        Assert.Null(normalized);
        Assert.Null(raw);
    }
}