using System.ComponentModel.DataAnnotations;
using DigitalBoxApi.Entities;

namespace DigitalBoxApi.Models.Orders;

public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class LineItemModel
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string? Sku { get; set; }

    public static LineItemModel FromEntity(OrderLineItem li) => new()
    {
        Id = li.Id,
        Title = li.Title,
        Quantity = li.Quantity,
        Sku = li.Sku
    };
}

public class OrderListItemModel
{
    public Guid Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string Marketplace { get; set; } = nameof(Entities.Marketplace.Unknown);
    public DateOnly? ShipDate { get; set; }
    public string Status { get; set; } = nameof(OrderStatus.Open);
    public string ParseStatus { get; set; } = nameof(Entities.ParseStatus.Parsed);
    public int LineItemCount { get; set; }
    public int TotalQuantity { get; set; }
    public string? FirstItemTitle { get; set; }
    public string? ActionedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? CancelledAt { get; set; }

    public static OrderListItemModel FromEntity(Order o) => new()
    {
        Id = o.Id,
        OrderNumber = o.OrderNumber,
        Marketplace = o.Marketplace.ToString(),
        ShipDate = o.ShipDate,
        Status = o.Status.ToString(),
        ParseStatus = o.ParseStatus.ToString(),
        LineItemCount = o.LineItems.Count,
        TotalQuantity = o.LineItems.Sum(li => li.Quantity),
        FirstItemTitle = o.LineItems.OrderBy(li => li.SortOrder).Select(li => li.Title).FirstOrDefault(),
        ActionedBy = o.ActionedBy,
        CreatedAt = o.CreatedAt,
        ShippedAt = o.ShippedAt,
        CancelledAt = o.CancelledAt
    };
}

public class OrderDetailModel : OrderListItemModel
{
    public IReadOnlyList<LineItemModel> LineItems { get; set; } = Array.Empty<LineItemModel>();
    public PackingSlipInfoModel PackingSlip { get; set; } = new();
    public IReadOnlyList<OrderEventModel> Events { get; set; } = Array.Empty<OrderEventModel>();

    public static OrderDetailModel From(Order o)
    {
        var baseModel = OrderListItemModel.FromEntity(o);
        return new OrderDetailModel
        {
            Id = baseModel.Id,
            OrderNumber = baseModel.OrderNumber,
            Marketplace = baseModel.Marketplace,
            ShipDate = baseModel.ShipDate,
            Status = baseModel.Status,
            ParseStatus = baseModel.ParseStatus,
            LineItemCount = baseModel.LineItemCount,
            TotalQuantity = baseModel.TotalQuantity,
            ActionedBy = baseModel.ActionedBy,
            CreatedAt = baseModel.CreatedAt,
            ShippedAt = baseModel.ShippedAt,
            CancelledAt = baseModel.CancelledAt,
            LineItems = o.LineItems.OrderBy(li => li.SortOrder).Select(LineItemModel.FromEntity).ToList(),
            PackingSlip = new PackingSlipInfoModel
            {
                Id = o.PackingSlipId,
                FileName = o.PackingSlip?.FileName ?? string.Empty,
                ByteSize = o.PackingSlip?.ByteSize ?? 0
            },
            Events = o.Events.OrderBy(e => e.OccurredAt)
                .Select(e => new OrderEventModel
                {
                    Type = e.Type.ToString(),
                    Actor = e.Actor,
                    Detail = e.Detail,
                    OccurredAt = e.OccurredAt
                }).ToList()
        };
    }
}

public class PackingSlipInfoModel
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public int ByteSize { get; set; }
}

public class OrderEventModel
{
    public string Type { get; set; } = string.Empty;
    public string? Actor { get; set; }
    public string? Detail { get; set; }
    public DateTime OccurredAt { get; set; }
}

public class UploadFileResultModel
{
    public string FileName { get; set; } = string.Empty;
    /// <summary>"created", "duplicate", or "error".</summary>
    public string Outcome { get; set; } = string.Empty;
    public Guid? OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public string? ParseStatus { get; set; }
    public string? Message { get; set; }
}

public class UploadResponseModel
{
    public int Created { get; set; }
    public int Duplicates { get; set; }
    public int Errors { get; set; }
    public List<UploadFileResultModel> Files { get; set; } = new();
}

public class ShipOrCancelRequestModel
{
    [Required]
    [MinLength(1)]
    public List<Guid> OrderIds { get; set; } = new();

    [Required]
    [MaxLength(120)]
    public string ActionedBy { get; set; } = string.Empty;
}

public class ActionResultModel
{
    public int Updated { get; set; }
    public List<Guid> SkippedIds { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

public class UpdateOrderRequestModel
{
    [MaxLength(128)]
    public string OrderNumber { get; set; } = string.Empty;

    /// <summary>One of the Marketplace enum names.</summary>
    public string Marketplace { get; set; } = nameof(Entities.Marketplace.Unknown);

    public DateOnly? ShipDate { get; set; }

    [Required]
    [MinLength(1)]
    public List<UpdateLineItemModel> LineItems { get; set; } = new();
}

public class UpdateLineItemModel
{
    [Required]
    public string Title { get; set; } = string.Empty;

    [Range(0, int.MaxValue)]
    public int Quantity { get; set; }

    [MaxLength(128)]
    public string? Sku { get; set; }
}
