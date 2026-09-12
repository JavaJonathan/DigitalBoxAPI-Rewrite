namespace DigitalBoxApi.Entities;

// The uploaded packing-slip PDF. Bytes live in Content (Postgres bytea) for v1; access always
// goes through IPackingSlipStore so the storage backend can change later.
public class PackingSlip
{
    public Guid Id { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/pdf";

    public int ByteSize { get; set; }

    // SHA-256 (hex), unique, used to reject duplicate uploads. Hashes the order's own parsed
    // content (order number + ship date + line items) when parsing succeeded, so the same order
    // re-split from a different combined export still lands on the same hash; falls back to
    // hashing the file's own bytes when parsing failed, so distinct unparseable uploads stay
    // distinct. See OrderIngestionService.ComputeDedupeHash.
    public string Sha256 { get; set; } = string.Empty;

    public byte[] Content { get; set; } = Array.Empty<byte>();

    public DateTime UploadedAt { get; set; }

    public Order? Order { get; set; }
}
