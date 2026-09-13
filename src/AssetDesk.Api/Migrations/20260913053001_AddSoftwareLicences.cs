using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AssetDesk.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftwareLicences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SoftwareLicences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Publisher = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SupplierId = table.Column<int>(type: "integer", nullable: true),
                    LicenceModel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LicenceKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftwareLicences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoftwareLicences_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SoftwareLicences_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LicenceEntitlements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SoftwareLicenceId = table.Column<int>(type: "integer", nullable: false),
                    SeatsAdded = table.Column<int>(type: "integer", nullable: false),
                    Cost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "PHP"),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    EntitlementDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtAfter = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GoodsReceiptLineId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenceEntitlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LicenceEntitlements_GoodsReceiptLines_GoodsReceiptLineId",
                        column: x => x.GoodsReceiptLineId,
                        principalTable: "GoodsReceiptLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LicenceEntitlements_SoftwareLicences_SoftwareLicenceId",
                        column: x => x.SoftwareLicenceId,
                        principalTable: "SoftwareLicences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LicenceEntitlements_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LicenceSeatAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SoftwareLicenceId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    AssetId = table.Column<int>(type: "integer", nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AssignedByUserId = table.Column<string>(type: "text", nullable: false),
                    ReleasedByUserId = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenceSeatAssignments", x => x.Id);
                    table.CheckConstraint("CK_LicenceSeatAssignments_ExactlyOneTarget", "(\"UserId\" IS NULL) <> (\"AssetId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_LicenceSeatAssignments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LicenceSeatAssignments_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LicenceSeatAssignments_SoftwareLicences_SoftwareLicenceId",
                        column: x => x.SoftwareLicenceId,
                        principalTable: "SoftwareLicences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LicenceSeatAssignments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LicenceEntitlements_GoodsReceiptLineId",
                table: "LicenceEntitlements",
                column: "GoodsReceiptLineId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LicenceEntitlements_SoftwareLicenceId",
                table: "LicenceEntitlements",
                column: "SoftwareLicenceId");

            migrationBuilder.CreateIndex(
                name: "IX_LicenceEntitlements_TenantId",
                table: "LicenceEntitlements",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LicenceSeatAssignments_ActiveAssetSeat",
                table: "LicenceSeatAssignments",
                columns: new[] { "SoftwareLicenceId", "AssetId" },
                unique: true,
                filter: "\"ReleasedAt\" IS NULL AND \"AssetId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LicenceSeatAssignments_ActiveUserSeat",
                table: "LicenceSeatAssignments",
                columns: new[] { "SoftwareLicenceId", "UserId" },
                unique: true,
                filter: "\"ReleasedAt\" IS NULL AND \"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LicenceSeatAssignments_AssetId",
                table: "LicenceSeatAssignments",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_LicenceSeatAssignments_TenantId",
                table: "LicenceSeatAssignments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LicenceSeatAssignments_UserId",
                table: "LicenceSeatAssignments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareLicences_SupplierId",
                table: "SoftwareLicences",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareLicences_TenantId_Name",
                table: "SoftwareLicences",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LicenceEntitlements");

            migrationBuilder.DropTable(
                name: "LicenceSeatAssignments");

            migrationBuilder.DropTable(
                name: "SoftwareLicences");
        }
    }
}
