using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IAMS.Api.Migrations
{
    /// <inheritdoc />
    public partial class TicketsFromMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- Rename tables rather than recreate, so existing rows survive ---
            //
            // EF scaffolded DropTable("Maintenances") / CreateTable("Tickets"), which would
            // destroy every maintenance record, attachment and history row. Everything below
            // renames in place instead.
            migrationBuilder.DropForeignKey(name: "FK_MaintenanceAttachments_Maintenances_MaintenanceId", table: "MaintenanceAttachments");

            // Indexes the new model no longer has, or that are superseded by the renamed-column
            // indexes created further down. Dropped before the rename so the old names are
            // unambiguous. Dropping an index touches no rows.
            migrationBuilder.DropIndex(name: "IX_Maintenances_AssetId_Status", table: "Maintenances");
            migrationBuilder.DropIndex(name: "IX_Maintenances_CreatedByUserId", table: "Maintenances");
            migrationBuilder.DropIndex(name: "IX_Maintenances_PerformedByUserId", table: "Maintenances");

            migrationBuilder.RenameTable(name: "Maintenances", newName: "Tickets");
            migrationBuilder.RenameTable(name: "MaintenanceAttachments", newName: "TicketAttachments");

            migrationBuilder.RenameColumn(name: "MaintenanceId", table: "TicketAttachments", newName: "TicketId");
            // MaintenanceAttachment called this CreatedAt; TicketAttachment calls it UploadedAt.
            migrationBuilder.RenameColumn(name: "CreatedAt", table: "TicketAttachments", newName: "UploadedAt");
            migrationBuilder.RenameColumn(name: "PerformedByUserId", table: "Tickets", newName: "AssignedToUserId");
            migrationBuilder.RenameColumn(name: "CreatedByUserId", table: "Tickets", newName: "RequesterUserId");
            migrationBuilder.RenameColumn(name: "CompletedAt", table: "Tickets", newName: "ResolvedAt");

            // --- Carry the old indexes and constraints over to the new names ---
            //
            // PostgreSQL keeps index and constraint names when a table is renamed, so without
            // this the database would still hold IX_Maintenances_* / PK_Maintenances while the
            // model snapshot records IX_Tickets_* / PK_Tickets. Any later migration that
            // touched one of them would fail against a name that does not exist. These are
            // catalogue-only operations: no rows are read or written.
            migrationBuilder.RenameIndex(name: "IX_Maintenances_AssetId", newName: "IX_Tickets_AssetId", table: "Tickets");
            migrationBuilder.RenameIndex(name: "IX_Maintenances_Status", newName: "IX_Tickets_Status", table: "Tickets");
            migrationBuilder.RenameIndex(name: "IX_Maintenances_TenantId", newName: "IX_Tickets_TenantId", table: "Tickets");

            migrationBuilder.RenameIndex(name: "IX_MaintenanceAttachments_Category", newName: "IX_TicketAttachments_Category", table: "TicketAttachments");
            migrationBuilder.RenameIndex(name: "IX_MaintenanceAttachments_MaintenanceId", newName: "IX_TicketAttachments_TicketId", table: "TicketAttachments");
            migrationBuilder.RenameIndex(name: "IX_MaintenanceAttachments_MaintenanceId_Category", newName: "IX_TicketAttachments_TicketId_Category", table: "TicketAttachments");
            migrationBuilder.RenameIndex(name: "IX_MaintenanceAttachments_TenantId", newName: "IX_TicketAttachments_TenantId", table: "TicketAttachments");
            migrationBuilder.RenameIndex(name: "IX_MaintenanceAttachments_UploadedByUserId", newName: "IX_TicketAttachments_UploadedByUserId", table: "TicketAttachments");

            // MigrationBuilder has no RenameConstraint, so these go through raw SQL.
            migrationBuilder.Sql(@"ALTER TABLE ""Tickets"" RENAME CONSTRAINT ""PK_Maintenances"" TO ""PK_Tickets"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Tickets"" RENAME CONSTRAINT ""FK_Maintenances_AspNetUsers_CreatedByUserId"" TO ""FK_Tickets_AspNetUsers_RequesterUserId"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Tickets"" RENAME CONSTRAINT ""FK_Maintenances_AspNetUsers_PerformedByUserId"" TO ""FK_Tickets_AspNetUsers_AssignedToUserId"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Tickets"" RENAME CONSTRAINT ""FK_Maintenances_Tenants_TenantId"" TO ""FK_Tickets_Tenants_TenantId"";");
            migrationBuilder.Sql(@"ALTER TABLE ""TicketAttachments"" RENAME CONSTRAINT ""PK_MaintenanceAttachments"" TO ""PK_TicketAttachments"";");
            migrationBuilder.Sql(@"ALTER TABLE ""TicketAttachments"" RENAME CONSTRAINT ""FK_MaintenanceAttachments_AspNetUsers_UploadedByUserId"" TO ""FK_TicketAttachments_AspNetUsers_UploadedByUserId"";");
            migrationBuilder.Sql(@"ALTER TABLE ""TicketAttachments"" RENAME CONSTRAINT ""FK_MaintenanceAttachments_Tenants_TenantId"" TO ""FK_TicketAttachments_Tenants_TenantId"";");

            // --- New Ticket columns ---
            migrationBuilder.AddColumn<int>(name: "TicketNumber", table: "Tickets", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<string>(name: "Type", table: "Tickets", maxLength: 50, nullable: false, defaultValue: "Incident");
            migrationBuilder.AddColumn<string>(name: "Priority", table: "Tickets", maxLength: 50, nullable: false, defaultValue: "Medium");
            migrationBuilder.AddColumn<DateTime>(name: "AssignedAt", table: "Tickets", nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "ClosedAt", table: "Tickets", nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "DueAt", table: "Tickets", nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "BreachedAt", table: "Tickets", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Resolution", table: "Tickets", maxLength: 2000, nullable: true);
            migrationBuilder.AddColumn<int>(name: "AssetAssignmentId", table: "Tickets", nullable: true);

            // Asset gains an accountable owner and a physical-verification stamp.
            migrationBuilder.AddColumn<string>(name: "OwnerUserId", table: "Assets", maxLength: 450, nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "LastVerifiedAt", table: "Assets", nullable: true);

            // A Request exists before the asset that will fulfil it, so AssetId becomes optional
            // and must not cascade-delete ticket history when an asset is removed. The old FK was
            // NOT NULL / ON DELETE CASCADE, so it is replaced rather than renamed.
            migrationBuilder.DropForeignKey(name: "FK_Maintenances_Assets_AssetId", table: "Tickets");
            migrationBuilder.AlterColumn<int>(
                name: "AssetId",
                table: "Tickets",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            // --- Data migration ---

            // Old Notes become the resolution text; the column is dropped afterwards.
            migrationBuilder.Sql(@"UPDATE ""Tickets"" SET ""Resolution"" = ""Notes"" WHERE ""Notes"" IS NOT NULL;");

            // A completed maintenance record was both resolved and closed at the same moment.
            migrationBuilder.Sql(@"UPDATE ""Tickets"" SET ""ClosedAt"" = ""ResolvedAt"" WHERE ""Status"" IN ('Completed', 'Cancelled');");

            // Status remap: Pending -> New, Completed -> Closed. InProgress and Cancelled are unchanged.
            migrationBuilder.Sql(@"UPDATE ""Tickets"" SET ""Status"" = 'New' WHERE ""Status"" = 'Pending';");
            migrationBuilder.Sql(@"UPDATE ""Tickets"" SET ""Status"" = 'Closed' WHERE ""Status"" = 'Completed';");

            // Backfill per-tenant ticket numbers in creation order.
            migrationBuilder.Sql(@"
                WITH numbered AS (
                    SELECT ""Id"", ROW_NUMBER() OVER (PARTITION BY ""TenantId"" ORDER BY ""CreatedAt"", ""Id"") AS rn
                    FROM ""Tickets""
                )
                UPDATE ""Tickets"" t SET ""TicketNumber"" = n.rn FROM numbered n WHERE t.""Id"" = n.""Id"";
            ");

            migrationBuilder.DropColumn(name: "Notes", table: "Tickets");

            // --- Indexes and constraints ---
            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_TicketNumber",
                table: "Tickets",
                columns: ["TenantId", "TicketNumber"],
                unique: true);

            migrationBuilder.CreateIndex(name: "IX_Tickets_Type", table: "Tickets", column: "Type");
            migrationBuilder.CreateIndex(name: "IX_Tickets_RequesterUserId", table: "Tickets", column: "RequesterUserId");
            migrationBuilder.CreateIndex(name: "IX_Tickets_AssignedToUserId", table: "Tickets", column: "AssignedToUserId");
            migrationBuilder.CreateIndex(name: "IX_Tickets_TenantId_Status", table: "Tickets", columns: ["TenantId", "Status"]);
            migrationBuilder.CreateIndex(name: "IX_Tickets_AssetAssignmentId", table: "Tickets", column: "AssetAssignmentId");
            migrationBuilder.CreateIndex(name: "IX_Assets_OwnerUserId", table: "Assets", column: "OwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_TicketAttachments_Tickets_TicketId",
                table: "TicketAttachments",
                column: "TicketId",
                principalTable: "Tickets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Assets_AssetId",
                table: "Tickets",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_AssetAssignments_AssetAssignmentId",
                table: "Tickets",
                column: "AssetAssignmentId",
                principalTable: "AssetAssignments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Assets_AspNetUsers_OwnerUserId",
                table: "Assets",
                column: "OwnerUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // --- New tables ---
            // Genuinely new: nothing to preserve, so these are the EF-generated CreateTable calls.
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    Changes = table.Column<string>(type: "text", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditLogs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketComments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    IsInternal = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketComments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketComments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketComments_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId",
                table: "AuditLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_EntityType_EntityId",
                table: "AuditLogs",
                columns: new[] { "TenantId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_Timestamp",
                table: "AuditLogs",
                columns: new[] { "TenantId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketComments_TenantId",
                table: "TicketComments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketComments_TicketId",
                table: "TicketComments",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketComments_TicketId_CreatedAt",
                table: "TicketComments",
                columns: new[] { "TicketId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketComments_UserId",
                table: "TicketComments",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Exact reverse of Up. The two genuinely new tables are dropped; everything the
            // Maintenance tables became is renamed back, with the data migration undone first.
            //
            // Note: this only succeeds while every ticket still has an AssetId. A Request
            // created after the migration has none, and Maintenances.AssetId is NOT NULL.
            migrationBuilder.DropTable(name: "AuditLogs");
            migrationBuilder.DropTable(name: "TicketComments");

            migrationBuilder.DropForeignKey(name: "FK_Assets_AspNetUsers_OwnerUserId", table: "Assets");
            migrationBuilder.DropForeignKey(name: "FK_Tickets_AssetAssignments_AssetAssignmentId", table: "Tickets");
            migrationBuilder.DropForeignKey(name: "FK_Tickets_Assets_AssetId", table: "Tickets");
            migrationBuilder.DropForeignKey(name: "FK_TicketAttachments_Tickets_TicketId", table: "TicketAttachments");

            migrationBuilder.DropIndex(name: "IX_Assets_OwnerUserId", table: "Assets");
            migrationBuilder.DropIndex(name: "IX_Tickets_AssetAssignmentId", table: "Tickets");
            migrationBuilder.DropIndex(name: "IX_Tickets_TenantId_Status", table: "Tickets");
            migrationBuilder.DropIndex(name: "IX_Tickets_AssignedToUserId", table: "Tickets");
            migrationBuilder.DropIndex(name: "IX_Tickets_RequesterUserId", table: "Tickets");
            migrationBuilder.DropIndex(name: "IX_Tickets_Type", table: "Tickets");
            migrationBuilder.DropIndex(name: "IX_Tickets_TenantId_TicketNumber", table: "Tickets");

            // --- Undo the data migration ---
            migrationBuilder.AddColumn<string>(name: "Notes", table: "Tickets", maxLength: 2000, nullable: true);

            migrationBuilder.Sql(@"UPDATE ""Tickets"" SET ""Status"" = 'Completed' WHERE ""Status"" = 'Closed';");
            migrationBuilder.Sql(@"UPDATE ""Tickets"" SET ""Status"" = 'Pending' WHERE ""Status"" = 'New';");
            migrationBuilder.Sql(@"UPDATE ""Tickets"" SET ""Notes"" = ""Resolution"" WHERE ""Resolution"" IS NOT NULL;");

            // AssetId goes back to NOT NULL with the original cascade delete.
            migrationBuilder.AlterColumn<int>(
                name: "AssetId",
                table: "Tickets",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Maintenances_Assets_AssetId",
                table: "Tickets",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.DropColumn(name: "LastVerifiedAt", table: "Assets");
            migrationBuilder.DropColumn(name: "OwnerUserId", table: "Assets");

            migrationBuilder.DropColumn(name: "AssetAssignmentId", table: "Tickets");
            migrationBuilder.DropColumn(name: "Resolution", table: "Tickets");
            migrationBuilder.DropColumn(name: "BreachedAt", table: "Tickets");
            migrationBuilder.DropColumn(name: "DueAt", table: "Tickets");
            migrationBuilder.DropColumn(name: "ClosedAt", table: "Tickets");
            migrationBuilder.DropColumn(name: "AssignedAt", table: "Tickets");
            migrationBuilder.DropColumn(name: "Priority", table: "Tickets");
            migrationBuilder.DropColumn(name: "Type", table: "Tickets");
            migrationBuilder.DropColumn(name: "TicketNumber", table: "Tickets");

            // --- Names back ---
            migrationBuilder.Sql(@"ALTER TABLE ""TicketAttachments"" RENAME CONSTRAINT ""FK_TicketAttachments_Tenants_TenantId"" TO ""FK_MaintenanceAttachments_Tenants_TenantId"";");
            migrationBuilder.Sql(@"ALTER TABLE ""TicketAttachments"" RENAME CONSTRAINT ""FK_TicketAttachments_AspNetUsers_UploadedByUserId"" TO ""FK_MaintenanceAttachments_AspNetUsers_UploadedByUserId"";");
            migrationBuilder.Sql(@"ALTER TABLE ""TicketAttachments"" RENAME CONSTRAINT ""PK_TicketAttachments"" TO ""PK_MaintenanceAttachments"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Tickets"" RENAME CONSTRAINT ""FK_Tickets_Tenants_TenantId"" TO ""FK_Maintenances_Tenants_TenantId"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Tickets"" RENAME CONSTRAINT ""FK_Tickets_AspNetUsers_AssignedToUserId"" TO ""FK_Maintenances_AspNetUsers_PerformedByUserId"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Tickets"" RENAME CONSTRAINT ""FK_Tickets_AspNetUsers_RequesterUserId"" TO ""FK_Maintenances_AspNetUsers_CreatedByUserId"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Tickets"" RENAME CONSTRAINT ""PK_Tickets"" TO ""PK_Maintenances"";");

            migrationBuilder.RenameIndex(name: "IX_TicketAttachments_UploadedByUserId", newName: "IX_MaintenanceAttachments_UploadedByUserId", table: "TicketAttachments");
            migrationBuilder.RenameIndex(name: "IX_TicketAttachments_TenantId", newName: "IX_MaintenanceAttachments_TenantId", table: "TicketAttachments");
            migrationBuilder.RenameIndex(name: "IX_TicketAttachments_TicketId_Category", newName: "IX_MaintenanceAttachments_MaintenanceId_Category", table: "TicketAttachments");
            migrationBuilder.RenameIndex(name: "IX_TicketAttachments_TicketId", newName: "IX_MaintenanceAttachments_MaintenanceId", table: "TicketAttachments");
            migrationBuilder.RenameIndex(name: "IX_TicketAttachments_Category", newName: "IX_MaintenanceAttachments_Category", table: "TicketAttachments");

            migrationBuilder.RenameIndex(name: "IX_Tickets_TenantId", newName: "IX_Maintenances_TenantId", table: "Tickets");
            migrationBuilder.RenameIndex(name: "IX_Tickets_Status", newName: "IX_Maintenances_Status", table: "Tickets");
            migrationBuilder.RenameIndex(name: "IX_Tickets_AssetId", newName: "IX_Maintenances_AssetId", table: "Tickets");

            migrationBuilder.RenameColumn(name: "ResolvedAt", table: "Tickets", newName: "CompletedAt");
            migrationBuilder.RenameColumn(name: "RequesterUserId", table: "Tickets", newName: "CreatedByUserId");
            migrationBuilder.RenameColumn(name: "AssignedToUserId", table: "Tickets", newName: "PerformedByUserId");
            migrationBuilder.RenameColumn(name: "UploadedAt", table: "TicketAttachments", newName: "CreatedAt");
            migrationBuilder.RenameColumn(name: "TicketId", table: "TicketAttachments", newName: "MaintenanceId");

            migrationBuilder.RenameTable(name: "TicketAttachments", newName: "MaintenanceAttachments");
            migrationBuilder.RenameTable(name: "Tickets", newName: "Maintenances");

            migrationBuilder.CreateIndex(
                name: "IX_Maintenances_AssetId_Status",
                table: "Maintenances",
                columns: new[] { "AssetId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Maintenances_CreatedByUserId",
                table: "Maintenances",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Maintenances_PerformedByUserId",
                table: "Maintenances",
                column: "PerformedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_MaintenanceAttachments_Maintenances_MaintenanceId",
                table: "MaintenanceAttachments",
                column: "MaintenanceId",
                principalTable: "Maintenances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
