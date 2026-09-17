using IAMS.Api.Common.Domain;
using IAMS.Api.Common.MasterData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IAMS.Api.Common.Persistence.Configurations;

// Phase 2a (F3 Scanning, F4 Inventory Operations) entity configurations. Same conventions as
// HierarchyConfigurations / ConnectionAndAuthConfigurations:
//  * GUID PKs; enums as strings + AddEnumCheck<T>() CHECK domain (helper lives in HierarchyConfigurations).
//  * Postgres double-quoted identifiers in raw CHECK/filter SQL; CreatedAtUtc defaults to now().
//  * decimal money/qty columns pinned to numeric(18,4) via HasPrecision(18, 4).
//  * Restrict / NoAction on cross-aggregate FKs; soft-delete via IsActive where the row is master data.
//  * Master-data-syncable tables carry the shared SyncCursor shadow column + an IX_<Table>_Sync keyset
//    index; the STORED generated column SQL, the StockCount.Variance generated column, and the xmin
//    optimistic-concurrency tokens are all wired conditionally (Npgsql only) in IamsDbContext.OnModelCreating
//    — see the "DbContext wiring" section of phase-2a-inventory-scanning.md.

public class InventoryItemConfiguration : IEntityTypeConfiguration<InventoryItem>
{
    public void Configure(EntityTypeBuilder<InventoryItem> b)
    {
        b.ToTable("InventoryItems");
        b.HasKey(x => x.Id);
        b.Property(x => x.Sku).HasMaxLength(100).IsRequired();
        b.Property(x => x.Barcode).HasMaxLength(100);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.UnitOfMeasure).HasMaxLength(20);
        b.Property(x => x.Category).HasMaxLength(200);
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("now()");

        b.HasOne(x => x.Company).WithMany()
            .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);

        // SKU/barcode are the scan-resolution lookup keys, scoped to a company.
        b.HasIndex(x => new { x.CompanyId, x.Sku }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.Barcode })
            .HasFilter("\"Barcode\" IS NOT NULL");
        b.HasIndex(x => x.TenantId);

        // Master-data sync (offline read): shadow cursor column + company-scoped keyset index.
        b.Property<DateTime>(SyncCursor.ColumnName);
        b.HasIndex("CompanyId", SyncCursor.ColumnName, "Id").HasDatabaseName("IX_InventoryItems_Sync");
    }
}

public class StockLevelConfiguration : IEntityTypeConfiguration<StockLevel>
{
    public void Configure(EntityTypeBuilder<StockLevel> b)
    {
        b.ToTable("StockLevels", t =>
            t.HasCheckConstraint("CK_StockLevels_NonNegative", "\"QuantityOnHand\" >= 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.QuantityOnHand).HasPrecision(18, 4);
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("now()");

        b.HasOne(x => x.InventoryItem).WithMany(i => i.StockLevels)
            .HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Bin).WithMany()
            .HasForeignKey(x => x.BinId).OnDelete(DeleteBehavior.Restrict);

        // One stock row per (item, bin).
        b.HasIndex(x => new { x.InventoryItemId, x.BinId }).IsUnique();
        b.HasIndex(x => x.BinId);                            // "what's in this bin"
        b.HasIndex(x => new { x.CompanyId, x.InventoryItemId }); // "where is this item"
        b.HasIndex(x => x.TenantId);

        b.Property<DateTime>(SyncCursor.ColumnName);
        b.HasIndex("CompanyId", SyncCursor.ColumnName, "Id").HasDatabaseName("IX_StockLevels_Sync");
    }
}

public class InventoryTransactionConfiguration : IEntityTypeConfiguration<InventoryTransaction>
{
    public void Configure(EntityTypeBuilder<InventoryTransaction> b)
    {
        b.ToTable("InventoryTransactions", t =>
        {
            // Per-type shape: which bins are populated and the sign/positivity of Quantity.
            t.HasCheckConstraint("CK_InvTxn_TypeShape",
                "(\"TransactionType\" = 'Receive'    AND \"SourceBinId\" IS NULL     AND \"DestinationBinId\" IS NOT NULL AND \"Quantity\" > 0) OR " +
                "(\"TransactionType\" = 'Transfer'   AND \"SourceBinId\" IS NOT NULL AND \"DestinationBinId\" IS NOT NULL AND \"SourceBinId\" <> \"DestinationBinId\" AND \"Quantity\" > 0) OR " +
                "(\"TransactionType\" = 'Adjustment' AND \"SourceBinId\" IS NOT NULL AND \"DestinationBinId\" IS NULL     AND \"Quantity\" <> 0)");
            // Adjustment must carry a reason (BR-009/010).
            t.HasCheckConstraint("CK_InvTxn_AdjustmentReason",
                "\"TransactionType\" <> 'Adjustment' OR \"AdjustmentReason\" IS NOT NULL");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.TransactionType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Quantity).HasPrecision(18, 4);
        b.Property(x => x.AdjustmentReason).HasMaxLength(400);
        b.Property(x => x.RejectionReason).HasMaxLength(400);
        b.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        b.Property(x => x.DeviceId).HasMaxLength(200);
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("now()");
        b.AddEnumCheck<InventoryTransactionType>(nameof(InventoryTransaction.TransactionType));
        b.AddEnumCheck<InventoryTransactionStatus>(nameof(InventoryTransaction.Status));

        b.HasOne(x => x.InventoryItem).WithMany()
            .HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.SourceBin).WithMany()
            .HasForeignKey(x => x.SourceBinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.DestinationBin).WithMany()
            .HasForeignKey(x => x.DestinationBinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.CreatedByUser).WithMany()
            .HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ReversalOf).WithMany()
            .HasForeignKey(x => x.ReversalOfTransactionId).OnDelete(DeleteBehavior.Restrict);

        // Offline idempotency: a replayed push with the same client key is de-duplicated.
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.CreatedAtUtc }); // ledger listing
        b.HasIndex(x => x.InventoryItemId);                   // item movement history
        b.HasIndex(x => x.SourceBinId).HasFilter("\"SourceBinId\" IS NOT NULL");
        b.HasIndex(x => x.DestinationBinId).HasFilter("\"DestinationBinId\" IS NOT NULL");
        b.HasIndex(x => x.TenantId);

        b.Property<DateTime>(SyncCursor.ColumnName);
        b.HasIndex("CompanyId", SyncCursor.ColumnName, "Id").HasDatabaseName("IX_InventoryTransactions_Sync");
    }
}

public class StockCountConfiguration : IEntityTypeConfiguration<StockCount>
{
    public void Configure(EntityTypeBuilder<StockCount> b)
    {
        b.ToTable("StockCounts", t =>
            t.HasCheckConstraint("CK_StockCounts_CountedNonNegative", "\"CountedQuantity\" >= 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.VarianceThresholdType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CountedQuantity).HasPrecision(18, 4);
        b.Property(x => x.SystemQuantity).HasPrecision(18, 4);
        b.Property(x => x.Variance).HasPrecision(18, 4);
        b.Property(x => x.VarianceThreshold).HasPrecision(18, 4);
        b.Property(x => x.RejectionReason).HasMaxLength(400);
        b.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        b.Property(x => x.DeviceId).HasMaxLength(200);
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("now()");
        b.AddEnumCheck<StockCountStatus>(nameof(StockCount.Status));
        b.AddEnumCheck<VarianceThresholdType>(nameof(StockCount.VarianceThresholdType));

        b.HasOne(x => x.InventoryItem).WithMany()
            .HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Bin).WithMany()
            .HasForeignKey(x => x.BinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.CountedByUser).WithMany()
            .HasForeignKey(x => x.CountedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ApprovedByUser).WithMany()
            .HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AdjustmentTransaction).WithMany()
            .HasForeignKey(x => x.AdjustmentTransactionId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        // Approval queue: over-threshold counts awaiting review, per company.
        b.HasIndex(x => new { x.CompanyId, x.Status })
            .HasFilter("\"Status\" = 'PendingApproval'");
        b.HasIndex(x => x.BinId);
        b.HasIndex(x => x.InventoryItemId);
        b.HasIndex(x => x.TenantId);

        b.Property<DateTime>(SyncCursor.ColumnName);
        b.HasIndex("CompanyId", SyncCursor.ColumnName, "Id").HasDatabaseName("IX_StockCounts_Sync");
    }
}

public class InventorySettingsConfiguration : IEntityTypeConfiguration<InventorySettings>
{
    public void Configure(EntityTypeBuilder<InventorySettings> b)
    {
        b.ToTable("InventorySettings");
        b.HasKey(x => x.Id);
        b.Property(x => x.VarianceThreshold).HasPrecision(18, 4);
        b.Property(x => x.VarianceThresholdType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("now()");
        b.AddEnumCheck<VarianceThresholdType>(nameof(InventorySettings.VarianceThresholdType));

        b.HasOne(x => x.Company).WithMany()
            .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.CompanyId).IsUnique(); // one settings row per company
    }
}

public class ScanEventConfiguration : IEntityTypeConfiguration<ScanEvent>
{
    public void Configure(EntityTypeBuilder<ScanEvent> b)
    {
        b.ToTable("ScanEvents");
        b.HasKey(x => x.Id);
        b.Property(x => x.RawCode).HasMaxLength(400).IsRequired();
        b.Property(x => x.ResolvedType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.DeviceId).HasMaxLength(200);
        b.Property(x => x.IdempotencyKey).HasMaxLength(200);
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("now()");
        b.AddEnumCheck<ScanResolvedType>(nameof(ScanEvent.ResolvedType));

        // ResolvedEntityId is intentionally FK-less (polymorphic target; Asset table absent until F5).
        b.HasOne(x => x.ScannedByUser).WithMany()
            .HasForeignKey(x => x.ScannedByUserId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.CompanyId, x.ScannedAtUtc }); // scan history for a company
        b.HasIndex(x => new { x.ScannedByUserId, x.ScannedAtUtc });
        b.HasIndex(x => new { x.CompanyId, x.RawCode });      // "who scanned this code"
        b.HasIndex(x => x.IdempotencyKey)
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL");
        b.HasIndex(x => x.TenantId);
    }
}
