using System.Globalization;
using System.Text;

using CsvHelper;
using CsvHelper.Configuration;

namespace Eetaa.Worker.Storage;

public interface ILocalAuthorityCsvParser
{
    LocalAuthorityPayload Parse(long authorityId, Stream stream);
    LocalAuthorityPayload Parse(long authorityId, string csvText);
}

public sealed class LocalAuthorityCsvParser : ILocalAuthorityCsvParser
{
    public LocalAuthorityPayload Parse(long authorityId, string csvText)
    {
        if (string.IsNullOrWhiteSpace(csvText))
        {
            return new LocalAuthorityPayload(authorityId, Array.Empty<LocalAuthorityDetailRecord>());
        }

        using var reader = new StringReader(csvText);
        return ParseInternal(authorityId, reader);
    }

    public LocalAuthorityPayload Parse(long authorityId, Stream stream)
    {
        // Preamble-aware UTF-8 StreamReader handles UTF-8 with or without BOM
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        return ParseInternal(authorityId, reader);
    }

    private static LocalAuthorityPayload ParseInternal(long authorityId, TextReader reader)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ";",
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            IgnoreBlankLines = true,
            MissingFieldFound = null,
            BadDataFound = null,
            HeaderValidated = null
        };

        using var csv = new CsvReader(reader, config);

        if (!csv.Read())
        {
            // Empty file
            return new LocalAuthorityPayload(authorityId, Array.Empty<LocalAuthorityDetailRecord>());
        }

        if (!csv.ReadHeader() || csv.HeaderRecord == null)
        {
            // No headers
            return new LocalAuthorityPayload(authorityId, Array.Empty<LocalAuthorityDetailRecord>());
        }

        var headerRecord = csv.HeaderRecord;
        var colMap = MapHeaders(headerRecord);

        var changes = new List<LocalAuthorityDetailRecord>();
        var changeOrder = 1;

        while (csv.Read())
        {
            var rawRow = csv.Parser.RawRecord?.TrimEnd('\r', '\n');

            var typeStr = GetValue(csv, colMap.OtaTypeCol);
            var authorityName = GetValue(csv, colMap.NameCol) ?? string.Empty;
            var nomos = GetValue(csv, colMap.NomosCol);
            var yearStr = GetValue(csv, colMap.YearCol);
            var fekNum = GetValue(csv, colMap.FekNumCol);
            var fekIssue = GetValue(csv, colMap.FekIssueCol);
            var fekDateRawStr = GetValue(csv, colMap.FekDateCol);
            var description = GetValue(csv, colMap.DescCol) ?? string.Empty;

            // If an entire row is blank or empty, skip it
            if (string.IsNullOrWhiteSpace(typeStr) &&
                string.IsNullOrWhiteSpace(authorityName) &&
                string.IsNullOrWhiteSpace(nomos) &&
                string.IsNullOrWhiteSpace(yearStr) &&
                string.IsNullOrWhiteSpace(description))
            {
                continue;
            }

            int? changeYear = null;
            if (!string.IsNullOrWhiteSpace(yearStr) && int.TryParse(yearStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedYear))
            {
                changeYear = parsedYear;
            }

            var authorityType = AuthorityTypeExtensions.FromCode(typeStr);
            var (normalizedDate, rawDate) = FekDateParser.Parse(fekDateRawStr);

            changes.Add(new LocalAuthorityDetailRecord(
                AuthorityId: authorityId,
                ChangeOrder: changeOrder++,
                AuthorityType: authorityType,
                AuthorityName: authorityName,
                Nomos: nomos,
                ChangeYear: changeYear,
                FekNumber: fekNum,
                FekIssue: fekIssue,
                FekDate: normalizedDate,
                FekDateRaw: rawDate,
                ChangeDescription: description,
                RawRecord: rawRow
            ));
        }

        return new LocalAuthorityPayload(authorityId, changes);
    }

    private static string? GetValue(CsvReader csv, int colIndex)
    {
        if (colIndex < 0)
        {
            return null;
        }

        var val = csv.GetField(colIndex)?.Trim();
        return string.IsNullOrEmpty(val) ? null : val;
    }

    private static HeaderColumnIndices MapHeaders(string[] headers)
    {
        int otaTypeCol = -1;
        int nameCol = -1;
        int nomosCol = -1;
        int yearCol = -1;
        int fekNumCol = -1;
        int fekIssueCol = -1;
        int fekDateCol = -1;
        int descCol = -1;

        for (int i = 0; i < headers.Length; i++)
        {
            var h = headers[i].Trim().Trim('"').ToUpperInvariant();

            if (h.Contains("ΕΙΔΟΣ") || (h.Contains("ΕΙΔ") && h.Contains("ΟΤΑ")))
            {
                otaTypeCol = i;
            }
            else if (h.Contains("ΟΝΟΜΑΣΙΑ"))
            {
                nameCol = i;
            }
            else if (h.Contains("ΝΟΜΟΣ"))
            {
                nomosCol = i;
            }
            else if (h.Contains("ΕΤΟΣ"))
            {
                yearCol = i;
            }
            else if (h.Contains("ΑΡΙΘΜΟΣ") && h.Contains("ΦΕΚ"))
            {
                fekNumCol = i;
            }
            else if (h.Contains("ΤΕΥΧΟΣ") && h.Contains("ΦΕΚ"))
            {
                fekIssueCol = i;
            }
            else if (h.Contains("ΗΜΕΡΟΜΗΝΙΑ") && h.Contains("ΦΕΚ"))
            {
                fekDateCol = i;
            }
            else if (h.Contains("ΠΕΡΙΓΡΑΦΗ") || h.Contains("ΜΕΤΑΒΟΛ"))
            {
                descCol = i;
            }
        }

        // Positional fallback if Greek headers weren't matched
        if (otaTypeCol == -1 && headers.Length > 0) otaTypeCol = 0;
        if (nameCol == -1 && headers.Length > 1) nameCol = 1;
        if (nomosCol == -1 && headers.Length > 2) nomosCol = 2;
        if (yearCol == -1 && headers.Length > 3) yearCol = 3;
        if (fekNumCol == -1 && headers.Length > 4) fekNumCol = 4;
        if (fekIssueCol == -1 && headers.Length > 5) fekIssueCol = 5;
        if (fekDateCol == -1 && headers.Length > 6) fekDateCol = 6;
        if (descCol == -1 && headers.Length > 7) descCol = 7;

        return new HeaderColumnIndices(otaTypeCol, nameCol, nomosCol, yearCol, fekNumCol, fekIssueCol, fekDateCol, descCol);
    }

    private readonly record struct HeaderColumnIndices(
        int OtaTypeCol,
        int NameCol,
        int NomosCol,
        int YearCol,
        int FekNumCol,
        int FekIssueCol,
        int FekDateCol,
        int DescCol
    );
}
