using System.Globalization;
using System.Text;

using CsvHelper;
using CsvHelper.Configuration;

namespace Eetaa.Worker.Storage;

public interface ISettlementCsvParser
{
    SettlementPayload Parse(long settlementId, Stream stream);
    SettlementPayload Parse(long settlementId, string csvText);
}

public sealed class SettlementCsvParser : ISettlementCsvParser
{
    public SettlementPayload Parse(long settlementId, string csvText)
    {
        if (string.IsNullOrWhiteSpace(csvText))
        {
            return new SettlementPayload(settlementId, Array.Empty<SettlementDetailRecord>());
        }

        using var reader = new StringReader(csvText);
        return ParseInternal(settlementId, reader);
    }

    public SettlementPayload Parse(long settlementId, Stream stream)
    {
        // Preamble-aware UTF-8 StreamReader handles UTF-8 with or without BOM
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        return ParseInternal(settlementId, reader);
    }

    private static SettlementPayload ParseInternal(long settlementId, TextReader reader)
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
            return new SettlementPayload(settlementId, Array.Empty<SettlementDetailRecord>());
        }

        if (!csv.ReadHeader() || csv.HeaderRecord == null)
        {
            // No headers
            return new SettlementPayload(settlementId, Array.Empty<SettlementDetailRecord>());
        }

        var headerRecord = csv.HeaderRecord;
        var colMap = MapHeaders(headerRecord);

        var changes = new List<SettlementDetailRecord>();
        var changeOrder = 1;

        while (csv.Read())
        {
            var rawRow = csv.Parser.RawRecord?.TrimEnd('\r', '\n');

            var settlementName = GetValue(csv, colMap.NameCol) ?? string.Empty;
            var nomos = GetValue(csv, colMap.NomosCol);
            var yearStr = GetValue(csv, colMap.YearCol);
            var fekNum = GetValue(csv, colMap.FekNumCol);
            var fekIssue = GetValue(csv, colMap.FekIssueCol);
            var fekDateRawStr = GetValue(csv, colMap.FekDateCol);
            var description = GetValue(csv, colMap.DescCol) ?? string.Empty;

            // If an entire row is blank or empty, skip it
            if (string.IsNullOrWhiteSpace(settlementName) &&
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

            var (normalizedDate, rawDate) = FekDateParser.Parse(fekDateRawStr);

            changes.Add(new SettlementDetailRecord(
                SettlementId: settlementId,
                ChangeOrder: changeOrder++,
                SettlementName: settlementName,
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

        return new SettlementPayload(settlementId, changes);
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

            if (h.Contains("ΟΝΟΜΑΣΙΑ"))
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
        if (nameCol == -1 && headers.Length > 0) nameCol = 0;
        if (nomosCol == -1 && headers.Length > 1) nomosCol = 1;
        if (yearCol == -1 && headers.Length > 2) yearCol = 2;
        if (fekNumCol == -1 && headers.Length > 3) fekNumCol = 3;
        if (fekIssueCol == -1 && headers.Length > 4) fekIssueCol = 4;
        if (fekDateCol == -1 && headers.Length > 5) fekDateCol = 5;
        if (descCol == -1 && headers.Length > 6) descCol = 6;

        return new HeaderColumnIndices(nameCol, nomosCol, yearCol, fekNumCol, fekIssueCol, fekDateCol, descCol);
    }

    private readonly record struct HeaderColumnIndices(
        int NameCol,
        int NomosCol,
        int YearCol,
        int FekNumCol,
        int FekIssueCol,
        int FekDateCol,
        int DescCol
    );
}