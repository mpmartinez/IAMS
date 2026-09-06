using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetDesk.Api.Migrations
{
    /// <summary>
    /// Backfills the new iams:audit:view grant onto existing tenants.
    ///
    /// SeedData.EnsureRolePermissionsAsync is gated on Tenant.RolePermissionsSeededAt and never
    /// re-runs, so a key added to Permissions.All today reaches no tenant provisioned before
    /// today. Without this, every existing Admin would silently lack the permission and the new
    /// /admin/audit page would 403 for everyone.
    ///
    /// Backfilling unconditionally is safe only because the key is brand new: no tenant can have
    /// deliberately revoked something that did not exist, so there is no admin decision to
    /// trample. A future migration for a key that renames or splits an existing one must not
    /// copy this blindly - there, an existing revocation would be real and must be respected.
    /// </summary>
    public partial class GrantAuditViewPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Built-in roles carry a null TenantId and are shared by every tenant, so the grant
            // rows are the cross product of tenants and the three built-in roles that should
            // hold this key - not a join on RoleId.
            //
            // The id is derived from the row's own identity rather than gen_random_uuid(): that
            // function only exists as a built-in from PostgreSQL 13, and the database here is
            // supplied externally through ConnectionStrings__DefaultConnection with no version
            // pinned anywhere in the repository. md5(...)::uuid works on every version, and
            // being deterministic it makes the insert idempotent - the NOT EXISTS guard and the
            // id agree with each other on a re-run.
            migrationBuilder.Sql(@"
                INSERT INTO ""RolePermissions"" (""Id"", ""RoleId"", ""TenantId"", ""Permission"")
                SELECT
                    md5(t.""Id""::text || r.""Id"" || 'iams:audit:view')::uuid,
                    r.""Id"",
                    t.""Id"",
                    'iams:audit:view'
                FROM ""Tenants"" t
                CROSS JOIN ""AspNetRoles"" r
                WHERE r.""IsBuiltIn""
                  AND r.""Name"" IN ('Admin', 'SuperAdmin', 'Auditor')
                  AND NOT EXISTS (
                      SELECT 1 FROM ""RolePermissions"" x
                      WHERE x.""RoleId"" = r.""Id""
                        AND x.""TenantId"" = t.""Id""
                        AND x.""Permission"" = 'iams:audit:view'
                  );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Removes the key entirely, including from any tenant that granted it to a custom
            // role after this migration ran. That is the correct reversal: rolling back to a
            // schema where Permissions.All has no such key leaves those rows referencing a
            // permission that no policy checks, which PermissionCatalogTests would not catch
            // and an admin would see as a phantom tick in the /admin/roles matrix.
            migrationBuilder.Sql(@"
                DELETE FROM ""RolePermissions"" WHERE ""Permission"" = 'iams:audit:view';
            ");
        }
    }
}
