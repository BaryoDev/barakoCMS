using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Service for managing backup operations.
/// Centralizes backup directory logic, validation, and size limits.
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// Gets the backup directory path (from environment variable or default).
    /// </summary>
    string GetBackupDirectory();

    /// <summary>
    /// Validates that the backup ID is safe (no path traversal).
    /// </summary>
    bool IsValidBackupId(string? id);

    /// <summary>
    /// Checks if the total backup size exceeds the limit.
    /// </summary>
    bool IsBackupStorageFull(long maxBytes = 10_000_000_000); // 10GB default
}

/// <summary>
/// Implementation of IBackupService.
/// </summary>
public class BackupService : IBackupService
{
    private readonly IWebHostEnvironment _env;

    public BackupService(IWebHostEnvironment env)
    {
        _env = env;
    }

    public string GetBackupDirectory()
    {
        // Allow override via environment variable for container deployments
        var customDir = Environment.GetEnvironmentVariable("BARAKO_BACKUP_DIR");
        if (!string.IsNullOrEmpty(customDir))
        {
            Directory.CreateDirectory(customDir);
            return customDir;
        }

        var defaultDir = Path.Combine(_env.ContentRootPath, "Backups");
        Directory.CreateDirectory(defaultDir);
        return defaultDir;
    }

    public bool IsValidBackupId(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return false;

        // Prevent path traversal attacks
        if (id.Contains("..") || id.Contains("/") || id.Contains("\\"))
            return false;

        // Only allow alphanumeric, dash, underscore, and dot
        return id.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.');
    }

    public bool IsBackupStorageFull(long maxBytes = 10_000_000_000)
    {
        var dir = GetBackupDirectory();
        if (!Directory.Exists(dir))
            return false;

        var totalSize = new DirectoryInfo(dir)
            .GetFiles("*.json")
            .Sum(f => f.Length);

        return totalSize >= maxBytes;
    }
}
