using System;
using System.IO;
using System.IO.Compression;

namespace PdfEngine.Infrastructure.Security;

public static class ZipValidator
{
    private const long MaxUncompressedSize = 20 * 1024 * 1024; // 20 MB
    private const int MaxEntryCount = 100;
    private const double MaxRatio = 100.0; // Max uncompressed/compressed ratio

    public static (bool IsSafe, string? ErrorMessage) ValidateZipStream(Stream zipStream)
    {
        try
        {
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            
            long totalUncompressedSize = 0;
            int entryCount = 0;

            foreach (var entry in archive.Entries)
            {
                entryCount++;
                if (entryCount > MaxEntryCount)
                {
                    return (false, $"ZIP contains too many entries (limit: {MaxEntryCount}).");
                }

                // Check for Zip Slip vulnerability
                var normalizedPath = entry.FullName.Replace('\\', '/');
                if (normalizedPath.Contains("../") || normalizedPath.Contains("..\\"))
                {
                    return (false, "Path traversal attempt detected in ZIP entries.");
                }

                // Protect against unknown/infinite size entries (e.g. sparse files)
                if (entry.Length < 0)
                {
                    return (false, "Invalid ZIP entry length.");
                }

                totalUncompressedSize += entry.Length;
                if (totalUncompressedSize > MaxUncompressedSize)
                {
                    return (false, $"Uncompressed ZIP size exceeds limits (limit: {MaxUncompressedSize / (1024 * 1024)}MB).");
                }
            }

            // Check compression ratio
            if (zipStream.Length > 0)
            {
                double ratio = (double)totalUncompressedSize / zipStream.Length;
                if (ratio > MaxRatio)
                {
                    return (false, $"Abnormal compression ratio detected: {ratio:F1}x (potential ZIP bomb).");
                }
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to parse ZIP archive: {ex.Message}");
        }
    }
}
