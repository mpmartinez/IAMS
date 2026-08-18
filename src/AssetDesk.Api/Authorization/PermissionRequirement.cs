using AssetDesk.Api.Entities;
using Microsoft.AspNetCore.Authorization;

namespace AssetDesk.Api.Authorization;

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        // SuperAdmin bypasses permission checks by design, consistent with it short-circuiting
        // tenant isolation elsewhere: gating it on per-tenant grants would be incoherent.
        if (context.User.IsInRole(Roles.SuperAdmin) ||
            context.User.HasClaim(Permissions.ClaimType, requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class AuthorizationBuilderExtensions
{
    public static AuthorizationBuilder RequirePermission(
        this AuthorizationBuilder builder, string policyName, string permission) =>
        builder.AddPolicy(policyName, policy =>
            policy.AddRequirements(new PermissionRequirement(permission)));
}
