using System.Text;
using DigitalBoxApi.Entities;

namespace DigitalBoxApi.Services;

// Builds the normalized Order.SearchText blob. Mirrors the old client behaviour of stripping
// whitespace and punctuation before matching (HttpHelper.filterForSearchValue).
public static class SearchText
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    public static string Build(string orderNumber, IEnumerable<OrderLineItem> lineItems, string? notes = null)
    {
        var sb = new StringBuilder();
        sb.Append(Normalize(orderNumber));
        foreach (var li in lineItems)
        {
            sb.Append(Normalize(li.Title));
            sb.Append(Normalize(li.Sku));
        }

        sb.Append(Normalize(notes));
        return sb.ToString();
    }
}
