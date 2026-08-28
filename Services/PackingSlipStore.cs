using DigitalBoxApi.Data;
using DigitalBoxApi.Entities;
using Microsoft.EntityFrameworkCore;

namespace DigitalBoxApi.Services;

public interface IPackingSlipStore
{
    /// <summary>Persists the bytes and returns a tracked (unsaved) PackingSlip entity.</summary>
    PackingSlip Create(string fileName, string contentType, byte[] content, string sha256);

    /// <summary>Returns the raw PDF bytes for a stored slip, or null if it is missing.</summary>
    Task<byte[]?> GetContentAsync(Guid packingSlipId, CancellationToken ct = default);
}

/// <summary>v1 backend: bytes live in the packing_slips.content bytea column.</summary>
public class PostgresPackingSlipStore : IPackingSlipStore
{
    private readonly ApplicationDbContext _db;

    public PostgresPackingSlipStore(ApplicationDbContext db)
    {
        _db = db;
    }

    public PackingSlip Create(string fileName, string contentType, byte[] content, string sha256)
    {
        var slip = new PackingSlip
        {
            FileName = fileName,
            ContentType = contentType,
            ByteSize = content.Length,
            Sha256 = sha256,
            Content = content,
            UploadedAt = DateTime.UtcNow
        };

        _db.PackingSlips.Add(slip);
        return slip;
    }

    public async Task<byte[]?> GetContentAsync(Guid packingSlipId, CancellationToken ct = default)
    {
        return await _db.PackingSlips
            .Where(s => s.Id == packingSlipId)
            .Select(s => s.Content)
            .FirstOrDefaultAsync(ct);
    }
}
