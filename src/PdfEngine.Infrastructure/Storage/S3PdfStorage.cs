using System;
using System.IO;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Storage;

public class S3PdfStorage : IPdfStorage
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;

    public S3PdfStorage(IAmazonS3 s3Client, string bucketName)
    {
        _s3Client = s3Client;
        _bucketName = bucketName;
    }

    public async Task<string> SaveAsync(byte[] pdfBytes, string jobId, string documentName)
    {
        try
        {
            // Automatically ensure bucket exists on first write
            try
            {
                if (!await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(_s3Client, _bucketName))
                {
                    await _s3Client.PutBucketAsync(_bucketName);
                }
            }
            catch (Exception ex)
            {
                // Fail silently or log
                Console.WriteLine($"Error verifying or creating S3 bucket '{_bucketName}': {ex.Message}");
            }

            var key = $"{jobId}/{documentName}.pdf";

            using var stream = new MemoryStream(pdfBytes);
            var putRequest = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                InputStream = stream,
                ContentType = "application/pdf"
            };

            // Upload to S3
            await _s3Client.PutObjectAsync(putRequest);

            var urlRequest = new GetPreSignedUrlRequest
            {
                BucketName = _bucketName,
                Key = key,
                Expires = DateTime.UtcNow.AddHours(24),
                Protocol = Protocol.HTTP
            };

            return _s3Client.GetPreSignedURL(urlRequest);
        }
        catch (Exception)
        {
            // Outage/Connection Loss Fallback cache
            var fallbackDir = Path.Combine(Path.GetTempPath(), "pdfengine_storage_fallback");
            if (!Directory.Exists(fallbackDir))
            {
                Directory.CreateDirectory(fallbackDir);
            }
            var fileName = $"{jobId}_{documentName}.pdf";
            var filePath = Path.Combine(fallbackDir, fileName);
            await File.WriteAllBytesAsync(filePath, pdfBytes);
            return "fallback://" + filePath;
        }
    }

    public async Task<Stream?> GetStreamAsync(string fileUrl)
    {
        if (fileUrl.StartsWith("fallback://", StringComparison.OrdinalIgnoreCase))
        {
            var filePath = fileUrl.Substring("fallback://".Length);
            if (File.Exists(filePath))
            {
                return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            return null;
        }

        try
        {
            var uri = new Uri(fileUrl);
            var path = uri.AbsolutePath; // e.g., /pdf-storage/jobId/document.pdf
            
            // Extract the key: remove leading slash and bucket name if present
            var bucketPrefix = $"/{_bucketName}/";
            string key = path;
            if (path.StartsWith(bucketPrefix, StringComparison.OrdinalIgnoreCase))
            {
                key = path.Substring(bucketPrefix.Length);
            }
            else if (path.StartsWith("/"))
            {
                key = path.Substring(1);
            }

            var response = await _s3Client.GetObjectAsync(_bucketName, Uri.UnescapeDataString(key));
            return response.ResponseStream;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
