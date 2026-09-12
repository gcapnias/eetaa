namespace Eetaa.Worker.Configuration;

public sealed class EetaaOptions
{
    public const string SectionName = "Eetaa";

    public string SettlementsBaseUrl { get; set; } = "https://eetaa.gr/eetaa/metaboles/oik_metaboles.php";
    public string SettlementsCsvBaseUrl { get; set; } = "https://eetaa.gr/eetaa/metaboles/oikmet2csv.php";
    public string AuthoritiesBaseUrl { get; set; } = "https://eetaa.gr/eetaa/metaboles/dk_metaboles.php";
    public string AuthoritiesCsvBaseUrl { get; set; } = "https://eetaa.gr/eetaa/metaboles/dkmet2csv.php";
}
