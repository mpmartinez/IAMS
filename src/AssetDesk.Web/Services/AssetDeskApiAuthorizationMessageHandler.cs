using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace AssetDesk.Web.Services;

/// <summary>
/// Authorization message handler configured for AssetDesk.Api requests.
/// Adds the access token from m2ID to outgoing API requests.
/// </summary>
public class AssetDeskApiAuthorizationMessageHandler : AuthorizationMessageHandler
{
    public AssetDeskApiAuthorizationMessageHandler(
        IAccessTokenProvider provider,
        NavigationManager navigation,
        IConfiguration configuration)
        : base(provider, navigation)
    {
        var apiBaseUrl = configuration["ApiBaseUrl"] ?? "http://localhost:5010";
        ConfigureHandler(authorizedUrls: new[] { apiBaseUrl });
    }
}
