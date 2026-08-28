using DigitalBoxApi.Entities;
using Microsoft.EntityFrameworkCore;

namespace DigitalBoxApi.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLineItem> OrderLineItems => Set<OrderLineItem>();
    public DbSet<PackingSlip> PackingSlips => Set<PackingSlip>();
    public DbSet<OrderEvent> OrderEvents => Set<OrderEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // pg_trgm powers the ILIKE search on Order.SearchText.
        builder.HasPostgresExtension("pg_trgm");

        builder.Entity<Order>(entity =>
        {
            entity.Property(o => o.OrderNumber).HasMaxLength(128);
            entity.Property(o => o.Marketplace).HasConversion<string>().HasMaxLength(32);
            entity.Property(o => o.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(o => o.ParseStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(o => o.SearchText).HasColumnType("text");
            entity.Property(o => o.ActionedBy).HasMaxLength(120);
            entity.Property(o => o.Notes).HasColumnType("text");

            entity.HasIndex(o => o.OrderNumber);
            // Serves the queue's "open, priority first" ordering and the ?priority= filter;
            // Postgres also uses this for Status-only lookups via the leading column.
            entity.HasIndex(o => new { o.Status, o.IsPriority });
            entity.HasIndex(o => o.SearchText)
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");

            entity.HasOne(o => o.PackingSlip)
                .WithOne(s => s.Order)
                .HasForeignKey<Order>(o => o.PackingSlipId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(o => o.LineItems)
                .WithOne(li => li.Order)
                .HasForeignKey(li => li.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(o => o.Events)
                .WithOne(e => e.Order)
                .HasForeignKey(e => e.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OrderLineItem>(entity =>
        {
            entity.Property(li => li.Title).HasColumnType("text");
            entity.Property(li => li.Sku).HasMaxLength(128);
        });

        builder.Entity<PackingSlip>(entity =>
        {
            entity.Property(s => s.FileName).HasMaxLength(400);
            entity.Property(s => s.ContentType).HasMaxLength(120);
            entity.Property(s => s.Sha256).HasMaxLength(64);
            entity.Property(s => s.Content).HasColumnType("bytea");
            entity.HasIndex(s => s.Sha256).IsUnique();
        });

        builder.Entity<OrderEvent>(entity =>
        {
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.Actor).HasMaxLength(120);
            entity.Property(e => e.Detail).HasColumnType("text");
            entity.HasIndex(e => e.OrderId);
        });
    }
}
