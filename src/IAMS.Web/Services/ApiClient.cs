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
        string? deviceType = null,
        string? status = null,
        string? assignedToUserId = null,
        int page = 1,
        int pageSize = 20)
    {
        var client = await GetAuthenticatedClient();
        var query = $"api/assets?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(search)) query += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrEmpty(deviceType)) query += $"&deviceType={Uri.EscapeDataString(deviceType)}";
        if (!string.IsNullOrEmpty(status)) query += $"&status={Uri.EscapeDataString(status)}";
        if (!string.IsNullOrEmpty(assignedToUserId)) query += $"&assignedToUserId={Uri.EscapeDataString(assignedToUserId)}";

        return await client.GetFromJsonAsync<PagedResponse<AssetDto>>(query);
    }

    public async Task<AssetDto?> GetAssetAsync(int id)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<AssetDto>>($"api/assets/{id}");
        return response?.Data;
    }

    public async Task<AssetDto?> GetAssetByTagAsync(string assetTag)
    {
        var client = await GetAuthenticatedClient();
        try
        {
            var response = await client.GetFromJsonAsync<ApiResponse<AssetDto>>($"api/assets/scan/{Uri.EscapeDataString(assetTag)}");
            return response?.Data;
        }
        catch
        {
            return null;
        }
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

    public async Task<string[]?> GetDeviceTypesAsync()
    {
        return await http.GetFromJsonAsync<string[]>("api/assets/device-types");
    }

    public async Task<string[]?> GetStatusesAsync()
    {
        return await http.GetFromJsonAsync<string[]>("api/assets/statuses");
    }

    public async Task<string[]?> GetCurrenciesAsync()
    {
        return await http.GetFromJsonAsync<string[]>("api/assets/currencies");
    }

    public async Task<List<UserListItem>?> GetUserListAsync()
    {
        var client = await GetAuthenticatedClient();
        return await client.GetFromJsonAsync<List<UserListItem>>("api/users/list");
    }

    // Assignment APIs
    public async Task<(bool Success, string? Error)> AssignAssetAsync(int assetId, string userId, string? notes = null)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync($"api/assignments/assets/{assetId}/assign", new { UserId = userId, Notes = notes });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to assign asset");
        }

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> ReturnAssetAsync(int assetId, string? notes = null, string? returnCondition = null)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync($"api/assignments/assets/{assetId}/return", new { Notes = notes, ReturnCondition = returnCondition });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to return asset");
        }

        return (true, null);
    }

    public async Task<List<AssetAssignmentDto>?> GetAssetHistoryAsync(int assetId)
    {
        var client = await GetAuthenticatedClient();
        return await client.GetFromJsonAsync<List<AssetAssignmentDto>>($"api/assignments/assets/{assetId}/history");
    }

    public async Task<UserAssetsDto?> GetUserAssetsAsync(string userId)
    {
        var client = await GetAuthenticatedClient();
        return await client.GetFromJsonAsync<UserAssetsDto>($"api/assignments/users/{userId}/assets");
    }

    public async Task<OffboardingDto?> GetOffboardingSummaryAsync(string userId)
    {
        var client = await GetAuthenticatedClient();
        return await client.GetFromJsonAsync<OffboardingDto>($"api/assignments/users/{userId}/offboarding");
    }

    public async Task<(bool Success, int Returned, string? Error)> BulkReturnAssetsAsync(string userId, List<int> assetIds, string? notes = null, string returnCondition = "Good")
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync($"api/assignments/users/{userId}/offboarding/return", new { AssetIds = assetIds, Notes = notes, ReturnCondition = returnCondition });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, 0, error?.Message ?? "Failed to return assets");
        }

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<BulkReturnResult>>();
        return (true, result?.Data?.TotalReturned ?? 0, null);
    }

    public async Task<List<OffboardingSummaryItem>?> GetPendingOffboardingsAsync()
    {
        var client = await GetAuthenticatedClient();
        return await client.GetFromJsonAsync<List<OffboardingSummaryItem>>("api/assignments/offboarding/pending");
    }

    public async Task<PagedResponse<AssetAssignmentDto>?> GetAssignmentAuditLogAsync(int page = 1, int pageSize = 20, string? assetTag = null, string? userId = null)
    {
        var client = await GetAuthenticatedClient();
        var query = $"api/assignments/audit?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(assetTag)) query += $"&assetTag={Uri.EscapeDataString(assetTag)}";
        if (!string.IsNullOrEmpty(userId)) query += $"&userId={Uri.EscapeDataString(userId)}";
        return await client.GetFromJsonAsync<PagedResponse<AssetAssignmentDto>>(query);
    }

    // Warranty Alert APIs
    public async Task<PagedResponse<WarrantyAlertDto>?> GetWarrantyAlertsAsync(
        string? alertType = null,
        bool? acknowledged = null,
        int page = 1,
        int pageSize = 20)
    {
        var client = await GetAuthenticatedClient();
        var query = $"api/warrantyalerts?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(alertType)) query += $"&alertType={Uri.EscapeDataString(alertType)}";
        if (acknowledged.HasValue) query += $"&acknowledged={acknowledged.Value}";
        return await client.GetFromJsonAsync<PagedResponse<WarrantyAlertDto>>(query);
    }

    public async Task<WarrantyAlertSummaryDto?> GetWarrantyAlertSummaryAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<WarrantyAlertSummaryDto>>("api/warrantyalerts/summary");
        return response?.Data;
    }

    public async Task<int> GetUnacknowledgedWarrantyAlertCountAsync()
    {
        var client = await GetAuthenticatedClient();
        return await client.GetFromJsonAsync<int>("api/warrantyalerts/count");
    }

    public async Task<bool> AcknowledgeWarrantyAlertAsync(int alertId)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsync($"api/warrantyalerts/{alertId}/acknowledge", null);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> AcknowledgeWarrantyAlertsBulkAsync(List<int> alertIds)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("api/warrantyalerts/acknowledge-bulk", alertIds);
        return response.IsSuccessStatusCode;
    }

    // Dashboard API
    public async Task<DashboardDto?> GetDashboardAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<DashboardDto>>("api/dashboard");
        return response?.Data;
    }

    // Status update for offline sync
    public async Task<bool> UpdateAssetStatusAsync(int assetId, string status)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PatchAsJsonAsync($"api/assets/{assetId}/status", new { Status = status });
        return response.IsSuccessStatusCode;
    }

    // Report APIs
    public async Task<List<AssetInventoryReportRow>?> GetInventoryReportAsync(string? deviceType = null, string? status = null)
    {
        var client = await GetAuthenticatedClient();
        var query = "api/reports/inventory";
        var queryParams = new List<string>();
        if (!string.IsNullOrEmpty(deviceType)) queryParams.Add($"deviceType={Uri.EscapeDataString(deviceType)}");
        if (!string.IsNullOrEmpty(status)) queryParams.Add($"status={Uri.EscapeDataString(status)}");
        if (queryParams.Count > 0) query += "?" + string.Join("&", queryParams);

        var response = await client.GetFromJsonAsync<ApiResponse<List<AssetInventoryReportRow>>>(query);
        return response?.Data;
    }

    public async Task<List<AssignedAssetsByUserReportRow>?> GetAssignedByUserReportAsync(string? userId = null)
    {
        var client = await GetAuthenticatedClient();
        var query = "api/reports/assigned-by-user";
        if (!string.IsNullOrEmpty(userId)) query += $"?userId={Uri.EscapeDataString(userId)}";

        var response = await client.GetFromJsonAsync<ApiResponse<List<AssignedAssetsByUserReportRow>>>(query);
        return response?.Data;
    }

    public async Task<List<WarrantyExpiryReportRow>?> GetWarrantyExpiryReportAsync(string? warrantyStatus = null, int? daysThreshold = null)
    {
        var client = await GetAuthenticatedClient();
        var query = "api/reports/warranty-expiry";
        var queryParams = new List<string>();
        if (!string.IsNullOrEmpty(warrantyStatus)) queryParams.Add($"warrantyStatus={Uri.EscapeDataString(warrantyStatus)}");
        if (daysThreshold.HasValue) queryParams.Add($"daysThreshold={daysThreshold.Value}");
        if (queryParams.Count > 0) query += "?" + string.Join("&", queryParams);

        var response = await client.GetFromJsonAsync<ApiResponse<List<WarrantyExpiryReportRow>>>(query);
        return response?.Data;
    }

    public async Task<AssetValueSummaryDto?> GetAssetValueReportAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<AssetValueSummaryDto>>("api/reports/asset-value");
        return response?.Data;
    }

    public string GetReportExportUrl(string reportType, Dictionary<string, string>? filters = null)
    {
        var baseUrl = $"api/reports/{reportType}/export";
        if (filters != null && filters.Count > 0)
        {
            var queryParams = filters.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}");
            baseUrl += "?" + string.Join("&", queryParams);
        }
        return baseUrl;
    }

    // QR Code API
    public async Task<string?> GetAssetQrCodeBase64Async(int assetId, int size = 15, bool tagOnly = true)
    {
        var client = await GetAuthenticatedClient();
        try
        {
            var contentType = tagOnly ? "tag" : "url";
            var bytes = await client.GetByteArrayAsync($"api/assets/{assetId}/qr.png?size={size}&contentType={contentType}");
            return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }

    // Attachment APIs
    public async Task<List<AttachmentDto>?> GetAttachmentsAsync(int assetId)
    {
        var client = await GetAuthenticatedClient();
        return await client.GetFromJsonAsync<List<AttachmentDto>>($"api/assets/{assetId}/attachments");
    }

    public async Task<AttachmentSummaryDto?> GetAttachmentSummaryAsync(int assetId)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<AttachmentSummaryDto>>(
            $"api/assets/{assetId}/attachments/summary");
        return response?.Data;
    }

    public async Task<(bool Success, AttachmentDto? Attachment, string? Error)> UploadAttachmentAsync(
        int assetId,
        Stream fileStream,
        string fileName,
        string contentType,
        string category,
        string? description = null)
    {
        var client = await GetAuthenticatedClient();

        // Buffer the stream to handle mobile browser stream issues
        using var memoryStream = new MemoryStream();
        await fileStream.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(memoryStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        content.Add(fileContent, "file", fileName);
        content.Add(new StringContent(category), "category");
        if (!string.IsNullOrEmpty(description))
            content.Add(new StringContent(description), "description");

        var response = await client.PostAsync($"api/assets/{assetId}/attachments", content);

        if (!response.IsSuccessStatusCode)
        {
            // Read as string first to handle HTML error pages (common on mobile)
            var errorContent = await response.Content.ReadAsStringAsync();

            // Check if the response is JSON (starts with {)
            if (!string.IsNullOrEmpty(errorContent) && errorContent.TrimStart().StartsWith('{'))
            {
                try
                {
                    var error = System.Text.Json.JsonSerializer.Deserialize<ApiResponse<object>>(errorContent);
                    return (false, null, error?.Message ?? "Failed to upload attachment");
                }
                catch
                {
                    // JSON parsing failed, fall through to status code handling
                }
            }

            // Return meaningful error based on status code
            var statusError = response.StatusCode switch
            {
                System.Net.HttpStatusCode.RequestEntityTooLarge => "File size exceeds the server limit",
                System.Net.HttpStatusCode.Unauthorized => "Session expired. Please log in again",
                System.Net.HttpStatusCode.Forbidden => "You don't have permission to upload files",
                System.Net.HttpStatusCode.NotFound => "Asset not found",
                System.Net.HttpStatusCode.BadRequest => "Invalid file or request",
                _ => $"Upload failed (Error {(int)response.StatusCode})"
            };
            return (false, null, statusError);
        }

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AttachmentDto>>();
        return (true, result?.Data, null);
    }

    public async Task<bool> DeleteAttachmentAsync(int assetId, int attachmentId)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.DeleteAsync($"api/assets/{assetId}/attachments/{attachmentId}");
        return response.IsSuccessStatusCode;
    }

    public string GetAttachmentDownloadUrl(int assetId, int attachmentId)
    {
        return $"{http.BaseAddress}api/assets/{assetId}/attachments/{attachmentId}/download";
    }

    public async Task<string?> GetAttachmentBase64Async(int assetId, int attachmentId, string contentType)
    {
        try
        {
            var client = await GetAuthenticatedClient();
            var response = await client.GetAsync($"api/assets/{assetId}/attachments/{attachmentId}/download");
            if (!response.IsSuccessStatusCode) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync();
            return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }

    public async Task<string[]?> GetAttachmentCategoriesAsync()
    {
        return await http.GetFromJsonAsync<string[]>("api/attachments/categories");
    }

    // Notification APIs
    public async Task<List<NotificationDto>?> GetNotificationsAsync(int take = 20)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<List<NotificationDto>>>($"api/notifications?take={take}");
        return response?.Data;
    }

    public async Task<NotificationCountDto?> GetNotificationCountAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<NotificationCountDto>>("api/notifications/count");
        return response?.Data;
    }

    public async Task<bool> MarkNotificationAsReadAsync(int notificationId)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsync($"api/notifications/{notificationId}/read", null);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> MarkAllNotificationsAsReadAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsync("api/notifications/read-all", null);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteNotificationAsync(int notificationId)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.DeleteAsync($"api/notifications/{notificationId}");
        return response.IsSuccessStatusCode;
    }

    public string GetBaseUrl() => http.BaseAddress?.ToString().TrimEnd('/') ?? "";

    // User Management APIs
    public async Task<PagedResponse<UserDto>?> GetUsersAsync(string? search = null, int page = 1, int pageSize = 20)
    {
        var client = await GetAuthenticatedClient();
        var query = $"api/users?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(search)) query += $"&search={Uri.EscapeDataString(search)}";
        return await client.GetFromJsonAsync<PagedResponse<UserDto>>(query);
    }

    public async Task<UserDto?> GetUserAsync(string id)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<UserDto>>($"api/users/{id}");
        return response?.Data;
    }

    public async Task<(bool Success, string? Error)> CreateUserAsync(CreateUserDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("api/users", dto);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to create user");
        }

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateUserAsync(string id, UpdateUserDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PutAsJsonAsync($"api/users/{id}", dto);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to update user");
        }

        return (true, null);
    }

    public async Task<bool> DeleteUserAsync(string id)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.DeleteAsync($"api/users/{id}");
        return response.IsSuccessStatusCode;
    }
}

public record UserListItem(string Id, string FullName, string? Department);

public record OffboardingSummaryItem
{
    public string UserId { get; init; } = "";
    public string UserName { get; init; } = "";
    public string? Department { get; init; }
    public int UnreturnedCount { get; init; }
    public decimal TotalValue { get; init; }
    public DateTime OldestAssignment { get; init; }
}
