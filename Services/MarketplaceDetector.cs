using DigitalBoxApi.Entities;

namespace DigitalBoxApi.Services;

// Guesses the marketplace from an order number's shape. Ported from the old
// HttpHelper.filterForMarketplace heuristics (DigitalBoxApi/HttpHelper.js:153-170).
// Not authoritative — an operator can override it on the order.
public static class MarketplaceDetector
{
    public static Marketplace Detect(string? orderNumber)
    {
        if (string.IsNullOrWhiteSpace(orderNumber))
        {
            return Marketplace.Unknown;
        }

        var n = orderNumber.Trim();

        if (n.StartsWith("1001", StringComparison.Ordinal))
        {
            return Marketplace.Shopify;
        }

        if (n.Contains('-'))
        {
            return n.Length switch
            {
                14 => Marketplace.Ebay,
                19 => Marketplace.Amazon,
                _ => Marketplace.Unknown
            };
        }

        // No dash and not a Shopify "1001..." number — the old code treated this as Walmart.
        return Marketplace.Walmart;
    }
}
