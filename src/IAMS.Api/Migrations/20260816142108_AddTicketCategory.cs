using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAMS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Tickets",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Other");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Category",
                table: "Tickets",
                column: "Category");

            // Backfill existing tickets to Hardware rather than leaving them at the column
            // default of Other: every ticket that exists today was generalised from the old
            // Maintenance entity, and those were all equipment issues.
            migrationBuilder.Sql(
                """
                UPDATE "Tickets" SET "Category" = 'Hardware';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_Category",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Tickets");
        }
    }
}
