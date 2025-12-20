using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace IAMS.Web.Services;

/// <summary>
/// Authentication state provider for m2ID OIDC integration.
/// Maps m2ID roles to IAMS roles (Administrator -> Admin).
/// </summary>
public class M2IDAuthStateProvider : AuthenticationStateProvider
{
    private readonly IAccessTokenProvider _tokenProvider;
    private readonly ILogger<M2IDAuthStateProvider> _logger;

    public M2IDAuthStateProvider(
        IAccessTokenProvider tokenProvider,
        ILogger<M2IDAuthStateProvider> logger)
    {
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var tokenResult = await _tokenProvider.RequestAccessToken();

            if (tokenResult.TryGetToken(out var token))
            {
                var claims = ParseClaimsFromJwt(token.Value);
                var mappedClaims = MapRoles(claims);
                var identity = new ClaimsIdentity(mappedClaims, "m2ID");
                var user = new ClaimsPrincipal(identity);

                _logger.LogInformation("User authenticated via m2ID: {Email}",
                    user.FindFirst(ClaimTypes.Email)?.Value ?? user.FindFirst("email")?.Value);

                return new AuthenticationState(user);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No active authentication session");
        }

        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
    }

    private static IEnumerable<Claim> ParseClaimsFromJwt(string jwt)
    {
        var claims = new List<Claim>();

        try
        {
            var payload = jwt.Split('.')[1];
            var jsonBytes = ParseBase64WithoutPadding(payload);
            var keyValuePairs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(jsonBytes);

            if (keyValuePairs == null)
                return claims;

            foreach (var kvp in keyValuePairs)
            {
                if (kvp.Value is System.Text.Json.JsonElement element)
                {
                    if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in element.EnumerateArray())
                        {
                            claims.Add(new Claim(kvp.Key, item.GetString() ?? ""));
                        }
                    }
                    else
                    {
                        claims.Add(new Claim(kvp.Key, element.ToString()));
                    }
                }
                else
                {
                    claims.Add(new Claim(kvp.Key, kvp.Value?.ToString() ?? ""));
                }
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return claims;
    }

    private static byte[] ParseBase64WithoutPadding(string base64)
    {
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Convert.FromBase64String(base64.Replace('-', '+').Replace('_', '/'));
    }

    /// <summary>
    /// Maps m2ID roles to IAMS-compatible roles.
    /// m2ID "Administrator" role is mapped to IAMS "Admin" role.
    /// </summary>
    private static IEnumerable<Claim> MapRoles(IEnumerable<Claim> claims)
    {
        var claimsList = claims.ToList();
        var mappedClaims = new List<Claim>();

        foreach (var claim in claimsList)
        {
            // Map role claims
            if (claim.Type == "role" || claim.Type == ClaimTypes.Role)
            {
                var roleValue = claim.Value;

                // Map m2ID roles to IAMS roles
                if (roleValue == "Administrator")
                {
                    mappedClaims.Add(new Claim(ClaimTypes.Role, "Admin"));
                }
                else
                {
                    mappedClaims.Add(new Claim(ClaimTypes.Role, roleValue));
                }
            }
            else
            {
                mappedClaims.Add(claim);
            }
        }

        return mappedClaims;
    }

    public void NotifyAuthenticationStateChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
