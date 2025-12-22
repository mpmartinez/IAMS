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
var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "https://localhost:5001";
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(apiBaseUrl) });

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
