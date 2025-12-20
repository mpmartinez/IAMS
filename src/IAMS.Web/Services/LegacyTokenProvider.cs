namespace IAMS.Web.Services;

/// <summary>
/// Token provider for legacy local JWT authentication.
/// </summary>
public class LegacyTokenProvider(AuthService authService) : ITokenProvider
{
    public async Task<string?> GetAccessTokenAsync()
    {
        return await authService.GetTokenAsync();
    }
}
