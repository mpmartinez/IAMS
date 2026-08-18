using Microsoft.AspNetCore.Components.Authorization;

namespace AssetDesk.Web.Services;

/// <summary>
/// Single source of truth for "does the current user hold this permission claim". Used by
/// <c>PermissionView</c> to decide what to render, and by pages that need to skip their own
/// <c>OnInitializedAsync</c> data loads when they already know a denied <c>PermissionView</c> is
/// about to hide all their content anyway - Blazor runs a component's lifecycle methods
/// regardless of what any wrapping <c>PermissionView</c> decides to render, so a page that gates
/// itself only via markup still fires its API calls (and their 403s) even when the user can't see
/// the result.
/// </summary>
public class PermissionChecker(AuthenticationStateProvider authenticationStateProvider)
{
    public async Task<bool> HasPermissionAsync(string permission)
    {
        var authState = await authenticationStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity?.IsAuthenticated != true)
            return false;

        // SuperAdmin bypasses authorisation on the API, so it must bypass here too or the UI
        // would skip loads / hide controls that would in fact work.
        return user.IsInRole("SuperAdmin") || user.HasClaim("permission", permission);
    }
}
