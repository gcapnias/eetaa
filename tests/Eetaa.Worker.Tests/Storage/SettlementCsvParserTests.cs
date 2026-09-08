using System.Text;

using Eetaa.Worker.Storage;

namespace Eetaa.Worker.Tests.Storage;

public class SettlementCsvParserTests
{
    private readonly SettlementCsvParser _parser = new();

    [Fact]
    public void Parse_StandardGreekSettlementWithBom_ParsesAllChangesAccurately()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "settlement_25734.csv");
        using var stream = File.OpenRead(fixturePath);

        var payload = _parser.Parse(25734, stream);

        Assert.Equal(25734, payload.SettlementId);
        Assert.Null(payload.ErrorMessage);
        Assert.Equal(4, payload.Changes.Count);

        // Row 1
        var row1 = payload.Changes[0];
        Assert.Equal(25734, row1.SettlementId);
        Assert.Equal(1, row1.ChangeOrder);
        Assert.Equal("21η Απριλίου", row1.SettlementName);
        Assert.Equal("Θεσσαλονίκης", row1.Nomos);
        Assert.Equal(1971, row1.ChangeYear);
        Assert.Null(row1.FekNumber);
        Assert.Null(row1.FekIssue);
        Assert.Equal("1971-03-14", row1.FekDate);
        Assert.Equal("14/03/1971", row1.FekDateRaw);
        Assert.Equal("Αναγνώριση του οικισμού και προσάρτησή του στην κοινότητα Θέρμης", row1.ChangeDescription);

        // Row 2
        var row2 = payload.Changes[1];
        Assert.Equal(2, row2.ChangeOrder);
        Assert.Equal("21η Απριλίου", row2.SettlementName);
        Assert.Equal("Θεσσαλονίκης", row2.Nomos);
        Assert.Equal(1975, row2.ChangeYear);
        Assert.Equal("96", row2.FekNumber);
        Assert.Equal("Α", row2.FekIssue);
        Assert.Equal("1975-05-21", row2.FekDate);
        Assert.Equal("21/05/1975", row2.FekDateRaw);
        Assert.Equal("Ο οικισμός μετονομάζεται σε Καλλιθέα", row2.ChangeDescription);

        // Row 3
        var row3 = payload.Changes[2];
        Assert.Equal(3, row3.ChangeOrder);
        Assert.Equal("Καλλιθέα", row3.SettlementName);
        Assert.Equal("Θεσσαλονίκης", row3.Nomos);
        Assert.Equal(1975, row3.ChangeYear);
        Assert.Equal("96", row3.FekNumber);
        Assert.Equal("Α", row3.FekIssue);
        Assert.Equal("1975-05-21", row3.FekDate);
        Assert.Equal("21/05/1975", row3.FekDateRaw);

        // Row 4
        var row4 = payload.Changes[3];
        Assert.Equal(4, row4.ChangeOrder);
        Assert.Equal("Καλλιθέα", row4.SettlementName);
        Assert.Equal(1981, row4.ChangeYear);
        Assert.Null(row4.FekNumber);
        Assert.Null(row4.FekIssue);
        Assert.Equal("1981-04-05", row4.FekDate);
        Assert.Equal("05/04/1981", row4.FekDateRaw);
        Assert.Contains("καταργείται", row4.ChangeDescription);
    }

    [Fact]
    public void Parse_EmptyHeaderOnlyCsv_ReturnsZeroChangesWithoutError()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "settlement_empty.csv");
        using var stream = File.OpenRead(fixturePath);

        var payload = _parser.Parse(1, stream);

        Assert.Equal(1, payload.SettlementId);
        Assert.Null(payload.ErrorMessage);
        Assert.Empty(payload.Changes);
    }

    [Fact]
    public void Parse_IrregularHistoricalDatesCsv_ParsesSafelyPreservingRawDates()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "settlement_irregular_dates.csv");
        using var stream = File.OpenRead(fixturePath);

        var payload = _parser.Parse(777, stream);

        Assert.Equal(777, payload.SettlementId);
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
        var csvText = "\"ΟΝΟΜΑΣΙΑ ΟΙΚΙΣΜΟΥ\";ΝΟΜΟΣ;\"ΕΤΟΣ ΜΕΤΑΒΟΛΗΣ\";\"ΑΡΙΘΜΟΣ ΦΕΚ\";\"ΤΕΥΧΟΣ ΦΕΚ\";\"ΗΜΕΡΟΜΗΝΙΑ ΦΕΚ\";\"ΠΕΡΙΓΡΑΦΗ ΜΕΤΑΒΟΛΗΣ\"\n" +
                      "\"Δαρμαροχώρι\";Χανίων;1925;27;Α;31/01/1925;\"Ο οικισμός προσαρτάται στην κοινότητα Σπηλιάς\"\n";

        var payload = _parser.Parse(20000, csvText);

        Assert.Equal(20000, payload.SettlementId);
        Assert.Single(payload.Changes);
        Assert.Equal("Δαρμαροχώρι", payload.Changes[0].SettlementName);
        Assert.Equal("Χανίων", payload.Changes[0].Nomos);
        Assert.Equal(1925, payload.Changes[0].ChangeYear);
        Assert.Equal("27", payload.Changes[0].FekNumber);
        Assert.Equal("Α", payload.Changes[0].FekIssue);
        Assert.Equal("1925-01-31", payload.Changes[0].FekDate);
        Assert.Equal("31/01/1925", payload.Changes[0].FekDateRaw);
    }
}