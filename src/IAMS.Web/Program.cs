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

builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<AuthenticationStateProvider, AuthStateProvider>();
builder.Services.AddAuthorizationCore();

builder.Services.AddScoped<ApiClient>();
builder.Services.AddSingleton<AppleDeviceLookupService>();

// Offline support services
builder.Services.AddScoped<OfflineStorageService>();
builder.Services.AddScoped<NetworkStatusService>();
builder.Services.AddScoped<SyncService>();

builder.Services.AddScoped(sp =>
{
    var client = new HttpClient { BaseAddress = new Uri("https://localhost:5001") };
    return client;
});

// Configure logging for sync service
builder.Logging.SetMinimumLevel(LogLevel.Information);

await builder.Build().RunAsync();
