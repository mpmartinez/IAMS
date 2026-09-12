using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetDesk.Api.Migrations
{
    /// <summary>
    /// Backfills the new iams:procurement:view and iams:procurement:manage grants onto existing
    /// tenants.
    ///
    /// SeedData.EnsureRolePermissionsAsync is gated on Tenant.RolePermissionsSeededAt and never
    /// re-runs, so a key added to Permissions.All today reaches no tenant provisioned before
    /// today. Without this, every existing Admin would silently lack these permissions and the
    /// procurement screens built in later tasks would 403 for everyone.
    ///
    /// Backfilling unconditionally is safe only because both keys are brand new: no tenant can
    /// have deliberately revoked something that did not exist, so there is no admin decision to
    /// trample. A future migration for a key that renames or splits an existing one must not
    /// copy this blindly - there, an existing revocation would be real and must be respected.
    /// </summary>
    public partial class GrantProcurementPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Built-in roles carry a null TenantId and are shared by every tenant, so the grant
            // rows are the cross product of tenants and the built-in roles that should hold each
            // key - not a join on RoleId.
            //
            // The id comes from md5(...)::uuid rather than gen_random_uuid(): that function is
            // only a built-in from PostgreSQL 13, and the database here is supplied externally
            // with no version pinned. Being deterministic also makes the insert idempotent - the
            // NOT EXISTS guard and the id agree with each other on a re-run.
            //
            // Two literal blocks rather than one interpolated helper. They differ only in the key
            // and the role list, but a migration that builds SQL by string interpolation reads
            // like an injection site to everyone who meets it later, and the two migrations this
            // copies both spell their SQL out.
            migrationBuilder.Sql(@"
                INSERT INTO ""RolePermissions"" (""Id"", ""RoleId"", ""TenantId"", ""Permission"")
                SELECT
                    md5(t.""Id""::text || r.""Id"" || 'iams:procurement:view')::uuid,
                    r.""Id"",
                    t.""Id"",
                    'iams:procurement:view'
                FROM ""Tenants"" t
                CROSS JOIN ""AspNetRoles"" r
                WHERE r.""IsBuiltIn""
                  AND r.""Name"" IN ('Admin', 'SuperAdmin', 'Staff', 'Auditor')
                  AND NOT EXISTS (
                      SELECT 1 FROM ""RolePermissions"" x
                      WHERE x.""RoleId"" = r.""Id""
                        AND x.""TenantId"" = t.""Id""
                        AND x.""Permission"" = 'iams:procurement:view'
                  );
            ");

            migrationBuilder.Sql(@"
                INSERT INTO ""RolePermissions"" (""Id"", ""RoleId"", ""TenantId"", ""Permission"")
                SELECT
                    md5(t.""Id""::text || r.""Id"" || 'iams:procurement:manage')::uuid,
                    r.""Id"",
                    t.""Id"",
                    'iams:procurement:manage'
                FROM ""Tenants"" t
                CROSS JOIN ""AspNetRoles"" r
                WHERE r.""IsBuiltIn""
                  AND r.""Name"" IN ('Admin', 'SuperAdmin', 'Staff')
                  AND NOT EXISTS (
                      SELECT 1 FROM ""RolePermissions"" x
                      WHERE x.""RoleId"" = r.""Id""
                        AND x.""TenantId"" = t.""Id""
                        AND x.""Permission"" = 'iams:procurement:manage'
                  );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Removes each key entirely, including from any tenant that granted it to a custom
            // role after this migration ran. That is the correct reversal: rolling back to a
            // catalogue without these keys leaves those rows referencing a permission no policy
            // checks, which an admin would see as a phantom tick in the /admin/roles matrix.
            migrationBuilder.Sql(@"
                DELETE FROM ""RolePermissions""
                WHERE ""Permission"" IN ('iams:procurement:view', 'iams:procurement:manage');
            ");
        }
    }
}
