using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace IAMS.Web.Services;

/// <summary>
/// Authorization message handler configured for IAMS.Api requests.
/// Adds the access token from m2ID to outgoing API requests.
/// </summary>
public class IamsApiAuthorizationMessageHandler : AuthorizationMessageHandler
{
    public IamsApiAuthorizationMessageHandler(
        IAccessTokenProvider provider,
        NavigationManager navigation,
        IConfiguration configuration)
        : base(provider, navigation)
    {
        var apiBaseUrl = configuration["ApiBaseUrl"] ?? "http://localhost:5010";
        ConfigureHandler(authorizedUrls: new[] { apiBaseUrl });
    }
}
