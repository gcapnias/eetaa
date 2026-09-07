using Eetaa.Worker.Discovery;

namespace Eetaa.Worker.Tests.Discovery;

public class IndexParserTests
{
    [Fact]
    public void ParseSettlements_ExtractsExpectedSettlements_FromPage1Fixture()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "index_page_1.html"));
        var parser = new IndexParser();

        var settlements = parser.ParseSettlements(html).ToList();

        // Page 1 has 15 settlement entries in table#results
        Assert.Equal(15, settlements.Count);

        // Verify first entry: id=25734, "21η Απριλίου (Θεσσαλονίκης)"
        Assert.Equal(25734, settlements[0].SettlementId);
        Assert.Equal("21η Απριλίου (Θεσσαλονίκης)", settlements[0].NameHint);

        // Verify entries with duplicate IDs present on the same page (aliases):
        // id 15985 is repeated for Αβανλήδες (Θεσσαλονίκης) and Αβανλήδες (Χαλκιδικής)
        var count15985 = settlements.Count(s => s.SettlementId == 15985);
        Assert.Equal(2, count15985);
    }

    [Fact]
    public void ParseSettlements_HandlesIrregularMarkupAndDeduplicates_Correctly()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "index_page_irregular.html"));
        var parser = new IndexParser();

        var settlements = parser.ParseSettlements(html).ToList();

        // Should extract: 99901, 99902, 99903, 99901 (alias), 99904
        Assert.Equal(5, settlements.Count);

        var deduplicated = parser.Deduplicate(settlements).ToList();
        Assert.Equal(4, deduplicated.Count);
        Assert.Contains(deduplicated, s => s.SettlementId == 99901 && s.NameHint == "Αγία Παρασκευή (Αττικής)");
        Assert.Contains(deduplicated, s => s.SettlementId == 99902 && s.NameHint == "Αγία Τριάδα (Βοιωτίας)");
        Assert.Contains(deduplicated, s => s.SettlementId == 99903 && s.NameHint == "Άγιος Γεώργιος (Κορινθίας)");
        Assert.Contains(deduplicated, s => s.SettlementId == 99904 && s.NameHint == "Βαθύ (Σάμου)");
    }

    [Fact]
    public void ParseSettlements_ReturnsEmpty_ForEmptyResultsTable()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "index_page_empty.html"));
        var parser = new IndexParser();

        var settlements = parser.ParseSettlements(html).ToList();
        Assert.Empty(settlements);
    }

    [Fact]
    public void ParseTotalPages_ExtractsMaxPage_FromPaginationBar()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "index_page_1.html"));
        var parser = new IndexParser();

        var totalPages = parser.ParseTotalPages(html);
        Assert.Equal(2247, totalPages);
    }
}
