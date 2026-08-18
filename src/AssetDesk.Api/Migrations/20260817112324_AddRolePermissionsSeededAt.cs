using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetDesk.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRolePermissionsSeededAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RolePermissionsSeededAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RolePermissionsSeededAt",
                table: "Tenants");
        }
    }
}
