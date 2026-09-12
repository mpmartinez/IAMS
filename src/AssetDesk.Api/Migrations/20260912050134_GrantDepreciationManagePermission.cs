using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetDesk.Api.Migrations
{
    /// <summary>
    /// Backfills the new iams:depreciation:manage grant onto existing tenants.
    ///
    /// SeedData.EnsureRolePermissionsAsync is gated on Tenant.RolePermissionsSeededAt and never
    /// re-runs, so a key added to Permissions.All today reaches no tenant provisioned before
    /// today. Without this, every existing Admin would silently lack the permission and the new
    /// depreciation policy screen would 403 for everyone.
    ///
    /// Backfilling unconditionally is safe only because the key is brand new: no tenant can have
    /// deliberately revoked something that did not exist, so there is no admin decision to
    /// trample. A future migration for a key that renames or splits an existing one must not
    /// copy this blindly - there, an existing revocation would be real and must be respected.
    /// </summary>
    public partial class GrantDepreciationManagePermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Built-in roles carry a null TenantId and are shared by every tenant, so the grant
            // rows are the cross product of tenants and the built-in roles that should hold this
            // key - not a join on RoleId.
            //
            // The id comes from md5(...)::uuid rather than gen_random_uuid(): that function is
            // only a built-in from PostgreSQL 13, and the database here is supplied externally
            // with no version pinned. Being deterministic also makes the insert idempotent - the
            // NOT EXISTS guard and the id agree with each other on a re-run.
            migrationBuilder.Sql(@"
                INSERT INTO ""RolePermissions"" (""Id"", ""RoleId"", ""TenantId"", ""Permission"")
                SELECT
                    md5(t.""Id""::text || r.""Id"" || 'iams:depreciation:manage')::uuid,
                    r.""Id"",
                    t.""Id"",
                    'iams:depreciation:manage'
                FROM ""Tenants"" t
                CROSS JOIN ""AspNetRoles"" r
                WHERE r.""IsBuiltIn""
                  AND r.""Name"" IN ('Admin', 'SuperAdmin')
                  AND NOT EXISTS (
                      SELECT 1 FROM ""RolePermissions"" x
                      WHERE x.""RoleId"" = r.""Id""
                        AND x.""TenantId"" = t.""Id""
                        AND x.""Permission"" = 'iams:depreciation:manage'
                  );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DELETE FROM ""RolePermissions"" WHERE ""Permission"" = 'iams:depreciation:manage';
            ");
        }
    }
}
