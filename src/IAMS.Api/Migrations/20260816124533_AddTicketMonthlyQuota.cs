using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAMS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketMonthlyQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxTicketsPerMonth",
                table: "Tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill existing tenants by their current tier so the new quota takes
            // effect immediately rather than leaving every existing tenant at the
            // column default of 0 (which would block ticket creation entirely) until
            // something happens to touch their subscription tier.
            migrationBuilder.Sql(
                """
                UPDATE "Tenants" SET "MaxTicketsPerMonth" = CASE "SubscriptionTier"
                    WHEN 'Free' THEN 100
                    WHEN 'Pro' THEN 1000
                    WHEN 'Enterprise' THEN 2147483647
                    ELSE 100
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxTicketsPerMonth",
                table: "Tenants");
        }
    }
}
