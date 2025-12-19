namespace IAMS.Api.Services;

public interface IFileStorageService
{
    Task<string> SaveFileAsync(Stream fileStream, string fileName, string contentType);
    Task<(Stream FileStream, string ContentType)?> GetFileAsync(string storedFileName);
    Task<bool> DeleteFileAsync(string storedFileName);
    bool IsValidFileType(string contentType);
    bool IsValidFileSize(long sizeBytes);
}

public class FileStorageService : IFileStorageService
{
    private readonly string _uploadPath;
    private readonly long _maxFileSizeBytes;
    private readonly ILogger<FileStorageService> _logger;

    // Allowed MIME types for attachments
    private static readonly HashSet<string> AllowedContentTypes =
    [
        "image/jpeg",
        "image/png",
        "image/gif",
        "image/webp",
        "application/pdf",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "text/plain"
    ];

    public FileStorageService(IConfiguration configuration, ILogger<FileStorageService> logger)
    {
        _logger = logger;
        _maxFileSizeBytes = configuration.GetValue("FileStorage:MaxFileSizeMB", 5) * 1024 * 1024;
        _uploadPath = configuration.GetValue("FileStorage:UploadPath", "Uploads/Attachments")!;

        // Ensure upload directory exists
        if (!Directory.Exists(_uploadPath))
        {
            Directory.CreateDirectory(_uploadPath);
            _logger.LogInformation("Created upload directory: {UploadPath}", _uploadPath);
        }
    }

    public async Task<string> SaveFileAsync(Stream fileStream, string fileName, string contentType)
    {
        // Generate unique storage name to prevent conflicts
        var extension = Path.GetExtension(fileName);
        var storedFileName = $"{Guid.NewGuid()}{extension}";
        var filePath = Path.Combine(_uploadPath, storedFileName);

        await using var outputStream = File.Create(filePath);
        await fileStream.CopyToAsync(outputStream);

        _logger.LogInformation("File saved: {StoredFileName} (Original: {FileName}, Size: {Size} bytes)",
            storedFileName, fileName, outputStream.Length);

        return storedFileName;
    }

    public Task<(Stream FileStream, string ContentType)?> GetFileAsync(string storedFileName)
    {
        var filePath = Path.Combine(_uploadPath, storedFileName);

        if (!File.Exists(filePath))
        {
            _logger.LogWarning("File not found: {StoredFileName}", storedFileName);
            return Task.FromResult<(Stream, string)?>(null);
        }

        var extension = Path.GetExtension(storedFileName).ToLowerInvariant();
        var contentType = GetContentTypeFromExtension(extension);

        var fileStream = File.OpenRead(filePath);
        return Task.FromResult<(Stream, string)?>((fileStream, contentType));
    }

    public Task<bool> DeleteFileAsync(string storedFileName)
    {
        var filePath = Path.Combine(_uploadPath, storedFileName);

        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Attempted to delete non-existent file: {StoredFileName}", storedFileName);
            return Task.FromResult(false);
        }

        File.Delete(filePath);
        _logger.LogInformation("File deleted: {StoredFileName}", storedFileName);
        return Task.FromResult(true);
    }

    public bool IsValidFileType(string contentType) => AllowedContentTypes.Contains(contentType.ToLowerInvariant());

    public bool IsValidFileSize(long sizeBytes) => sizeBytes <= _maxFileSizeBytes;

    private static string GetContentTypeFromExtension(string extension) => extension switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".txt" => "text/plain",
        _ => "application/octet-stream"
    };
}
