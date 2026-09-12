using System.Text;

using Eetaa.Worker.Storage;

namespace Eetaa.Worker.Tests.Storage;

public class LocalAuthorityCsvParserTests
{
    private readonly LocalAuthorityCsvParser _parser = new();

    [Fact]
    public void Parse_StandardGreekLocalAuthorityWithBom_ParsesAllChangesAccurately()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "local_authority_1001.csv");
        using var stream = File.OpenRead(fixturePath);

        var payload = _parser.Parse(1001, stream);

        Assert.Equal(1001, payload.AuthorityId);
        Assert.Null(payload.ErrorMessage);
        Assert.Equal(4, payload.Changes.Count);

        // Row 1: Κοινότητα (Κ)
        var row1 = payload.Changes[0];
        Assert.Equal(1001, row1.AuthorityId);
        Assert.Equal(1, row1.ChangeOrder);
        Assert.Equal(AuthorityType.Community, row1.AuthorityType);
        Assert.Equal("Αγίας Βαρβάρας", row1.AuthorityName);
        Assert.Equal("Ηρακλείου", row1.Nomos);
        Assert.Equal(1925, row1.ChangeYear);
        Assert.Equal("27", row1.FekNumber);
        Assert.Equal("Α", row1.FekIssue);
        Assert.Equal("1925-01-31", row1.FekDate);
        Assert.Equal("31/01/1925", row1.FekDateRaw);
        Assert.Contains("Σύσταση της κοινότητας", row1.ChangeDescription);

        // Row 2: Κοινότητα (Κ)
        var row2 = payload.Changes[1];
        Assert.Equal(2, row2.ChangeOrder);
        Assert.Equal(AuthorityType.Community, row2.AuthorityType);
        Assert.Equal("Αγίας Βαρβάρας", row2.AuthorityName);
        Assert.Equal("Ηρακλείου", row2.Nomos);
        Assert.Equal(1997, row2.ChangeYear);
        Assert.Equal("244", row2.FekNumber);
        Assert.Equal("Α", row2.FekIssue);
        Assert.Equal("1997-12-04", row2.FekDate);
        Assert.Equal("04/12/1997", row2.FekDateRaw);
        Assert.Contains("καταργείται και συνενώνεται", row2.ChangeDescription);

        // Row 3: Δήμος (Δ)
        var row3 = payload.Changes[2];
        Assert.Equal(3, row3.ChangeOrder);
        Assert.Equal(AuthorityType.Municipality, row3.AuthorityType);
        Assert.Equal("Αγίας Βαρβάρας", row3.AuthorityName);
        Assert.Equal("Ηρακλείου", row3.Nomos);
        Assert.Equal(1997, row3.ChangeYear);
        Assert.Equal("244", row3.FekNumber);
        Assert.Equal("Α", row3.FekIssue);
        Assert.Equal("1997-12-04", row3.FekDate);
        Assert.Equal("04/12/1997", row3.FekDateRaw);
        Assert.Contains("Σύσταση του δήμου", row3.ChangeDescription);

        // Row 4: Δήμος (Δ)
        var row4 = payload.Changes[3];
        Assert.Equal(4, row4.ChangeOrder);
        Assert.Equal(AuthorityType.Municipality, row4.AuthorityType);
        Assert.Equal("Αγίας Βαρβάρας", row4.AuthorityName);
        Assert.Equal("Ηρακλείου", row4.Nomos);
        Assert.Equal(2010, row4.ChangeYear);
        Assert.Equal("87", row4.FekNumber);
        Assert.Equal("Α", row4.FekIssue);
        Assert.Equal("2010-06-07", row4.FekDate);
        Assert.Equal("07/06/2010", row4.FekDateRaw);
        Assert.Contains("Ο δήμος καταργείται και συνενώνεται με το δήμο Γόρτυνας", row4.ChangeDescription);
    }

    [Fact]
    public void Parse_EmptyHeaderOnlyCsv_ReturnsZeroChangesWithoutError()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "local_authority_empty.csv");
        using var stream = File.OpenRead(fixturePath);

        var payload = _parser.Parse(1002, stream);

        Assert.Equal(1002, payload.AuthorityId);
        Assert.Null(payload.ErrorMessage);
        Assert.Empty(payload.Changes);
    }

    [Fact]
    public void Parse_IrregularHistoricalDatesCsv_ParsesSafelyPreservingRawDates()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "local_authority_irregular_dates.csv");
        using var stream = File.OpenRead(fixturePath);

        var payload = _parser.Parse(777, stream);

        Assert.Equal(777, payload.AuthorityId);
        Assert.Null(payload.ErrorMessage);
        Assert.Equal(7, payload.Changes.Count);

        // Row 1: no date
        Assert.Null(payload.Changes[0].FekDate);
        Assert.Null(payload.Changes[0].FekDateRaw);

        // Row 2: 24/8/1912 -> 1912-08-24
        Assert.Equal("1912-08-24", payload.Changes[1].FekDate);
        Assert.Equal("24/8/1912", payload.Changes[1].FekDateRaw);

        // Row 3: 5/4/1920 -> 1920-04-05
        Assert.Equal("1920-04-05", payload.Changes[2].FekDate);
        Assert.Equal("5/4/1920", payload.Changes[2].FekDateRaw);

        // Row 4: //1928 -> null normalized, //1928 raw
        Assert.Null(payload.Changes[3].FekDate);
        Assert.Equal("//1928", payload.Changes[3].FekDateRaw);

        // Row 5: 1940 -> null normalized, 1940 raw
        Assert.Null(payload.Changes[4].FekDate);
        Assert.Equal("1940", payload.Changes[4].FekDateRaw);

        // Row 6: - -> null normalized, - raw
        Assert.Null(payload.Changes[5].FekDate);
        Assert.Equal("-", payload.Changes[5].FekDateRaw);

        // Row 7: 04/12/1997 -> 1997-12-04
        Assert.Equal("1997-12-04", payload.Changes[6].FekDate);
        Assert.Equal("04/12/1997", payload.Changes[6].FekDateRaw);
    }

    [Fact]
    public void Parse_StringWithoutBom_ParsesIdenticallyToStringWithBom()
    {
        var csvText = "\"ΕΙΔΟΣ ΟΤΑ\";\"ΟΝΟΜΑΣΙΑ ΟΤΑ\";ΝΟΜΟΣ;" +
                      "\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\n" +
                      "Δ;\"Αθηναίων\";Αττικής;1835;17;Α;11/11/1835;\"Σύσταση του δήμου\"\n";

        var payload = _parser.Parse(5000, csvText);

        Assert.Equal(5000, payload.AuthorityId);
        Assert.Single(payload.Changes);
        Assert.Equal(AuthorityType.Municipality, payload.Changes[0].AuthorityType);
        Assert.Equal("Αθηναίων", payload.Changes[0].AuthorityName);
        Assert.Equal("Αττικής", payload.Changes[0].Nomos);
        Assert.Equal(1835, payload.Changes[0].ChangeYear);
        Assert.Equal("17", payload.Changes[0].FekNumber);
        Assert.Equal("Α", payload.Changes[0].FekIssue);
        Assert.Equal("1835-11-11", payload.Changes[0].FekDate);
        Assert.Equal("11/11/1835", payload.Changes[0].FekDateRaw);
        Assert.Equal("Σύσταση του δήμου", payload.Changes[0].ChangeDescription);
    }

    [Fact]
    public void Parse_PositionalFallback_ParsesWhenHeadersAreNonStandard()
    {
        var csvText = "Type;Name;Nomos;Year;FekNum;FekIssue;FekDate;Description\n" +
                      "Κ;\"Κοινότητα Τεστ\";\"Χανίων\";1930;10;Α;15/05/1930;\"Περιγραφή\"\n";

        var payload = _parser.Parse(6000, csvText);

        Assert.Equal(6000, payload.AuthorityId);
        Assert.Single(payload.Changes);
        Assert.Equal(AuthorityType.Community, payload.Changes[0].AuthorityType);
        Assert.Equal("Κοινότητα Τεστ", payload.Changes[0].AuthorityName);
        Assert.Equal("Χανίων", payload.Changes[0].Nomos);
        Assert.Equal(1930, payload.Changes[0].ChangeYear);
        Assert.Equal("10", payload.Changes[0].FekNumber);
        Assert.Equal("Α", payload.Changes[0].FekIssue);
        Assert.Equal("1930-05-15", payload.Changes[0].FekDate);
        Assert.Equal("15/05/1930", payload.Changes[0].FekDateRaw);
    }
}
