using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DigitalBoxApi.Services;

public record ParsedLineItem(string Title, int Quantity, string? Sku);

public record ParsedSlip(
    string OrderNumber,
    DateOnly? ShipDate,
    IReadOnlyList<ParsedLineItem> LineItems,
    ParseConfidence Confidence,
    string? Note);

public enum ParseConfidence
{
    // Order number and clean line items were found.
    Good,

    // Some text was read but key fields are missing or suspicious.
    Partial,

    // No usable text could be extracted.
    None
}

public interface IPackingSlipParser
{
    ParsedSlip Parse(byte[] pdfBytes);
}

// Layout-aware packing-slip extraction over PdfPig. Replaces the old token/URL-encoding state
// machine in ContentHelper.js. Words are grouped into visual rows; "Order #" / "Ship Date"
// anchors are found by regex; line items are anchored on the price ($) token in each row,
// with price-less rows folded into the previous item's title as wrapped text.
public partial class PdfPigPackingSlipParser : IPackingSlipParser
{
    private readonly ILogger<PdfPigPackingSlipParser> _logger;

    public PdfPigPackingSlipParser(ILogger<PdfPigPackingSlipParser> logger)
    {
        _logger = logger;
    }

    public ParsedSlip Parse(byte[] pdfBytes)
    {
        try
        {
            using var doc = PdfDocument.Open(pdfBytes);
            var rows = new List<TextRow>();
            foreach (var page in doc.GetPages())
            {
                rows.AddRange(BuildRows(page));
            }

            if (rows.Count == 0)
            {
                return new ParsedSlip(string.Empty, null, Array.Empty<ParsedLineItem>(),
                    ParseConfidence.None, "No selectable text in PDF (scanned image?).");
            }

            var orderNumber = FindOrderNumber(rows);
            var shipDate = FindShipDate(rows);
            var lineItems = FindLineItems(rows);

            var confidence = ScoreConfidence(orderNumber, lineItems);
            string? note = confidence == ParseConfidence.Good
                ? null
                : $"Auto-parse needs a check — order #: {(orderNumber.Length > 0 ? orderNumber : "missing")}, " +
                  $"{lineItems.Count} line item(s).";

            return new ParsedSlip(orderNumber, shipDate, lineItems, confidence, note);
        }
        catch (Exception ex)
        {
            // The note is persisted as OrderEvent.Detail and returned in the upload response, so
            // it must never carry ex.Message / stack detail (CLAUDE.md). Log the real exception;
            // hand back a generic note.
            _logger.LogWarning(ex, "Packing-slip parse threw.");
            return new ParsedSlip(string.Empty, null, Array.Empty<ParsedLineItem>(),
                ParseConfidence.None, "Auto-parse failed — enter this order's details manually.");
        }
    }

    private static ParseConfidence ScoreConfidence(string orderNumber, IReadOnlyList<ParsedLineItem> lineItems)
    {
        if (orderNumber.Length == 0 || lineItems.Count == 0)
        {
            return ParseConfidence.Partial;
        }

        var cleanItems = lineItems.All(li =>
            li.Quantity is > 0 and < 1000 && li.Title.Length >= 6);

        var plausibleNumber = orderNumber.Length is >= 6 and <= 30;

        return cleanItems && plausibleNumber ? ParseConfidence.Good : ParseConfidence.Partial;
    }

    // --- row reconstruction -------------------------------------------------

    private sealed record Token(string Text, double Left, double Right, double Y);

    private sealed class TextRow
    {
        public double Y { get; init; }
        public List<Token> Tokens { get; } = new();
        public string Text => string.Join(' ', Tokens.Select(t => t.Text));
    }

    // Debug helper for `dotnet run -- dump-pdf <file> --rows`.
    public static IEnumerable<string> DumpRows(byte[] pdfBytes)
    {
        using var doc = PdfDocument.Open(pdfBytes);
        var pageNo = 0;
        foreach (var page in doc.GetPages())
        {
            pageNo++;
            yield return $"--- page {pageNo} ---";
            foreach (var row in BuildRows(page))
            {
                yield return $"[y={row.Y,7:0.0}] {row.Text}";
            }
        }
    }

    private static string NormalizeToken(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            // Collapse every dash-like code point onto ASCII '-' (packing slips are riddled
            // with U+00AD soft hyphens inside order numbers — the old parser's whole "%C2%AD"
            // dance). Drop other control/format characters.
            if (ch is '­' or '‐' or '‑' or '‒' or '–' or '—'
                or '―' or '−')
            {
                sb.Append('-');
            }
            else if (!char.IsControl(ch) && ch != '﻿')
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static List<TextRow> BuildRows(Page page)
    {
        var tokens = page.GetWords()
            .Select(w => new Token(
                NormalizeToken(w.Text),
                w.BoundingBox.Left,
                w.BoundingBox.Right,
                Math.Round((w.BoundingBox.Bottom + w.BoundingBox.Top) / 2.0, 1)))
            .Where(t => t.Text.Length > 0)
            .OrderByDescending(t => t.Y)
            .ThenBy(t => t.Left)
            .ToList();

        var rows = new List<TextRow>();
        const double yTolerance = 3.5;

        foreach (var token in tokens)
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r.Y - token.Y) <= yTolerance);
            if (row is null)
            {
                row = new TextRow { Y = token.Y };
                rows.Add(row);
            }

            row.Tokens.Add(token);
        }

        foreach (var row in rows)
        {
            row.Tokens.Sort((a, b) => a.Left.CompareTo(b.Left));
        }

        return rows.OrderByDescending(r => r.Y).ToList();
    }

    // --- order number ----------------------------------------------------

    [GeneratedRegex(@"order\s*(#|no\.?|number)\s*:?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex OrderLabelRegex();

    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9\-]{4,28}")]
    private static partial Regex OrderNumberCandidateRegex();

    private static string FindOrderNumber(List<TextRow> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var text = rows[i].Text;
            var label = OrderLabelRegex().Match(text);
            if (!label.Success)
            {
                continue;
            }

            var after = text[(label.Index + label.Length)..];
            var candidate = OrderNumberCandidateRegex().Match(after);
            if (!candidate.Success || LooksLikeDate(candidate.Value))
            {
                continue;
            }

            var value = candidate.Value.Trim().Trim('-');

            // Order numbers frequently wrap: "112-5558857-" continues as "4076213" somewhere
            // on the next visual row (often after unrelated address text). If the captured
            // value ended on a dash, pull the first standalone numeric run (>= 4 digits) off
            // the next row as the tail.
            if (candidate.Value.TrimEnd().EndsWith('-') && i + 1 < rows.Count)
            {
                var tail = rows[i + 1].Tokens
                    .Select(t => t.Text)
                    .FirstOrDefault(t => t.Length >= 4 && t.All(char.IsDigit));
                if (tail is not null)
                {
                    value = $"{value}-{tail}";
                }
            }

            return Regex.Replace(value, "-{2,}", "-").Trim('-');
        }

        return string.Empty;
    }

    // --- ship date -------------------------------------------------------

    [GeneratedRegex(@"ship(ping)?\s*(date|by)\s*:?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ShipDateLabelRegex();

    [GeneratedRegex(@"\b(\d{1,2})[/\-.](\d{1,2})[/\-.](\d{2,4})\b")]
    private static partial Regex SlashDateRegex();

    [GeneratedRegex(@"\b(\d{4})-(\d{2})-(\d{2})\b")]
    private static partial Regex IsoDateRegex();

    private static bool LooksLikeDate(string value) =>
        SlashDateRegex().IsMatch(value) || IsoDateRegex().IsMatch(value);

    private static DateOnly? FindShipDate(List<TextRow> rows)
    {
        foreach (var row in rows)
        {
            var label = ShipDateLabelRegex().Match(row.Text);
            if (label.Success && TryParseDate(row.Text[(label.Index + label.Length)..], out var dated))
            {
                return dated;
            }
        }

        foreach (var row in rows)
        {
            if (TryParseDate(row.Text, out var date))
            {
                return date;
            }
        }

        return null;
    }

    private static bool TryParseDate(string text, out DateOnly date)
    {
        date = default;

        var iso = IsoDateRegex().Match(text);
        if (iso.Success && DateOnly.TryParseExact(iso.Value, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        var slash = SlashDateRegex().Match(text);
        if (!slash.Success)
        {
            return false;
        }

        var m = int.Parse(slash.Groups[1].Value);
        var d = int.Parse(slash.Groups[2].Value);
        var y = int.Parse(slash.Groups[3].Value);
        if (y < 100)
        {
            y += 2000;
        }

        try
        {
            date = new DateOnly(y, m, d);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    // --- line items -----------------------------------------------------

    [GeneratedRegex(@"\b(qty|quantity|units?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QtyHeaderRegex();

    [GeneratedRegex(@"\b(description|item|product|title)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionHeaderRegex();

    [GeneratedRegex(@"\b(subtotal|sub-total|total|thank\s*you|grand\s*total|order\s*total|amount\s*due|tracking|carrier|returns?)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex TableEndRegex();

    [GeneratedRegex(@"^[$€£]?\s?\d[\d,]*\.\d{2}$")]
    private static partial Regex PriceTokenRegex();

    [GeneratedRegex(@"[$€£]\s?\d")]
    private static partial Regex PriceAnywhereRegex();

    [GeneratedRegex(@"^\d{8,14}$|^[A-Z0-9]{6,20}$")]
    private static partial Regex SkuTokenRegex();

    private static List<ParsedLineItem> FindLineItems(List<TextRow> rows)
    {
        var items = new List<ParsedLineItem>();

        var start = rows.FindIndex(r =>
            QtyHeaderRegex().IsMatch(r.Text) && DescriptionHeaderRegex().IsMatch(r.Text));
        start = start < 0 ? 0 : start + 1;

        for (var i = start; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Tokens.Count == 0)
            {
                continue;
            }

            if (TableEndRegex().IsMatch(row.Text))
            {
                break;
            }

            var priceIndex = row.Tokens.FindIndex(t => PriceTokenRegex().IsMatch(t.Text));

            if (priceIndex < 0)
            {
                // No price on this row: it is either a wrapped continuation of the previous
                // item's title, or pre-table noise we skip.
                if (items.Count > 0 && !PriceAnywhereRegex().IsMatch(row.Text))
                {
                    var last = items[^1];
                    items[^1] = last with { Title = CleanTitle($"{last.Title} {row.Text}") };
                }

                continue;
            }

            var beforePrice = row.Tokens.Take(priceIndex).Select(t => t.Text).ToList();
            var afterPrice = row.Tokens.Skip(priceIndex + 1).ToList();

            string? sku = null;
            if (beforePrice.Count > 0 && SkuTokenRegex().IsMatch(beforePrice[0]))
            {
                sku = beforePrice[0];
                beforePrice.RemoveAt(0);
            }

            var quantity = afterPrice
                .Select(t => int.TryParse(t.Text, out var n) ? n : (int?)null)
                .FirstOrDefault(n => n is > 0 and < 100_000) ?? 1;

            var title = CleanTitle(string.Join(' ', beforePrice));
            if (title.Length == 0)
            {
                title = sku ?? "(item)";
            }

            items.Add(new ParsedLineItem(title, quantity, sku));
        }

        return items;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[$€£]\s?\d[\d.,]*")]
    private static partial Regex PriceStripRegex();

    private static string CleanTitle(string title)
    {
        var cleaned = PriceStripRegex().Replace(title, string.Empty);
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();
        return cleaned.Trim(' ', ',', '-', ':');
    }
}
