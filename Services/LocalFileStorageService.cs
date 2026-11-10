using HrmApi.Models;
using Microsoft.Extensions.Options;

namespace HrmApi.Services;

public sealed class LocalFileStorageService : IFileStorageService
{
    private readonly LocalFileStorageOptions _localOptions;
    private readonly string _baseDirectory;

    public LocalFileStorageService(IOptions<FileStorageOptions> options)
    {
        _localOptions = options.Value.Local;
        _baseDirectory = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), _localOptions.UploadPath));
        Directory.CreateDirectory(_baseDirectory);
    }

    public async Task<UploadResult> UploadAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
        {
            throw new ArgumentException("No file uploaded.", nameof(file));
        }

        // Only allow PDF
        if (!file.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only PDF files are allowed.", nameof(file));
        }

        // Max 5MB
        const long maxSize = 5 * 1024 * 1024;
        if (file.Length > maxSize)
        {
            throw new ArgumentException("File size cannot exceed 5MB.", nameof(file));
        }

        // Generate unique filename with .pdf extension (force pdf for safety)
        var uniqueFileName = $"{Guid.NewGuid()}.pdf";
        var filePath = Path.Combine(_baseDirectory, uniqueFileName);

        // Save file
        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await file.CopyToAsync(stream, cancellationToken);

        // Return URL that can be used in leave application
        var url = $"{_localOptions.BaseUrl}/{uniqueFileName}";
        return new UploadResult
        {
            Url = url,
            FileName = Path.GetFileName(file.FileName), // Sanitized to basename only
            Size = file.Length,
            ContentType = file.ContentType
        };
    }

    public Task<FileStream?> DownloadAsync(string fileName, CancellationToken cancellationToken = default)
    {
        // Prevent path traversal - canonical path check
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Task.FromResult<FileStream?>(null);
        }

        // Ensure filename doesn't contain path separators
        if (fileName.Contains("..") || fileName.Contains("/") || fileName.Contains("\\"))
        {
            return Task.FromResult<FileStream?>(null);
        }

        var filePath = Path.Combine(_baseDirectory, fileName);

        // Canonical path check - ensure resolved path is within base directory
        var resolvedPath = Path.GetFullPath(filePath);
        var resolvedBase = Path.GetFullPath(_baseDirectory);

        if (!resolvedPath.StartsWith(resolvedBase, StringComparison.Ordinal))
        {
            return Task.FromResult<FileStream?>(null);
        }

        if (!File.Exists(resolvedPath))
        {
            return Task.FromResult<FileStream?>(null);
        }

        var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return Task.FromResult<FileStream?>(stream);
    }

    public Task DeleteAsync(string fileName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Task.CompletedTask;
        }

        if (fileName.Contains("..") || fileName.Contains("/") || fileName.Contains("\\"))
        {
            return Task.CompletedTask;
        }

        var filePath = Path.Combine(_baseDirectory, fileName);
        var resolvedPath = Path.GetFullPath(filePath);
        var resolvedBase = Path.GetFullPath(_baseDirectory);

        if (!resolvedPath.StartsWith(resolvedBase, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        if (File.Exists(resolvedPath))
        {
            try
            {
                File.Delete(resolvedPath);
            }
            catch
            {
                // Ignore delete failures (file in use, permissions, etc.)
            }
        }

        return Task.CompletedTask;
    }

    public bool Exists(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (fileName.Contains("..") || fileName.Contains("/") || fileName.Contains("\\"))
        {
            return false;
        }

        var filePath = Path.Combine(_baseDirectory, fileName);
        var resolvedPath = Path.GetFullPath(filePath);
        var resolvedBase = Path.GetFullPath(_baseDirectory);

        if (!resolvedPath.StartsWith(resolvedBase, StringComparison.Ordinal))
        {
            return false;
        }

        return File.Exists(resolvedPath);
    }
}