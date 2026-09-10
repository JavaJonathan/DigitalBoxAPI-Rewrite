namespace DigitalBoxApi.Entities;

// Header row for one uploaded reference list. Exactly one per InventoryKind; re-uploading
// replaces the InventoryItem rows and updates this record in place. Backs the
// "In stock: 1,240 SKUs, updated 2h ago" line on the lookup screen.
public class InventoryUpload
{
    public Guid Id { get; set; }

    public InventoryKind Kind { get; set; }

    // Client-supplied upload filename, shown back to the operator. Display only.
    public string FileName { get; set; } = string.Empty;

    public int RowCount { get; set; }

    public DateTime UploadedAt { get; set; }

    // Display-name snapshot of who uploaded it; soft (FK-less) user reference, like Order.ActionedBy.
    public string? UploadedBy { get; set; }

    public Guid? UploadedByUserId { get; set; }
}
