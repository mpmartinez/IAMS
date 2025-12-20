using Blazored.LocalStorage;
using IAMS.Web;
using IAMS.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddBlazoredLocalStorage();

// Check if m2ID integration is configured
var m2idAuthority = builder.Configuration["m2ID:Authority"];
var useM2ID = !string.IsNullOrEmpty(m2idAuthority);

if (useM2ID)
{
    // Use m2ID OIDC authentication with custom claims factory for role mapping
    builder.Services.AddOidcAuthentication<RemoteAuthenticationState, RemoteUserAccount>(options =>
    {
        options.ProviderOptions.Authority = m2idAuthority;
        options.ProviderOptions.ClientId = builder.Configuration["m2ID:ClientId"] ?? "m2id_iams_asset_management";
        options.ProviderOptions.ResponseType = builder.Configuration["m2ID:ResponseType"] ?? "code";
        options.ProviderOptions.PostLogoutRedirectUri = builder.Configuration["m2ID:PostLogoutRedirectUri"] ?? "/";

        // Add default scopes
        var scopes = builder.Configuration.GetSection("m2ID:DefaultScopes").Get<string[]>() ?? ["openid", "profile", "email"];
        foreach (var scope in scopes)
        {
            options.ProviderOptions.DefaultScopes.Add(scope);
        }

        // Map claims
        options.UserOptions.RoleClaim = "role";
        options.UserOptions.NameClaim = "name";
    }).AddAccountClaimsPrincipalFactory<RemoteAuthenticationState, RemoteUserAccount, M2IDAccountClaimsPrincipalFactory>();

    // Configure HttpClient for IAMS.Api (token is added manually by ApiClient)
    var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5010";
    builder.Services.AddScoped(sp =>
    {
        var client = new HttpClient { BaseAddress = new Uri(apiBaseUrl) };
        return client;
    });

    // Register m2ID token provider
    builder.Services.AddScoped<ITokenProvider, M2IDTokenProvider>();
}
else
{
    // Use legacy local authentication
    builder.Services.AddScoped<AuthService>();
    builder.Services.AddScoped<AuthenticationStateProvider, AuthStateProvider>();

    // Register legacy token provider
    builder.Services.AddScoped<ITokenProvider, LegacyTokenProvider>();

    builder.Services.AddScoped(sp =>
    {
        var client = new HttpClient { BaseAddress = new Uri("https://localhost:5011") };
        return client;
    });
}

builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<ApiClient>();
builder.Services.AddSingleton<AppleDeviceLookupService>();

// Offline support services
builder.Services.AddScoped<OfflineStorageService>();
builder.Services.AddScoped<NetworkStatusService>();
builder.Services.AddScoped<SyncService>();

// Notification service for SSE
builder.Services.AddScoped<NotificationService>();

// Global snackbar service
builder.Services.AddSingleton<SnackbarService>();

// Configure logging for sync service
builder.Logging.SetMinimumLevel(LogLevel.Information);

await builder.Build().RunAsync();
