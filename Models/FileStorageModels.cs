using Microsoft.Extensions.Options;

namespace HrmApi.Models;

public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    public string Provider { get; set; } = "Local"; // "Local", "AzureBlob", "S3"
    public LocalFileStorageOptions Local { get; set; } = new();
}

public sealed class LocalFileStorageOptions
{
    public string UploadPath { get; set; } = "uploads/leaves";
    public string BaseUrl { get; set; } = "/api/leaves/files";
}

public interface IFileStorageService
{
    Task<UploadResult> UploadAsync(IFormFile file, CancellationToken cancellationToken = default);
    Task<FileStream?> DownloadAsync(string fileName, CancellationToken cancellationToken = default);
    Task DeleteAsync(string fileName, CancellationToken cancellationToken = default);
    bool Exists(string fileName);
}

public sealed class UploadResult
{
    public string Url { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string ContentType { get; set; } = string.Empty;
}