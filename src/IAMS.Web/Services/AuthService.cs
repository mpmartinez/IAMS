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
    private const string UserKey = "currentUser";

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

        await localStorage.SetItemAsync(TokenKey, result.Data.Token);
        await localStorage.SetItemAsync(UserKey, result.Data.User);

        ((AuthStateProvider)authStateProvider).NotifyAuthenticationStateChanged();

        return (true, null);
    }

    public async Task LogoutAsync()
    {
        await localStorage.RemoveItemAsync(TokenKey);
        await localStorage.RemoveItemAsync(UserKey);
        ((AuthStateProvider)authStateProvider).NotifyAuthenticationStateChanged();
    }

    public async Task<string?> GetTokenAsync() => await localStorage.GetItemAsync<string>(TokenKey);

    public async Task<UserDto?> GetCurrentUserAsync() => await localStorage.GetItemAsync<UserDto>(UserKey);

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
}
