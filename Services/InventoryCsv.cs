using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace DigitalBoxApi.Services;

/// <summary>
/// Reads a customer inventory CSV. Deliberately lenient — the whole reason this feature exists
/// is that the export is messy (ragged rows, quoted fields, a BOM, a street address as a header).
/// Column mapping is chosen by the operator in the UI and passed in.
/// </summary>
public static class InventoryCsv
{
    private static CsvConfiguration Config() => new(CultureInfo.InvariantCulture)
    {
        HeaderValidated = null,
        MissingFieldFound = null,
        BadDataFound = null,
        TrimOptions = TrimOptions.Trim,
        DetectDelimiter = false,
    };

    /// <summary>Number-ish → non-negative int; blank / garbage → 0 (fixes the old NaN bug that dropped valid rows).</summary>
    public static int ParseOnHand(string? raw)
    {
        if (decimal.TryParse(raw?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
        {
            return (int)Math.Max(0, Math.Floor(d));
        }

        return 0;
    }

    /// <summary>
    /// Reads mapped rows. Throws <see cref="InvalidOperationException"/> if a mapped column name
    /// isn't in the file, or if the row count exceeds <paramref name="maxRows"/>.
    /// </summary>
    public static List<InventoryRow> ReadRows(
        Stream csv, string skuHeader, string titleHeader, string qtyHeader, int maxRows)
    {
        using var reader = new StreamReader(csv);
        using var csvReader = new CsvReader(reader, Config());

        csvReader.Read();
        csvReader.ReadHeader();
        var headers = (csvReader.HeaderRecord ?? Array.Empty<string>())
            .Select((h, i) => i == 0 ? h.TrimStart('﻿').Trim() : h.Trim())
            .ToArray();

        int IndexOf(string name)
        {
            var idx = Array.FindIndex(headers, h => h.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                throw new InvalidOperationException($"Column '{name}' is not in the file.");
            }

            return idx;
        }

        var skuIdx = IndexOf(skuHeader);
        var titleIdx = IndexOf(titleHeader);
        var qtyIdx = IndexOf(qtyHeader);

        var rows = new List<InventoryRow>();
        while (csvReader.Read())
        {
            if (rows.Count >= maxRows)
            {
                throw new InvalidOperationException($"The file has more than {maxRows:N0} rows.");
            }

            rows.Add(new InventoryRow(
                Sku: csvReader.GetField(skuIdx)?.Trim() ?? string.Empty,
                Title: csvReader.GetField(titleIdx)?.Trim() ?? string.Empty,
                OnHand: ParseOnHand(csvReader.GetField(qtyIdx))));
        }

        return rows;
    }
}
