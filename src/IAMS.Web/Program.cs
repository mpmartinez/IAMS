using Blazored.LocalStorage;
using IAMS.Web;
using IAMS.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddBlazoredLocalStorage();

// Authentication
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<AuthenticationStateProvider, AuthStateProvider>();
builder.Services.AddAuthorizationCore();

// API Client
builder.Services.AddScoped<ApiClient>();
builder.Services.AddSingleton<AppleDeviceLookupService>();

// HTTP Client
var configuredUrl = builder.Configuration["ApiBaseUrl"] ?? "https://localhost:5001";
Uri apiBaseUri;

if (Uri.IsWellFormedUriString(configuredUrl, UriKind.Absolute))
{
    // Absolute URL (development)
    apiBaseUri = new Uri(configuredUrl);
}
else
{
    // Relative URL (production) - combine with browser's base URI
    var baseUri = new Uri(builder.HostEnvironment.BaseAddress);
    apiBaseUri = new Uri(baseUri, configuredUrl.TrimStart('/') + "/");
}

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = apiBaseUri });

// Offline support services
builder.Services.AddScoped<OfflineStorageService>();
builder.Services.AddScoped<NetworkStatusService>();
builder.Services.AddScoped<SyncService>();

// Notification service for SSE
builder.Services.AddScoped<NotificationService>();

// Global snackbar service
builder.Services.AddSingleton<SnackbarService>();

// Configure logging
builder.Logging.SetMinimumLevel(LogLevel.Information);

await builder.Build().RunAsync();
