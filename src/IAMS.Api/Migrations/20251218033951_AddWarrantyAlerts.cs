using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAMS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWarrantyAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WarrantyAlerts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false),
                    AlertType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    WarrantyEndDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DaysRemaining = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AcknowledgedByUserId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WarrantyAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WarrantyAlerts_AspNetUsers_AcknowledgedByUserId",
                        column: x => x.AcknowledgedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WarrantyAlerts_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WarrantyAlerts_AcknowledgedAt",
                table: "WarrantyAlerts",
                column: "AcknowledgedAt");

            migrationBuilder.CreateIndex(
                name: "IX_WarrantyAlerts_AcknowledgedByUserId",
                table: "WarrantyAlerts",
                column: "AcknowledgedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WarrantyAlerts_AlertType",
                table: "WarrantyAlerts",
                column: "AlertType");

            migrationBuilder.CreateIndex(
                name: "IX_WarrantyAlerts_AssetId",
                table: "WarrantyAlerts",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_WarrantyAlerts_AssetId_AlertType",
                table: "WarrantyAlerts",
                columns: new[] { "AssetId", "AlertType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WarrantyAlerts");
        }
    }
}
