namespace DigitalBoxApi.Entities;

/// <summary>
/// The uploaded packing-slip PDF. Bytes live in <see cref="Content"/> (Postgres bytea) for v1;
/// access always goes through IPackingSlipStore so the storage backend can change later.
/// </summary>
public class PackingSlip
{
    public Guid Id { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/pdf";

    public int ByteSize { get; set; }

    /// <summary>SHA-256 (hex) of the file bytes — unique, used to reject duplicate uploads.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public byte[] Content { get; set; } = Array.Empty<byte>();

    public DateTime UploadedAt { get; set; }

    public Order? Order { get; set; }
}
