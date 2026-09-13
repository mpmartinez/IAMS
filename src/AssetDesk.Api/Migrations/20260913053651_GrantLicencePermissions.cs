using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetDesk.Api.Migrations
{
    /// <summary>
    /// Backfills iams:licences:view, iams:licences:manage and iams:licences:reveal onto existing
    /// tenants.
    ///
    /// SeedData.EnsureRolePermissionsAsync is gated on Tenant.RolePermissionsSeededAt and never
    /// re-runs, so a key added to Permissions.All today reaches no tenant provisioned before today.
    /// Without this, every existing Admin would silently lack these permissions and the licence
    /// screens would 403 for everyone.
    ///
    /// Backfilling unconditionally is safe only because all three keys are brand new: no tenant can
    /// have revoked something that did not exist. A migration for a key that renames or splits an
    /// existing one must not copy this blindly.
    /// </summary>
    public partial class GrantLicencePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Built-in roles carry a null TenantId and are shared by every tenant, so the grant rows
            // are the cross product of tenants and the built-in roles that hold each key - not a
            // join on RoleId. md5(...)::uuid rather than gen_random_uuid(), which is only built in
            // from PostgreSQL 13 on a database supplied externally with no version pinned; being
            // deterministic also makes the insert idempotent alongside the NOT EXISTS guard.
            migrationBuilder.Sql(@"
                INSERT INTO ""RolePermissions"" (""Id"", ""RoleId"", ""TenantId"", ""Permission"")
                SELECT
                    md5(t.""Id""::text || r.""Id"" || 'iams:licences:view')::uuid,
                    r.""Id"",
                    t.""Id"",
                    'iams:licences:view'
                FROM ""Tenants"" t
                CROSS JOIN ""AspNetRoles"" r
                WHERE r.""IsBuiltIn""
                  AND r.""Name"" IN ('Admin', 'SuperAdmin', 'Staff', 'Auditor')
                  AND NOT EXISTS (
                      SELECT 1 FROM ""RolePermissions"" x
                      WHERE x.""RoleId"" = r.""Id""
                        AND x.""TenantId"" = t.""Id""
                        AND x.""Permission"" = 'iams:licences:view'
                  );
            ");

            migrationBuilder.Sql(@"
                INSERT INTO ""RolePermissions"" (""Id"", ""RoleId"", ""TenantId"", ""Permission"")
                SELECT
                    md5(t.""Id""::text || r.""Id"" || 'iams:licences:manage')::uuid,
                    r.""Id"",
                    t.""Id"",
                    'iams:licences:manage'
                FROM ""Tenants"" t
                CROSS JOIN ""AspNetRoles"" r
                WHERE r.""IsBuiltIn""
                  AND r.""Name"" IN ('Admin', 'SuperAdmin', 'Staff')
                  AND NOT EXISTS (
                      SELECT 1 FROM ""RolePermissions"" x
                      WHERE x.""RoleId"" = r.""Id""
                        AND x.""TenantId"" = t.""Id""
                        AND x.""Permission"" = 'iams:licences:manage'
                  );
            ");

            migrationBuilder.Sql(@"
                INSERT INTO ""RolePermissions"" (""Id"", ""RoleId"", ""TenantId"", ""Permission"")
                SELECT
                    md5(t.""Id""::text || r.""Id"" || 'iams:licences:reveal')::uuid,
                    r.""Id"",
                    t.""Id"",
                    'iams:licences:reveal'
                FROM ""Tenants"" t
                CROSS JOIN ""AspNetRoles"" r
                WHERE r.""IsBuiltIn""
                  AND r.""Name"" IN ('Admin', 'SuperAdmin', 'Staff')
                  AND NOT EXISTS (
                      SELECT 1 FROM ""RolePermissions"" x
                      WHERE x.""RoleId"" = r.""Id""
                        AND x.""TenantId"" = t.""Id""
                        AND x.""Permission"" = 'iams:licences:reveal'
                  );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Removes each key entirely, including grants a tenant made to a custom role after this
            // ran: rolling back to a catalogue without these keys would otherwise leave phantom
            // ticks in the /admin/roles matrix for permissions no policy checks.
            migrationBuilder.Sql(@"
                DELETE FROM ""RolePermissions""
                WHERE ""Permission"" IN ('iams:licences:view', 'iams:licences:manage', 'iams:licences:reveal');
            ");
        }
    }
}
