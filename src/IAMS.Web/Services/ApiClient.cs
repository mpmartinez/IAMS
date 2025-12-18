using System.Net.Http.Headers;
using System.Net.Http.Json;
using IAMS.Shared.DTOs;

namespace IAMS.Web.Services;

public class ApiClient(HttpClient http, AuthService authService)
{
    private async Task<HttpClient> GetAuthenticatedClient()
    {
        var token = await authService.GetTokenAsync();
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public async Task<PagedResponse<AssetDto>?> GetAssetsAsync(
        string? search = null,
        string? category = null,
        string? status = null,
        int page = 1,
        int pageSize = 20)
    {
        var client = await GetAuthenticatedClient();
        var query = $"api/assets?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(search)) query += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrEmpty(category)) query += $"&category={Uri.EscapeDataString(category)}";
        if (!string.IsNullOrEmpty(status)) query += $"&status={Uri.EscapeDataString(status)}";

        return await client.GetFromJsonAsync<PagedResponse<AssetDto>>(query);
    }

    public async Task<AssetDto?> GetAssetAsync(int id)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<AssetDto>>($"api/assets/{id}");
        return response?.Data;
    }

    public async Task<(bool Success, string? Error)> CreateAssetAsync(CreateAssetDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("api/assets", dto);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to create asset");
        }

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateAssetAsync(int id, UpdateAssetDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PutAsJsonAsync($"api/assets/{id}", dto);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to update asset");
        }

        return (true, null);
    }

    public async Task<bool> DeleteAssetAsync(int id)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.DeleteAsync($"api/assets/{id}");
        return response.IsSuccessStatusCode;
    }

    public async Task<string[]?> GetCategoriesAsync()
    {
        return await http.GetFromJsonAsync<string[]>("api/assets/categories");
    }

    public async Task<string[]?> GetStatusesAsync()
    {
        return await http.GetFromJsonAsync<string[]>("api/assets/statuses");
    }

    public async Task<List<UserListItem>?> GetUserListAsync()
    {
        var client = await GetAuthenticatedClient();
        return await client.GetFromJsonAsync<List<UserListItem>>("api/users/list");
    }
}

public record UserListItem(int Id, string FullName, string? Department);
