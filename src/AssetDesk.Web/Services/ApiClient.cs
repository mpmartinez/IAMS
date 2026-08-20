using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AssetDesk.Shared.DTOs;

namespace AssetDesk.Web.Services;

public class ApiClient(HttpClient http, AuthService authService)
{
    public event Func<Task>? OnAuthenticationFailed;

    // The API serializes with MVC's JsonSerializerDefaults.Web (camelCase property names, e.g.
    // "message"). JsonSerializerOptions.Default is case-SENSITIVE, so a bare
    // JsonSerializer.Deserialize<ApiResponse<object>> call never binds "message" to
    // ApiResponse.Message - every server-supplied error message is silently lost. Use this
    // options instance (hoisted rather than allocated per call) for every hand-rolled error-body
    // parse below; response.Content.ReadFromJsonAsync applies JsonSerializerDefaults.Web itself
    // and does not need it.
    private static readonly JsonSerializerOptions ErrorBodyJsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<HttpClient> GetAuthenticatedClient()
    {
        var token = await authService.GetTokenAsync();
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private async Task<T?> SafeGetAsync<T>(string url)
    {
        try
        {
            var client = await GetAuthenticatedClient();
            var response = await client.GetAsync(url);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Console.WriteLine($"401 Unauthorized for {url}");
                OnAuthenticationFailed?.Invoke();
                return default;
            }

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<T>();
            }

            return default;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"API error for {url}: {ex.Message}");
            return default;
        }
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
            Console.WriteLine($"Looking up asset by tag: '{assetTag}'");
            var httpResponse = await client.GetAsync($"api/assets/scan/{Uri.EscapeDataString(assetTag)}");

            Console.WriteLine($"Scan API response status: {httpResponse.StatusCode}");

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorContent = await httpResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"Scan API error: {errorContent}");
                return null;
            }

            var response = await httpResponse.Content.ReadFromJsonAsync<ApiResponse<AssetDto>>();
            return response?.Data;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GetAssetByTagAsync exception: {ex.Message}");
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

    public async Task<(ImportAssetsResultDto? Result, string? Error)> ImportAssetsAsync(Stream fileStream, string fileName)
    {
        var client = await GetAuthenticatedClient();

        using var memoryStream = new MemoryStream();
        await fileStream.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(memoryStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(fileContent, "file", fileName);

        var response = await client.PostAsync("api/assets/import", content);
        var payload = await response.Content.ReadFromJsonAsync<ApiResponse<ImportAssetsResultDto>>();

        if (!response.IsSuccessStatusCode)
            return (payload?.Data, payload?.Message ?? $"Import failed (Error {(int)response.StatusCode})");

        return (payload?.Data, null);
    }

    // Per-session cache for the small "give me the dropdown options" endpoints backed by
    // admin-editable lookup data. ApiClient is registered Scoped, and Blazor WASM has one
    // root scope for the whole browser session, so this dictionary lives exactly as long as
    // the tab does - populated once per lookup type, not refetched on every page visit or
    // keystroke. AssetsController.GetStatuses() is deliberately not cached through here: it is
    // the locked AssetStatus vocabulary, a short constant list, not lookup data.
    private readonly Dictionary<string, string[]> _lookupCache = new();
    private readonly SemaphoreSlim _lookupCacheLock = new(1, 1);

    private async Task<string[]?> GetCachedLookupAsync(string cacheKey, Func<Task<string[]?>> fetch)
    {
        if (_lookupCache.TryGetValue(cacheKey, out var cached))
            return cached;

        await _lookupCacheLock.WaitAsync();
        try
        {
            if (_lookupCache.TryGetValue(cacheKey, out cached))
                return cached;

            var result = await fetch();
            if (result is not null)
                _lookupCache[cacheKey] = result;
            return result;
        }
        finally
        {
            _lookupCacheLock.Release();
        }
    }

    public Task<string[]?> GetDeviceTypesAsync() =>
        GetCachedLookupAsync("device-types", () => http.GetFromJsonAsync<string[]>("api/assets/device-types"));

    public async Task<string[]?> GetStatusesAsync()
    {
        return await http.GetFromJsonAsync<string[]>("api/assets/statuses");
    }

    public Task<string[]?> GetCurrenciesAsync() =>
        GetCachedLookupAsync("currencies", () => http.GetFromJsonAsync<string[]>("api/assets/currencies"));

    public Task<string[]?> GetTicketCategoriesAsync() =>
        GetCachedLookupAsync("ticket-categories", async () =>
        {
            var values = await GetLookupValuesAsync(LookupTypeNames.TicketCategory);
            return values?.Select(v => v.Value).ToArray();
        });

    public async Task<List<UserListItem>?> GetUserListAsync()
    {
        var client = await GetAuthenticatedClient();
        return await client.GetFromJsonAsync<List<UserListItem>>("api/users/list");
    }

    // Reads the profile from the server rather than the copy AuthService cached at login, so a
    // role or department an administrator changed since then shows correctly on /profile.
    public async Task<UserDto?> GetMyProfileAsync()
    {
        var response = await SafeGetAsync<ApiResponse<UserDto>>("api/auth/me");
        return response?.Data;
    }

    public async Task<(bool Success, UserDto? User, string? Error)> UpdateProfileAsync(UpdateProfileDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PutAsJsonAsync("api/auth/me", dto);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, null, error?.Message ?? "Failed to update profile");
        }

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<UserDto>>();
        return (true, result?.Data, null);
    }

    public async Task<(bool Success, string? Error)> LogoutAllAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("api/auth/logout-all", new { });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to sign out other sessions");
        }

        return (true, null);
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
        return await SafeGetAsync<int>("api/warrantyalerts/count");
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

    /// <summary>
    /// The self-service dashboard, for users without iams:assets:view. The estate endpoint above
    /// returns 403 for them, so callers must pick one or the other on the permission - see
    /// Index.razor.
    /// </summary>
    public async Task<MyDashboardDto?> GetMyDashboardAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<MyDashboardDto>>("api/dashboard/me");
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
        => BuildReportUrl(reportType, "export", filters);

    public string GetReportPdfUrl(string reportType, Dictionary<string, string>? filters = null)
        => BuildReportUrl(reportType, "pdf", filters);

    private static string BuildReportUrl(string reportType, string segment, Dictionary<string, string>? filters)
    {
        var url = $"api/reports/{reportType}/{segment}";
        if (filters != null && filters.Count > 0)
        {
            var queryParams = filters.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}");
            url += "?" + string.Join("&", queryParams);
        }
        return url;
    }

    // Ticket APIs
    public async Task<PagedResponse<TicketListItemDto>?> GetTicketsAsync(
        string? type = null,
        string? category = null,
        string? status = null,
        string? priority = null,
        string? assignedToUserId = null,
        int? assetId = null,
        string? search = null,
        int page = 1,
        int pageSize = 25)
    {
        var client = await GetAuthenticatedClient();
        var query = $"api/tickets?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(type)) query += $"&type={Uri.EscapeDataString(type)}";
        if (!string.IsNullOrEmpty(category)) query += $"&category={Uri.EscapeDataString(category)}";
        if (!string.IsNullOrEmpty(status)) query += $"&status={Uri.EscapeDataString(status)}";
        if (!string.IsNullOrEmpty(priority)) query += $"&priority={Uri.EscapeDataString(priority)}";
        if (!string.IsNullOrEmpty(assignedToUserId)) query += $"&assignedToUserId={Uri.EscapeDataString(assignedToUserId)}";
        if (assetId.HasValue) query += $"&assetId={assetId.Value}";
        if (!string.IsNullOrEmpty(search)) query += $"&search={Uri.EscapeDataString(search)}";

        var response = await client.GetFromJsonAsync<ApiResponse<PagedResponse<TicketListItemDto>>>(query);
        return response?.Data;
    }

    public async Task<TicketDto?> GetTicketAsync(int id)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<TicketDto>>($"api/tickets/{id}");
        return response?.Data;
    }

    public async Task<TicketSummaryDto?> GetTicketSummaryAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<TicketSummaryDto>>("api/tickets/summary");
        return response?.Data;
    }

    public async Task<List<TicketListItemDto>?> GetMyTicketsAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<List<TicketListItemDto>>>("api/tickets/mine");
        return response?.Data;
    }

    public async Task<(bool Success, TicketDto? Ticket, string? Error)> CreateTicketAsync(CreateTicketRequest request)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("api/tickets", request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, null, error?.Message ?? "Failed to create ticket");
        }

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<TicketDto>>();
        return (true, result?.Data, null);
    }

    public async Task<(bool Success, string? Error)> AssignTicketAsync(int id, string assignedToUserId)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync($"api/tickets/{id}/assign", new AssignTicketRequest { AssignedToUserId = assignedToUserId });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to assign ticket");
        }

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> ChangeTicketStatusAsync(int id, string status)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync($"api/tickets/{id}/status", new ChangeTicketStatusRequest { Status = status });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to update ticket status");
        }

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> ResolveTicketAsync(int id, string resolution)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync($"api/tickets/{id}/resolve", new ResolveTicketRequest { Resolution = resolution });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to resolve ticket");
        }

        return (true, null);
    }

    public async Task<List<TicketCommentDto>?> GetTicketCommentsAsync(int ticketId)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<List<TicketCommentDto>>>($"api/tickets/{ticketId}/comments");
        return response?.Data;
    }

    public async Task<(bool Success, TicketCommentDto? Comment, string? Error)> AddTicketCommentAsync(int ticketId, string body, bool isInternal)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync($"api/tickets/{ticketId}/comments", new AddTicketCommentRequest { Body = body, IsInternal = isInternal });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, null, error?.Message ?? "Failed to add comment");
        }

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<TicketCommentDto>>();
        return (true, result?.Data, null);
    }

    public async Task<(bool Success, TicketAttachmentDto? Attachment, string? Error)> UploadTicketAttachmentAsync(
        int ticketId,
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

        var response = await client.PostAsync($"api/tickets/{ticketId}/attachments", content);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();

            if (!string.IsNullOrEmpty(errorContent) && errorContent.TrimStart().StartsWith('{'))
            {
                try
                {
                    var error = JsonSerializer.Deserialize<ApiResponse<object>>(errorContent, ErrorBodyJsonOptions);
                    return (false, null, error?.Message ?? "Failed to upload attachment");
                }
                catch
                {
                    // JSON parsing failed, fall through to status code handling
                }
            }

            var statusError = response.StatusCode switch
            {
                System.Net.HttpStatusCode.RequestEntityTooLarge => "File size exceeds the server limit",
                System.Net.HttpStatusCode.Unauthorized => "Session expired. Please log in again",
                System.Net.HttpStatusCode.Forbidden => "You don't have permission to upload files",
                System.Net.HttpStatusCode.NotFound => "Ticket not found",
                System.Net.HttpStatusCode.BadRequest => "Invalid file or request",
                _ => $"Upload failed (Error {(int)response.StatusCode})"
            };
            return (false, null, statusError);
        }

        var uploadResult = await response.Content.ReadFromJsonAsync<ApiResponse<TicketAttachmentDto>>();
        return (true, uploadResult?.Data, null);
    }

    // QR Code API
    public async Task<string?> GetAssetQrCodeBase64Async(int assetId, int size = 15, bool tagOnly = true)
    {
        var client = await GetAuthenticatedClient();
        try
        {
            var contentType = tagOnly ? "tag" : "url";
            var response = await client.GetAsync($"api/assets/{assetId}/qr.png?size={size}&contentType={contentType}");

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"QR Code API error: {response.StatusCode}");
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"QR Code error details: {errorContent}");
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
            {
                Console.WriteLine("QR Code API returned empty bytes");
                return null;
            }

            return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"QR Code generation exception: {ex.Message}");
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
                    var error = JsonSerializer.Deserialize<ApiResponse<object>>(errorContent, ErrorBodyJsonOptions);
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

    // Tenant Management APIs (SuperAdmin only)
    public async Task<PagedResponse<TenantDto>?> GetTenantsAsync(
        string? search = null,
        bool? isActive = null,
        int page = 1,
        int pageSize = 20)
    {
        var client = await GetAuthenticatedClient();
        var query = $"api/tenants?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(search)) query += $"&search={Uri.EscapeDataString(search)}";
        if (isActive.HasValue) query += $"&isActive={isActive.Value}";

        var response = await client.GetFromJsonAsync<ApiResponse<PagedResponse<TenantDto>>>(query);
        return response?.Data;
    }

    public async Task<TenantDto?> GetTenantAsync(Guid id)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<TenantDto>>($"api/tenants/{id}");
        return response?.Data;
    }

    public async Task<TenantUsageDto?> GetTenantUsageAsync(Guid id)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<TenantUsageDto>>($"api/tenants/{id}/usage");
        return response?.Data;
    }

    public async Task<(bool Success, TenantDto? Tenant, string? Error)> CreateTenantAsync(CreateTenantDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("api/tenants", dto);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, null, error?.Message ?? "Failed to create tenant");
        }

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<TenantDto>>();
        return (true, result?.Data, null);
    }

    public async Task<(bool Success, string? Error)> UpdateTenantAsync(Guid id, UpdateTenantDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PutAsJsonAsync($"api/tenants/{id}", dto);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to update tenant");
        }

        return (true, null);
    }

    public async Task<bool> DeactivateTenantAsync(Guid id)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.DeleteAsync($"api/tenants/{id}");
        return response.IsSuccessStatusCode;
    }

    public async Task<SubscriptionTierInfo[]?> GetSubscriptionTiersAsync()
    {
        var response = await http.GetFromJsonAsync<ApiResponse<SubscriptionTierInfo[]>>("api/tenants/subscription-tiers");
        return response?.Data;
    }

    // Lookup APIs. Reading is open to any authenticated user; creating, updating and
    // reordering are SuperAdmin-only and reject locked types server-side - see
    // /admin/lookups.
    public async Task<List<LookupTypeDto>?> GetLookupTypesAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<List<LookupTypeDto>>>("api/lookups/types");
        return response?.Data;
    }

    public async Task<List<LookupValueDto>?> GetLookupValuesAsync(string lookupType, bool includeInactive = false)
    {
        var client = await GetAuthenticatedClient();
        var url = $"api/lookups/{Uri.EscapeDataString(lookupType)}?includeInactive={includeInactive}";
        var response = await client.GetFromJsonAsync<ApiResponse<List<LookupValueDto>>>(url);
        return response?.Data;
    }

    public async Task<(bool Success, LookupValueDto? Value, string? Error)> CreateLookupValueAsync(CreateLookupValueDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("api/lookups", dto);

        var payload = await response.Content.ReadFromJsonAsync<ApiResponse<LookupValueDto>>();
        if (!response.IsSuccessStatusCode)
            return (false, null, payload?.Message ?? "Failed to create value.");

        return (true, payload?.Data, null);
    }

    public async Task<(bool Success, string? Error)> UpdateLookupValueAsync(int id, UpdateLookupValueDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PutAsJsonAsync($"api/lookups/{id}", dto);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to update value.");
        }

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> ReorderLookupValuesAsync(string lookupType, List<int> orderedIds)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync($"api/lookups/{Uri.EscapeDataString(lookupType)}/reorder", orderedIds);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to reorder values.");
        }

        return (true, null);
    }

    // Platform SMTP settings (SuperAdmin only) - what makes forgot-password mail actually
    // send. The stored password is never returned by the API; HasPassword is the only signal
    // the UI gets, and an empty Password on save means "keep the current one".
    // SafeGetAsync rather than GetFromJsonAsync: this is the first call the admin page makes
    // from OnInitializedAsync, where a throw (an expired token gives a 401) escapes as an
    // unhandled exception and drops Blazor's error bar over the app instead of the page's own
    // "couldn't load" message.
    public async Task<EmailSettingsDto?> GetEmailSettingsAsync()
    {
        var response = await SafeGetAsync<ApiResponse<EmailSettingsDto>>("api/settings/email");
        return response?.Data;
    }

    public async Task<(bool Success, string? Error)> UpdateEmailSettingsAsync(UpdateEmailSettingsDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PutAsJsonAsync("api/settings/email", dto);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
            return (false, error?.Message ?? "Failed to save email settings.");
        }

        return (true, null);
    }

    public async Task<(bool Success, string? Message)> SendTestEmailAsync(string testEmail)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("api/settings/email/test", new TestEmailDto { TestEmail = testEmail });

        var payload = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        return (response.IsSuccessStatusCode, payload?.Message);
    }

    // Roles and permissions.

    public async Task<List<RoleDto>?> GetRolesAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<List<RoleDto>>>("api/roles");
        return response?.Data;
    }

    public async Task<List<AssignableRoleDto>?> GetAssignableRolesAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<List<AssignableRoleDto>>>("api/roles/assignable");
        return response?.Data;
    }

    public async Task<List<PermissionGroupDto>?> GetPermissionGroupsAsync()
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetFromJsonAsync<ApiResponse<List<PermissionGroupDto>>>("api/permissions");
        return response?.Data;
    }

    public async Task<(bool Success, RoleDto? Role, string? Error)> CreateRoleAsync(CreateRoleDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("api/roles", dto);

        if (!response.IsSuccessStatusCode)
            return (false, null, await ReadRoleErrorAsync(response, "create"));

        var payload = await response.Content.ReadFromJsonAsync<ApiResponse<RoleDto>>();
        return (true, payload?.Data, null);
    }

    public async Task<(bool Success, string? Error)> UpdateRoleAsync(string id, UpdateRoleDto dto)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.PutAsJsonAsync($"api/roles/{id}", dto);

        if (!response.IsSuccessStatusCode)
            return (false, await ReadRoleErrorAsync(response, "update"));

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteRoleAsync(string id)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.DeleteAsync($"api/roles/{id}");

        if (!response.IsSuccessStatusCode)
            return (false, await ReadRoleErrorAsync(response, "delete"));

        return (true, null);
    }

    /// <summary>
    /// A 403 from the authorization middleware itself (as opposed to one a controller action
    /// returns deliberately) has an empty body - ReadFromJsonAsync throws JsonException on that,
    /// which used to surface as an unhandled error instead of a message. Read the body as a string
    /// first and only attempt to parse it if there's anything there, falling back to a
    /// status-derived message otherwise.
    /// </summary>
    private static async Task<string> ReadRoleErrorAsync(HttpResponseMessage response, string action)
    {
        var body = await response.Content.ReadAsStringAsync();

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var error = JsonSerializer.Deserialize<ApiResponse<object>>(body, ErrorBodyJsonOptions);
                if (!string.IsNullOrWhiteSpace(error?.Message))
                    return error.Message;
            }
            catch (JsonException)
            {
                // Not a JSON body (or not our shape) - fall through to the status-derived message.
            }
        }

        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.Forbidden => "You don't have permission to do that.",
            System.Net.HttpStatusCode.Unauthorized => "Session expired. Please log in again.",
            System.Net.HttpStatusCode.NotFound => "That role could not be found.",
            _ => $"Failed to {action} role."
        };
    }

    // Ticket attachments: reading, downloading and removing. The API has had these endpoints all
    // along; only UploadTicketAttachmentAsync was ever written here, which is why nothing in the
    // app could display an attachment that had been uploaded.

    public async Task<List<TicketAttachmentDto>?> GetTicketAttachmentsAsync(int ticketId)
    {
        var client = await GetAuthenticatedClient();
        try
        {
            return await client.GetFromJsonAsync<List<TicketAttachmentDto>>(
                $"api/tickets/{ticketId}/attachments");
        }
        catch
        {
            // GetFromJsonAsync throws on any non-success status. A caller showing a gallery wants
            // an empty/error state, not an unhandled exception that blanks the whole page.
            return null;
        }
    }

    public async Task<(bool Success, byte[]? Data, string? ContentType, string? Error)> DownloadTicketAttachmentAsync(
        int ticketId, int attachmentId)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.GetAsync($"api/tickets/{ticketId}/attachments/{attachmentId}/download");

        if (!response.IsSuccessStatusCode)
            return (false, null, null, $"Download failed ({(int)response.StatusCode}).");

        var data = await response.Content.ReadAsByteArrayAsync();
        return (true, data, response.Content.Headers.ContentType?.MediaType, null);
    }

    public async Task<(bool Success, string? Error)> DeleteTicketAttachmentAsync(int ticketId, int attachmentId)
    {
        var client = await GetAuthenticatedClient();
        var response = await client.DeleteAsync($"api/tickets/{ticketId}/attachments/{attachmentId}");

        if (response.IsSuccessStatusCode)
            return (true, null);

        // Read the body defensively: a 403 from the authorization middleware has an empty body,
        // and ReadFromJsonAsync throws on that.
        var body = await response.Content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var payload = System.Text.Json.JsonSerializer.Deserialize<ApiResponse<object>>(
                    body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                if (!string.IsNullOrWhiteSpace(payload?.Message))
                    return (false, payload.Message);
            }
            catch
            {
                // Non-JSON body (a proxy error page). Fall through to the status-derived message.
            }
        }

        return (false, response.StatusCode == System.Net.HttpStatusCode.Forbidden
            ? "You can only remove attachments you uploaded."
            : $"Couldn't remove the attachment ({(int)response.StatusCode}).");
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

public record SubscriptionTierInfo
{
    public string Name { get; init; } = "";
    public int MaxAssets { get; init; }
    public int MaxUsers { get; init; }
    public long MaxStorageBytes { get; init; }
    public string MaxStorageDisplay { get; init; } = "";
}
