using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using Blazored.LocalStorage;
using IAMS.Shared.DTOs;
using Microsoft.AspNetCore.Components.Authorization;

namespace IAMS.Web.Services;

public class AuthService(
    HttpClient http,
    ILocalStorageService localStorage,
    AuthenticationStateProvider authStateProvider)
{
    private const string TokenKey = "authToken";
    private const string RefreshTokenKey = "refreshToken";
    private const string UserKey = "currentUser";
    private const string TokenExpiryKey = "tokenExpiry";

    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    public async Task<(bool Success, string? Error)> LoginAsync(string email, string password)
    {
        var response = await http.PostAsJsonAsync("api/auth/login", new LoginDto
        {
            Email = email,
            Password = password
        });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Login failed");
        }

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponseDto>>();
        if (result?.Data is null)
            return (false, "Invalid response");

        await StoreTokensAsync(result.Data);
        ((AuthStateProvider)authStateProvider).NotifyAuthenticationStateChanged();

        return (true, null);
    }

    public async Task LogoutAsync()
    {
        try
        {
            var refreshToken = await localStorage.GetItemAsync<string>(RefreshTokenKey);
            if (!string.IsNullOrEmpty(refreshToken))
            {
                // Call logout endpoint to revoke refresh token
                await http.PostAsJsonAsync("api/auth/logout", new { RefreshToken = refreshToken });
            }
        }
        catch
        {
            // Ignore errors during logout
        }

        await ClearTokensAsync();
        ((AuthStateProvider)authStateProvider).NotifyAuthenticationStateChanged();
    }

    public async Task<string?> GetTokenAsync()
    {
        // Check if token needs refresh
        if (await ShouldRefreshTokenAsync())
        {
            await TryRefreshTokenAsync();
        }

        return await localStorage.GetItemAsync<string>(TokenKey);
    }

    public async Task<string?> GetRefreshTokenAsync() => await localStorage.GetItemAsync<string>(RefreshTokenKey);

    public async Task<UserDto?> GetCurrentUserAsync() => await localStorage.GetItemAsync<UserDto>(UserKey);

    public async Task<bool> IsAuthenticatedAsync()
    {
        var token = await localStorage.GetItemAsync<string>(TokenKey);
        var refreshToken = await localStorage.GetItemAsync<string>(RefreshTokenKey);

        // Has valid access token or refresh token
        return !string.IsNullOrEmpty(token) || !string.IsNullOrEmpty(refreshToken);
    }

    public async Task<bool> TryRefreshTokenAsync()
    {
        // Use lock to prevent multiple simultaneous refresh attempts
        await RefreshLock.WaitAsync();
        try
        {
            var refreshToken = await localStorage.GetItemAsync<string>(RefreshTokenKey);
            if (string.IsNullOrEmpty(refreshToken))
            {
                Console.WriteLine("No refresh token available");
                return false;
            }

            Console.WriteLine("Attempting to refresh access token...");

            var response = await http.PostAsJsonAsync("api/auth/refresh", new { RefreshToken = refreshToken });

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Token refresh failed: {response.StatusCode}");
                // Refresh token is invalid/expired - clear everything
                await ClearTokensAsync();
                ((AuthStateProvider)authStateProvider).NotifyAuthenticationStateChanged();
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<ApiResponse<LoginResponseDto>>();
            if (result?.Data is null)
            {
                Console.WriteLine("Token refresh returned invalid data");
                return false;
            }

            await StoreTokensAsync(result.Data);
            Console.WriteLine("Token refresh successful");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Token refresh exception: {ex.Message}");
            return false;
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    public async Task<bool> ShouldRefreshTokenAsync()
    {
        try
        {
            var token = await localStorage.GetItemAsync<string>(TokenKey);
            if (string.IsNullOrEmpty(token))
                return true; // No token, try refresh

            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(token))
                return true;

            var jwtToken = handler.ReadJwtToken(token);
            var expiry = jwtToken.ValidTo;

            // Refresh if token expires in less than 5 minutes
            var shouldRefresh = expiry < DateTime.UtcNow.AddMinutes(5);
            if (shouldRefresh)
            {
                Console.WriteLine($"Token expires at {expiry:u}, refreshing...");
            }

            return shouldRefresh;
        }
        catch
        {
            return true;
        }
    }

    public async Task<(bool Success, string? Error)> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        var token = await GetTokenAsync();
        if (string.IsNullOrEmpty(token))
            return (false, "Not authenticated");

        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await http.PostAsJsonAsync("api/auth/change-password", new ChangePasswordDto
        {
            CurrentPassword = currentPassword,
            NewPassword = newPassword
        });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to change password");
        }

        return (true, null);
    }

    private async Task StoreTokensAsync(LoginResponseDto data)
    {
        await localStorage.SetItemAsync(TokenKey, data.Token);
        await localStorage.SetItemAsync(RefreshTokenKey, data.RefreshToken);
        await localStorage.SetItemAsync(UserKey, data.User);
        await localStorage.SetItemAsync(TokenExpiryKey, data.ExpiresAt);
    }

    private async Task ClearTokensAsync()
    {
        await localStorage.RemoveItemAsync(TokenKey);
        await localStorage.RemoveItemAsync(RefreshTokenKey);
        await localStorage.RemoveItemAsync(UserKey);
        await localStorage.RemoveItemAsync(TokenExpiryKey);
    }
}
