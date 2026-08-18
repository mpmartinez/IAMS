using IAMS.Api.Services;
using IAMS.Shared.DTOs;

namespace IAMS.Api.Tests;

/// Records what was deleted so a test can assert the stored file went too, not just the row.
internal sealed class FakeFileStorageService : IFileStorageService
{
    public List<string> Deleted { get; } = new();

    public Task<bool> DeleteFileAsync(string storedFileName)
    {
        Deleted.Add(storedFileName);
        return Task.FromResult(true);
    }

    // The delete path touches none of these.
    public Task<string> SaveFileAsync(Stream fileStream, string fileName, string contentType) =>
        throw new NotImplementedException();

    public Task<(Stream FileStream, string ContentType)?> GetFileAsync(string storedFileName) =>
        throw new NotImplementedException();

    public bool IsValidFileType(string contentType) => true;

    public bool IsValidFileSize(long sizeBytes) => true;
}

internal sealed class FakeSubscriptionService : ISubscriptionService
{
    public Task<bool> CanCreateAssetAsync(Guid tenantId) => Task.FromResult(true);
    public Task<bool> CanCreateUserAsync(Guid tenantId) => Task.FromResult(true);
    public Task<bool> CanUploadFileAsync(Guid tenantId, long fileSizeBytes) => Task.FromResult(true);
    public Task<bool> CanCreateTicketAsync(Guid tenantId) => Task.FromResult(true);
    public Task UpdateAssetCountAsync(Guid tenantId) => Task.CompletedTask;
    public Task UpdateUserCountAsync(Guid tenantId) => Task.CompletedTask;
    public Task UpdateStorageUsageAsync(Guid tenantId) => Task.CompletedTask;
    public Task<TenantUsageDto> GetUsageAsync(Guid tenantId) => throw new NotImplementedException();
    public Task<bool> IsSubscriptionActiveAsync(Guid tenantId) => Task.FromResult(true);
}

internal sealed class FakeLookupService : ILookupService
{
    public Task<bool> IsActiveValueAsync(string lookupType, string value, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<List<string>> GetActiveValuesAsync(string lookupType, CancellationToken ct = default) =>
        Task.FromResult(new List<string>());
}
