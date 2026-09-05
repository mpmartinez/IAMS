using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetDesk.Api.Migrations
{
    /// <inheritdoc />
    public partial class PesoOnlyCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Currency",
                table: "Assets",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "PHP",
                oldClrType: typeof(string),
                oldType: "character varying(3)",
                oldMaxLength: 3,
                oldDefaultValue: "USD");

            // Re-denominate every existing row. The app renders all amounts with the peso sign
            // now without consulting this column, so a row left tagged USD/EUR/... would be
            // silently mislabelled rather than converted.
            //
            // This relabels the code ONLY - PurchasePrice is untouched, because converting the
            // figures would need an FX rate and an as-of date the schema does not record. If the
            // existing prices are genuinely foreign-currency amounts, convert them before
            // applying this migration.
            migrationBuilder.Sql(
                "UPDATE \"Assets\" SET \"Currency\" = 'PHP' WHERE \"Currency\" <> 'PHP';");

            migrationBuilder.UpdateData(
                table: "LookupValues",
                keyColumn: "Id",
                keyValue: 12,
                columns: new[] { "IsActive", "SortOrder" },
                values: new object[] { false, 1 });

            migrationBuilder.UpdateData(
                table: "LookupValues",
                keyColumn: "Id",
                keyValue: 13,
                columns: new[] { "IsActive", "SortOrder" },
                values: new object[] { false, 2 });

            migrationBuilder.UpdateData(
                table: "LookupValues",
                keyColumn: "Id",
                keyValue: 14,
                columns: new[] { "IsActive", "SortOrder" },
                values: new object[] { false, 3 });

            migrationBuilder.UpdateData(
                table: "LookupValues",
                keyColumn: "Id",
                keyValue: 15,
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "LookupValues",
                keyColumn: "Id",
                keyValue: 16,
                column: "IsActive",
                value: false);

            migrationBuilder.UpdateData(
                table: "LookupValues",
                keyColumn: "Id",
                keyValue: 17,
                column: "IsActive",
                value: false);

            migrationBuilder.UpdateData(
                table: "LookupValues",
                keyColumn: "Id",
                keyValue: 18,
                column: "IsActive",
                value: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Currency",
                table: "Assets",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "USD",
                oldClrType: typeof(string),
                oldType: "character varying(3)",
                oldMaxLength: 3,
                oldDefaultValue: "PHP");

            // The Up relabel is deliberately not reversed. Once it has run there is no way to
            // tell an asset that was always PHP from one that used to be USD, so flipping rows
            // back would mislabel real peso data. Reactivating the lookup rows below is enough
            // to make the other currencies selectable again.

            migrationBuilder.UpdateData(
                table: "LookupValues",
                keyColumn: "Id",
                keyValue: 12,
                columns: new[] { "IsActive", "SortOrder" },
                values: new object[] { true, 0 });

            migrationBuilder.UpdateData(
                table: "LookupValues",
                keyColumn: "Id",
                keyValue: 13,
                columns: new[] { "IsActive", "SortOrder" },
                values: new object[] { true, 1 });

            migrationBuilder.UpdateData(
                table: "LookupValues",
                keyColumn: "Id",
                keyValue: 14,
                columns: new[] { "IsActive", "SortOrder" },
                values: new object[] { true, 2 });

            migrationBuilder.UpdateData(
                table: "LookupValues",
                keyColumn: "Id",
                keyValue: 15,
                column: "SortOrder",
                value: 3);

            migrationBuilder.UpdateData(
                table: "LookupValues",
                keyColumn: "Id",
                keyValue: 16,
                column: "IsActive",
                value: true);

            migrationBuilder.UpdateData(
                table: "LookupValues",
                keyColumn: "Id",
                keyValue: 17,
                column: "IsActive",
                value: true);

            migrationBuilder.UpdateData(
                table: "LookupValues",
                keyColumn: "Id",
                keyValue: 18,
                column: "IsActive",
                value: true);
        }
    }
}
