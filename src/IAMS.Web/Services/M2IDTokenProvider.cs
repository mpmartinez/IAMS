using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace IAMS.Web.Services;

/// <summary>
/// Token provider for m2ID OIDC authentication.
/// </summary>
public class M2IDTokenProvider(IAccessTokenProvider tokenProvider) : ITokenProvider
{
    public async Task<string?> GetAccessTokenAsync()
    {
        var tokenResult = await tokenProvider.RequestAccessToken();

        if (tokenResult.TryGetToken(out var token))
        {
            return token.Value;
        }

        return null;
    }
}
