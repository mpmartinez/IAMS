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
        /// <remarks>
        /// A one-way door, deliberately left as it is. Rolling this back destroys every rate any
        /// asset was ever booked at, irrecoverably: the peso value is derived - PurchasePrice *
        /// ExchangeRate - and never stored, so dropping the column is the only copy gone. The
        /// Currency = 'USD' rows themselves survive the rollback untouched, and the rolled-back
        /// code has no rate to apply, so it resumes summing USD prices into peso totals raw - a
        /// USD 1,200 laptop adds 1,200 pesos.
        ///
        /// Re-applying Up does not undo that. It refills every row with the default rate of 1,
        /// which is indistinguishable from a rate someone actually booked, so the estate reads
        /// as fully converted while every foreign-currency asset is silently wrong. Whoever
        /// rolls this back has to recover the rates from somewhere else - the audit log, the
        /// original invoices - and re-enter them by hand.
        /// </remarks>
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
