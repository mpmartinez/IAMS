using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAMS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssetAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReturnedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AssignedByUserId = table.Column<string>(type: "TEXT", nullable: false),
                    ReturnedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ReturnNotes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ReturnCondition = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetAssignments_AspNetUsers_AssignedByUserId",
                        column: x => x.AssignedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetAssignments_AspNetUsers_ReturnedByUserId",
                        column: x => x.ReturnedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetAssignments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetAssignments_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssetAssignments_AssetId",
                table: "AssetAssignments",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetAssignments_AssignedByUserId",
                table: "AssetAssignments",
                column: "AssignedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetAssignments_ReturnedByUserId",
                table: "AssetAssignments",
                column: "ReturnedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetAssignments_UserId",
                table: "AssetAssignments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetAssignments_UserId_ReturnedAt",
                table: "AssetAssignments",
                columns: new[] { "UserId", "ReturnedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssetAssignments");
        }
    }
}
