using Amazon.S3;
using Amazon.S3.Model;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EnterprisePulse.Core.Modules.Platform;

public sealed class StorageService(IOptions<AppOptions> options)
{
    private readonly AppOptions _o = options.Value;
    private bool _bucketReady;

    private AmazonS3Client Client() => new(_o.S3AccessKey, _o.S3SecretKey, new AmazonS3Config
    {
        ServiceURL = _o.S3Endpoint,
        ForcePathStyle = true,
        AuthenticationRegion = "us-east-1",
    });

    public async Task PutAsync(string key, Stream content, string contentType)
    {
        using var client = Client();
        if (!_bucketReady)
        {
            try { await client.PutBucketAsync(_o.S3Bucket); } catch (AmazonS3Exception) { /* already exists */ }
            _bucketReady = true;
        }
        await client.PutObjectAsync(new PutObjectRequest { BucketName = _o.S3Bucket, Key = key, InputStream = content, ContentType = contentType, AutoCloseStream = false });
    }

    public async Task<Stream> GetAsync(string key)
    {
        using var client = Client();
        using var response = await client.GetObjectAsync(_o.S3Bucket, key);
        var copy = new MemoryStream();
        await response.ResponseStream.CopyToAsync(copy);
        copy.Position = 0;
        return copy;
    }

    public async Task DeleteAsync(string key)
    {
        using var client = Client();
        await client.DeleteObjectAsync(_o.S3Bucket, key);
    }
}

public sealed class FilesService(AppDbContext db, StorageService storage)
{
    public const long MaxBytes = 20 * 1024 * 1024;

    private static readonly HashSet<string> AllowedTypes =
    [
        "application/pdf", "image/png", "image/jpeg", "image/webp", "image/gif", "text/plain", "text/csv",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/msword", "application/vnd.ms-excel", "application/zip", "application/xml", "text/xml",
    ];

    public async Task<FileObject> UploadAsync(Stream content, string fileName, string contentType, long size, string? entityType, string? entityId)
    {
        Guard.That(size > 0 && size <= MaxBytes, "Dosya boyutu 20 MB'ı geçemez");
        Guard.That(AllowedTypes.Contains(contentType), $"Desteklenmeyen dosya türü: {contentType}");
        var companyId = db.User.RequireCompanyId();
        var key = $"{companyId}/{DateTime.UtcNow:yyyy/MM}/{Guid.NewGuid():N}{Path.GetExtension(fileName)}";
        await storage.PutAsync(key, content, contentType);
        var file = new FileObject
        {
            StorageKey = key,
            OriginalName = Path.GetFileName(fileName),
            MimeType = contentType,
            Size = (int)size,
            EntityType = entityType,
            EntityId = entityId,
            UploadedById = db.User.UserId,
        };
        db.FileObjects.Add(file);
        await db.SaveChangesAsync();
        return file;
    }

    public Task<List<FileObject>> ForEntityAsync(string entityType, string entityId) =>
        db.FileObjects.Where(f => f.EntityType == entityType && f.EntityId == entityId).OrderByDescending(f => f.CreatedAt).ToListAsync();

    public async Task<(FileObject File, Stream Content)> DownloadAsync(Guid id)
    {
        var file = await db.FileObjects.FirstOrDefaultAsync(f => f.Id == id) ?? throw new NotFoundAppException("Dosya bulunamadı");
        return (file, await storage.GetAsync(file.StorageKey));
    }

    public async Task DeleteAsync(Guid id)
    {
        var file = await db.FileObjects.FirstOrDefaultAsync(f => f.Id == id) ?? throw new NotFoundAppException("Dosya bulunamadı");
        Guard.State(file.UploadedById == db.User.UserId || db.User.Has("files:delete"), "Bu dosyayı silme yetkiniz yok");
        db.FileObjects.Remove(file);
        await db.SaveChangesAsync();
    }
}
