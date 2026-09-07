using System.Globalization;
using System.Text.RegularExpressions;

namespace Eetaa.Worker.Storage;

public static partial class FekDateParser
{
    private static readonly string[] StandardFormats =
    [
        "dd/MM/yyyy",
        "d/M/yyyy",
        "d/MM/yyyy",
        "dd/M/yyyy"
    ];

    [GeneratedRegex(@"^(\d{1,2})/(\d{1,2})/(\d{4})$")]
    private static partial Regex StandardDateRegex();

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex FourDigitYearRegex();

    /// <summary>
    /// Parses a raw FEK date string from EETAA CSV exports into a normalized ISO-8601 YYYY-MM-DD string,
    /// or returns null if it cannot be resolved to a valid calendar date, while safely preserving the raw string.
    /// </summary>
    public static (string? NormalizedDate, string? RawDate) Parse(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return (null, null);
        }

        var trimmed = rawInput.Trim();

        // Check if raw value is a simple dash or empty placeholder
        if (trimmed == "-" || trimmed == "--" || trimmed == "---")
        {
            return (null, trimmed);
        }

        // Multi-pass parsing: try standard exact formats first
        if (DateTime.TryParseExact(
                trimmed,
                StandardFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate))
        {
            return (parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), trimmed);
        }

        // Regex check in case of unexpected whitespace or slight irregularities like " 04/12/1997 "
        var match = StandardDateRegex().Match(trimmed);
        if (match.Success)
        {
            var day = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var year = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

            try
            {
                var dt = new DateTime(year, month, day);
                return (dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), trimmed);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Day or month out of range for calendar (e.g. 31/02/1920)
                return (null, trimmed);
            }
        }

        // Partial historical dates like "//1928", "1928", "-": normalized date is null, raw is preserved
        return (null, trimmed);
    }
}
