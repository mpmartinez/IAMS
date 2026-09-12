using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetDesk.Api.Migrations
{
    /// <summary>
    /// Adds the idempotency marker a replayed goods receipt uses to recognise its own committed
    /// work - see GoodsReceipt.RequestId - and the unique index that is the actual guarantee.
    /// </summary>
    public partial class AddGoodsReceiptRequestId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RequestId",
                table: "GoodsReceipts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Every existing receipt takes the all-zero default, so on any database holding more
            // than one the unique index below would fail to build and the whole startup
            // migration would abort. Give the existing rows distinct values first.
            //
            // md5(...)::uuid rather than gen_random_uuid(), for the two reasons
            // 20260906031321_GrantAuditViewPermission gives: gen_random_uuid() is only built in
            // from PostgreSQL 13 and the database here is supplied externally with no version
            // pinned, and a deterministic value makes this statement idempotent - re-running it
            // produces the same uuids rather than churning them.
            //
            // These are backfilled markers, not real request ids: no in-flight call is waiting
            // on them, and they exist only so the index can be built. They are still unique, so
            // they cannot collide with a future call's Guid either.
            migrationBuilder.Sql("""
                UPDATE "GoodsReceipts"
                SET "RequestId" = md5('goods-receipt-request-' || "Id"::text)::uuid
                WHERE "RequestId" = '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipts_RequestId",
                table: "GoodsReceipts",
                column: "RequestId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GoodsReceipts_RequestId",
                table: "GoodsReceipts");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "GoodsReceipts");
        }
    }
}
