using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace AssetDesk.Api;

/// <summary>
/// Named rate-limit policies for the endpoints where an unthrottled caller is the actual risk:
/// the anonymous credential surface, and the authenticated action that mails a reset link.
///
/// Partitioning is by client IP for the anonymous policies - there is no user to key on before
/// sign-in - and by user id for the admin policy, so one busy administrator cannot exhaust the
/// budget for everyone else behind the same office NAT.
/// </summary>
public static class RateLimitPolicies
{
    public const string Login = "auth-login";
    public const string PasswordReset = "auth-password-reset";
    public const string AdminPasswordReset = "admin-password-reset";

    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            // 429 rather than the default 503: this is the client being asked to slow down, not
            // the server failing. ApiClient surfaces the status straight to the user.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Generous enough that a person fat-fingering their password a few times is
            // unaffected, tight enough that credential stuffing from one address is not free.
            options.AddPolicy(Login, http => FixedWindowByIp(http, permitLimit: 10, windowMinutes: 5));

            // Deliberately tighter. Each request here can send mail, so an unthrottled caller
            // can use this endpoint to flood a third party's inbox.
            options.AddPolicy(PasswordReset, http => FixedWindowByIp(http, permitLimit: 5, windowMinutes: 15));

            // Keyed by the acting administrator. Onboarding a handful of people in one sitting
            // is normal; hundreds in five minutes is not.
            options.AddPolicy(AdminPasswordReset, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? ClientIp(http),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(5)
                }));
        });

    private static RateLimitPartition<string> FixedWindowByIp(HttpContext http, int permitLimit, int windowMinutes) =>
        RateLimitPartition.GetFixedWindowLimiter(
            ClientIp(http),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(windowMinutes)
            });

    /// <summary>
    /// Behind the reverse proxy this app is deployed under, RemoteIpAddress is the proxy for
    /// every caller - which would put the whole internet in one partition. Prefer the first hop
    /// in X-Forwarded-For, matching how AuthController records IPs for token revocation.
    /// </summary>
    private static string ClientIp(HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
            return forwarded.Split(',')[0].Trim();

        return http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
