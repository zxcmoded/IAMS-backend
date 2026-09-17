using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAMS.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryAndScanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Barcode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UnitOfMeasure = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    SyncCursorUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, computedColumnSql: "COALESCE(\"UpdatedAtUtc\", \"CreatedAtUtc\")", stored: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryItems_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventorySettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VarianceThreshold = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    VarianceThresholdType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventorySettings", x => x.Id);
                    table.CheckConstraint("CK_InventorySettings_VarianceThresholdType", "\"VarianceThresholdType\" IN ('AbsoluteQuantity', 'Percentage')");
                    table.ForeignKey(
                        name: "FK_InventorySettings_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScanEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScannedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawCode = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    ResolvedType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResolvedEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ScannedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanEvents", x => x.Id);
                    table.CheckConstraint("CK_ScanEvents_ResolvedType", "\"ResolvedType\" IN ('Sku', 'Location', 'Asset', 'NoMatch', 'Blocked')");
                    table.ForeignKey(
                        name: "FK_ScanEvents_Users_ScannedByUserId",
                        column: x => x.ScannedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransactionType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceBinId = table.Column<Guid>(type: "uuid", nullable: true),
                    DestinationBinId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    AdjustmentReason = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RejectionReason = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BaseSourceStockVersion = table.Column<long>(type: "bigint", nullable: true),
                    BaseDestinationStockVersion = table.Column<long>(type: "bigint", nullable: true),
                    ReversalOfTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientCreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    SyncCursorUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, computedColumnSql: "COALESCE(\"UpdatedAtUtc\", \"CreatedAtUtc\")", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryTransactions", x => x.Id);
                    table.CheckConstraint("CK_InventoryTransactions_Status", "\"Status\" IN ('Applied', 'Rejected')");
                    table.CheckConstraint("CK_InventoryTransactions_TransactionType", "\"TransactionType\" IN ('Receive', 'Transfer', 'Adjustment')");
                    table.CheckConstraint("CK_InvTxn_AdjustmentReason", "\"TransactionType\" <> 'Adjustment' OR \"AdjustmentReason\" IS NOT NULL");
                    table.CheckConstraint("CK_InvTxn_TypeShape", "(\"TransactionType\" = 'Receive'    AND \"SourceBinId\" IS NULL     AND \"DestinationBinId\" IS NOT NULL AND \"Quantity\" > 0) OR (\"TransactionType\" = 'Transfer'   AND \"SourceBinId\" IS NOT NULL AND \"DestinationBinId\" IS NOT NULL AND \"SourceBinId\" <> \"DestinationBinId\" AND \"Quantity\" > 0) OR (\"TransactionType\" = 'Adjustment' AND \"SourceBinId\" IS NOT NULL AND \"DestinationBinId\" IS NULL     AND \"Quantity\" <> 0)");
                    table.ForeignKey(
                        name: "FK_InventoryTransactions_Bins_DestinationBinId",
                        column: x => x.DestinationBinId,
                        principalTable: "Bins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransactions_Bins_SourceBinId",
                        column: x => x.SourceBinId,
                        principalTable: "Bins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransactions_InventoryItems_InventoryItemId",
                        column: x => x.InventoryItemId,
                        principalTable: "InventoryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransactions_InventoryTransactions_ReversalOfTrans~",
                        column: x => x.ReversalOfTransactionId,
                        principalTable: "InventoryTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryTransactions_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockLevels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    BinId = table.Column<Guid>(type: "uuid", nullable: false),
                    RackId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuantityOnHand = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    SyncCursorUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, computedColumnSql: "COALESCE(\"UpdatedAtUtc\", \"CreatedAtUtc\")", stored: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockLevels", x => x.Id);
                    table.CheckConstraint("CK_StockLevels_NonNegative", "\"QuantityOnHand\" >= 0");
                    table.ForeignKey(
                        name: "FK_StockLevels_Bins_BinId",
                        column: x => x.BinId,
                        principalTable: "Bins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockLevels_InventoryItems_InventoryItemId",
                        column: x => x.InventoryItemId,
                        principalTable: "InventoryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockCounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    BinId = table.Column<Guid>(type: "uuid", nullable: false),
                    RackId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CountedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    SystemQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Variance = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false, computedColumnSql: "\"CountedQuantity\" - \"SystemQuantity\"", stored: true),
                    VarianceThreshold = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    VarianceThresholdType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CountedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    AdjustmentTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BaseStockVersion = table.Column<long>(type: "bigint", nullable: true),
                    DeviceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ClientCreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    SyncCursorUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, computedColumnSql: "COALESCE(\"UpdatedAtUtc\", \"CreatedAtUtc\")", stored: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockCounts", x => x.Id);
                    table.CheckConstraint("CK_StockCounts_CountedNonNegative", "\"CountedQuantity\" >= 0");
                    table.CheckConstraint("CK_StockCounts_Status", "\"Status\" IN ('Completed', 'PendingApproval', 'Approved', 'Rejected')");
                    table.CheckConstraint("CK_StockCounts_VarianceThresholdType", "\"VarianceThresholdType\" IN ('AbsoluteQuantity', 'Percentage')");
                    table.ForeignKey(
                        name: "FK_StockCounts_Bins_BinId",
                        column: x => x.BinId,
                        principalTable: "Bins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockCounts_InventoryItems_InventoryItemId",
                        column: x => x.InventoryItemId,
                        principalTable: "InventoryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockCounts_InventoryTransactions_AdjustmentTransactionId",
                        column: x => x.AdjustmentTransactionId,
                        principalTable: "InventoryTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockCounts_Users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockCounts_Users_CountedByUserId",
                        column: x => x.CountedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_CompanyId_Barcode",
                table: "InventoryItems",
                columns: new[] { "CompanyId", "Barcode" },
                filter: "\"Barcode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_CompanyId_Sku",
                table: "InventoryItems",
                columns: new[] { "CompanyId", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_Sync",
                table: "InventoryItems",
                columns: new[] { "CompanyId", "SyncCursorUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_TenantId",
                table: "InventoryItems",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InventorySettings_CompanyId",
                table: "InventorySettings",
                column: "CompanyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_CompanyId_CreatedAtUtc",
                table: "InventoryTransactions",
                columns: new[] { "CompanyId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_CreatedByUserId",
                table: "InventoryTransactions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_DestinationBinId",
                table: "InventoryTransactions",
                column: "DestinationBinId",
                filter: "\"DestinationBinId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_IdempotencyKey",
                table: "InventoryTransactions",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_InventoryItemId",
                table: "InventoryTransactions",
                column: "InventoryItemId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_ReversalOfTransactionId",
                table: "InventoryTransactions",
                column: "ReversalOfTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_SourceBinId",
                table: "InventoryTransactions",
                column: "SourceBinId",
                filter: "\"SourceBinId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_Sync",
                table: "InventoryTransactions",
                columns: new[] { "CompanyId", "SyncCursorUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_TenantId",
                table: "InventoryTransactions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanEvents_CompanyId_RawCode",
                table: "ScanEvents",
                columns: new[] { "CompanyId", "RawCode" });

            migrationBuilder.CreateIndex(
                name: "IX_ScanEvents_CompanyId_ScannedAtUtc",
                table: "ScanEvents",
                columns: new[] { "CompanyId", "ScannedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ScanEvents_IdempotencyKey",
                table: "ScanEvents",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ScanEvents_ScannedByUserId_ScannedAtUtc",
                table: "ScanEvents",
                columns: new[] { "ScannedByUserId", "ScannedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ScanEvents_TenantId",
                table: "ScanEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_AdjustmentTransactionId",
                table: "StockCounts",
                column: "AdjustmentTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_ApprovedByUserId",
                table: "StockCounts",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_BinId",
                table: "StockCounts",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_CompanyId_Status",
                table: "StockCounts",
                columns: new[] { "CompanyId", "Status" },
                filter: "\"Status\" = 'PendingApproval'");

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_CountedByUserId",
                table: "StockCounts",
                column: "CountedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_IdempotencyKey",
                table: "StockCounts",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_InventoryItemId",
                table: "StockCounts",
                column: "InventoryItemId");

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_Sync",
                table: "StockCounts",
                columns: new[] { "CompanyId", "SyncCursorUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_TenantId",
                table: "StockCounts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StockLevels_BinId",
                table: "StockLevels",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_StockLevels_CompanyId_InventoryItemId",
                table: "StockLevels",
                columns: new[] { "CompanyId", "InventoryItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockLevels_InventoryItemId_BinId",
                table: "StockLevels",
                columns: new[] { "InventoryItemId", "BinId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockLevels_Sync",
                table: "StockLevels",
                columns: new[] { "CompanyId", "SyncCursorUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_StockLevels_TenantId",
                table: "StockLevels",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventorySettings");

            migrationBuilder.DropTable(
                name: "ScanEvents");

            migrationBuilder.DropTable(
                name: "StockCounts");

            migrationBuilder.DropTable(
                name: "StockLevels");

            migrationBuilder.DropTable(
                name: "InventoryTransactions");

            migrationBuilder.DropTable(
                name: "InventoryItems");
        }
    }
}
