namespace IAMS.Web.Services;

/// <summary>
/// Abstraction for getting access tokens for API calls.
/// Works with both local JWT auth and m2ID OIDC.
/// </summary>
public interface ITokenProvider
{
    Task<string?> GetAccessTokenAsync();
}
