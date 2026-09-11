using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetDesk.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExchangeRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                table: "Assets",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 1m);

            // Matched on (LookupType, Value), not Id. LookupValueSeed assigns the currency rows
            // out of order - PHP 15, USD 12, JPY 16, AUD 18 - so an id here is a magic number
            // whose correctness nobody can see at the call site. Value is documented immutable
            // on LookupValue, which makes it the stable key.
            migrationBuilder.Sql(
                """
                UPDATE "LookupValues"
                SET "IsActive" = true, "UpdatedAt" = now()
                WHERE "LookupType" = 'Currency' AND "Value" = 'USD';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                table: "Assets");

            migrationBuilder.Sql(
                """
                UPDATE "LookupValues"
                SET "IsActive" = false, "UpdatedAt" = now()
                WHERE "LookupType" = 'Currency' AND "Value" = 'USD';
                """);
        }
    }
}
