using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace IAMS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLookupValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LookupValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LookupType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Value = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LookupValues", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "LookupValues",
                columns: new[] { "Id", "CreatedAt", "IsActive", "Label", "LookupType", "SortOrder", "UpdatedAt", "Value" },
                values: new object[,]
                {
                    { 1, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Laptop", "DeviceType", 0, null, "Laptop" },
                    { 2, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Desktop", "DeviceType", 1, null, "Desktop" },
                    { 3, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Monitor", "DeviceType", 2, null, "Monitor" },
                    { 4, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Phone", "DeviceType", 3, null, "Phone" },
                    { 5, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Tablet", "DeviceType", 4, null, "Tablet" },
                    { 6, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Printer", "DeviceType", 5, null, "Printer" },
                    { 7, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Network", "DeviceType", 6, null, "Network" },
                    { 8, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Server", "DeviceType", 7, null, "Server" },
                    { 9, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Peripheral", "DeviceType", 8, null, "Peripheral" },
                    { 10, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Software", "DeviceType", 9, null, "Software" },
                    { 11, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Other", "DeviceType", 10, null, "Other" },
                    { 12, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "USD", "Currency", 0, null, "USD" },
                    { 13, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "EUR", "Currency", 1, null, "EUR" },
                    { 14, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "GBP", "Currency", 2, null, "GBP" },
                    { 15, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "PHP", "Currency", 3, null, "PHP" },
                    { 16, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "JPY", "Currency", 4, null, "JPY" },
                    { 17, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "CAD", "Currency", 5, null, "CAD" },
                    { 18, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "AUD", "Currency", 6, null, "AUD" },
                    { 19, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Receipt", "AttachmentCategory", 0, null, "Receipt" },
                    { 20, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Photo", "AttachmentCategory", 1, null, "Photo" },
                    { 21, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Warranty Document", "AttachmentCategory", 2, null, "WarrantyDocument" },
                    { 22, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Manual", "AttachmentCategory", 3, null, "Manual" },
                    { 23, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Other", "AttachmentCategory", 4, null, "Other" },
                    { 24, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Before Photo", "TicketAttachmentCategory", 0, null, "BeforePhoto" },
                    { 25, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "After Photo", "TicketAttachmentCategory", 1, null, "AfterPhoto" },
                    { 26, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Receipt", "TicketAttachmentCategory", 2, null, "Receipt" },
                    { 27, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Document", "TicketAttachmentCategory", 3, null, "Document" },
                    { 28, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Other", "TicketAttachmentCategory", 4, null, "Other" },
                    { 29, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Hardware", "TicketCategory", 0, null, "Hardware" },
                    { 30, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Software", "TicketCategory", 1, null, "Software" },
                    { 31, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Access", "TicketCategory", 2, null, "Access" },
                    { 32, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Network", "TicketCategory", 3, null, "Network" },
                    { 33, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Security", "TicketCategory", 4, null, "Security" },
                    { 34, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Other", "TicketCategory", 5, null, "Other" },
                    { 35, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "New", "TicketStatus", 0, null, "New" },
                    { 36, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Assigned", "TicketStatus", 1, null, "Assigned" },
                    { 37, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "In Progress", "TicketStatus", 2, null, "InProgress" },
                    { 38, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "On Hold", "TicketStatus", 3, null, "OnHold" },
                    { 39, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Resolved", "TicketStatus", 4, null, "Resolved" },
                    { 40, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Closed", "TicketStatus", 5, null, "Closed" },
                    { 41, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Cancelled", "TicketStatus", 6, null, "Cancelled" },
                    { 42, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Incident", "TicketType", 0, null, "Incident" },
                    { 43, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Request", "TicketType", 1, null, "Request" },
                    { 44, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Security Event", "TicketType", 2, null, "SecurityEvent" },
                    { 45, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Available", "AssetStatus", 0, null, "Available" },
                    { 46, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "In Use", "AssetStatus", 1, null, "InUse" },
                    { 47, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Maintenance", "AssetStatus", 2, null, "Maintenance" },
                    { 48, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Retired", "AssetStatus", 3, null, "Retired" },
                    { 49, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Lost", "AssetStatus", 4, null, "Lost" },
                    { 50, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Low", "TicketPriority", 0, null, "Low" },
                    { 51, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Medium", "TicketPriority", 1, null, "Medium" },
                    { 52, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "High", "TicketPriority", 2, null, "High" },
                    { 53, new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), true, "Critical", "TicketPriority", 3, null, "Critical" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_LookupValues_LookupType",
                table: "LookupValues",
                column: "LookupType");

            migrationBuilder.CreateIndex(
                name: "IX_LookupValues_LookupType_Value",
                table: "LookupValues",
                columns: new[] { "LookupType", "Value" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LookupValues");
        }
    }
}
