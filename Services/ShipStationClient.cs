using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DigitalBoxApi.Services;

public class ShipStationOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://ssapi.shipstation.com";
}

// One ShipStation order, flattened to what the lookup screen needs. Successor to the old
// Express proxy in CustomerServiceApp/server.js — the credential now lives in server config
// instead of a checked-in JS file.
public sealed record ShipStationOrder(
    string OrderNumber,
    string OrderStatus,
    DateTime? OrderDate,
    string? TrackingNumber,
    string? Carrier,
    ShipStationAddress? ShipTo,
    IReadOnlyList<ShipStationItem> Items);

public sealed record ShipStationAddress(
    string? Name, string? Street1, string? Street2,
    string? City, string? State, string? PostalCode, string? Country);

public sealed record ShipStationItem(string Name, string? Sku, int Quantity, decimal UnitPrice);

// Outcome of a lookup: the order (or null if ShipStation didn't have it), plus a generic
// error string when the call itself failed (timeout / 5xx / auth). The caller never sees the
// raw exception.
public sealed record ShipStationLookup(ShipStationOrder? Order, string? Error)
{
    public static readonly ShipStationLookup NotFound = new(null, null);
}

public class ShipStationClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly ShipStationOptions _options;
    private readonly ILogger<ShipStationClient> _logger;

    public ShipStationClient(HttpClient http, IConfiguration config, ILogger<ShipStationClient> logger)
    {
        _http = http;
        _logger = logger;
        _options = config.GetSection("ShipStation").Get<ShipStationOptions>() ?? new ShipStationOptions();

        _http.Timeout = TimeSpan.FromSeconds(10);
        if (IsConfigured)
        {
            _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
            var creds = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.ApiKey}:{_options.ApiSecret}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }
    }

    // No key/secret → the lookup endpoint degrades to a DigitalBox-DB-only result rather than
    // failing. This is a deliberate departure from the fail-fast Jwt:Key / Cors checks: the DB
    // lookup is the primary path and must not be blocked by a missing optional credential.
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.ApiSecret);

    public async Task<ShipStationLookup> LookupAsync(string orderNumber, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return ShipStationLookup.NotFound;
        }

        var encoded = Uri.EscapeDataString(orderNumber);
        try
        {
            // Order + its shipments in parallel, exactly as the old proxy did.
            var ordersTask = GetAsync<OrdersEnvelope>($"orders?orderNumber={encoded}", ct);
            var shipmentsTask = GetAsync<ShipmentsEnvelope>($"shipments?orderNumber={encoded}", ct);
            await Task.WhenAll(ordersTask, shipmentsTask);

            var order = ordersTask.Result?.Orders?.FirstOrDefault();
            if (order is null)
            {
                return ShipStationLookup.NotFound;
            }

            var shipment = shipmentsTask.Result?.Shipments?
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.TrackingNumber));

            return new ShipStationLookup(Map(order, shipment), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Timeout, 5xx, bad JSON, auth failure — log it, hand the caller a generic string.
            _logger.LogWarning(ex, "ShipStation lookup for order {OrderNumber} failed.", orderNumber);
            return new ShipStationLookup(null, "ShipStation is unavailable right now.");
        }
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return default;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
    }

    private static ShipStationOrder Map(SsOrder o, SsShipment? shipment) => new(
        OrderNumber: o.OrderNumber ?? string.Empty,
        OrderStatus: o.OrderStatus ?? string.Empty,
        OrderDate: o.OrderDate,
        TrackingNumber: shipment?.TrackingNumber,
        Carrier: shipment?.CarrierCode,
        ShipTo: o.ShipTo is null ? null : new ShipStationAddress(
            o.ShipTo.Name, o.ShipTo.Street1, o.ShipTo.Street2,
            o.ShipTo.City, o.ShipTo.State, o.ShipTo.PostalCode, o.ShipTo.Country),
        Items: (o.Items ?? new List<SsItem>())
            .Select(i => new ShipStationItem(
                i.Name ?? string.Empty, i.Sku, i.Quantity, i.UnitPrice))
            .ToList());

    // --- wire models (ShipStation JSON) -----------------------------------

    private sealed class OrdersEnvelope
    {
        public List<SsOrder>? Orders { get; set; }
    }

    private sealed class ShipmentsEnvelope
    {
        public List<SsShipment>? Shipments { get; set; }
    }

    private sealed class SsOrder
    {
        public string? OrderNumber { get; set; }
        public string? OrderStatus { get; set; }
        public DateTime? OrderDate { get; set; }
        public SsAddress? ShipTo { get; set; }
        public List<SsItem>? Items { get; set; }
    }

    private sealed class SsAddress
    {
        public string? Name { get; set; }
        public string? Street1 { get; set; }
        public string? Street2 { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? PostalCode { get; set; }
        public string? Country { get; set; }
    }

    private sealed class SsItem
    {
        public string? Name { get; set; }
        public string? Sku { get; set; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }

    private sealed class SsShipment
    {
        public string? TrackingNumber { get; set; }
        public string? CarrierCode { get; set; }
    }
}
