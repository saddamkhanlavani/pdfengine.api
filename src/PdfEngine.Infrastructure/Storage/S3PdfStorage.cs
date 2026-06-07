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

    public Task<Stream?> GetStreamAsync(string fileUrl)
    {
        // S3 Storage uses 302 Redirects directly to the pre-signed URL to save API bandwidth.
        // The JobsController handles the redirect automatically if the FileUrl starts with "http".
        throw new NotSupportedException("S3 Storage does not support direct streaming. Use the Pre-Signed URL for 302 Redirects.");
    }
}
