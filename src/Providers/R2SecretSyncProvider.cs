using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AditiKraft.Aspire.Hosting.SecretSync.Abstractions;

namespace AditiKraft.Aspire.Hosting.SecretSync.Providers;

public sealed class R2SecretSyncProvider : ISecretSyncProvider
{
    public string Name => "CloudflareR2";

    public async Task<SecretSyncRemoteObject?> GetAsync(
        SecretSyncProviderContext context,
        CancellationToken cancellationToken)
    {
        Validate(context);

        try
        {
            using AmazonS3Client client = CreateClient(context.Options.R2);
            using GetObjectResponse response = await client.GetObjectAsync(
                context.BucketName,
                context.ObjectKey,
                cancellationToken);

            using var ms = new MemoryStream();
            await response.ResponseStream.CopyToAsync(ms, cancellationToken);

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in response.Metadata.Keys)
            {
                metadata[key] = response.Metadata[key];
            }

            metadata.TryGetValue("x-amz-meta-secret-sync-revision", out string? revision);
            metadata.TryGetValue("secret-sync-revision", out string? alternateRevision);

            return new SecretSyncRemoteObject(
                ms.ToArray(),
                response.ETag,
                revision ?? alternateRevision,
                response.LastModified == default ? null : response.LastModified,
                metadata);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchKey")
        {
            return null;
        }
    }

    public async Task<SecretSyncRemoteWriteResult> PutAsync(
        SecretSyncProviderContext context,
        byte[] body,
        SecretSyncWriteCondition condition,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        Validate(context);

        using AmazonS3Client client = CreateClient(context.Options.R2);
        using var stream = new MemoryStream(body);

        var request = new PutObjectRequest
        {
            BucketName = context.BucketName,
            Key = context.ObjectKey,
            InputStream = stream,
            AutoCloseStream = false,
            ContentType = "application/vnd.aditikraft.secretsync+json",
            DisableDefaultChecksumValidation = context.Options.R2.DisableDefaultChecksumValidation,
            DisablePayloadSigning = context.Options.R2.DisablePayloadSigning,
            UseChunkEncoding = false
        };

        request.Headers.ContentLength = body.Length;

        if (condition.IfMissing)
        {
            request.IfNoneMatch = "*";
        }
        else if (!string.IsNullOrWhiteSpace(condition.IfMatchETag))
        {
            request.IfMatch = condition.IfMatchETag;
        }

        foreach ((string key, string value) in metadata)
        {
            request.Metadata[key] = value;
        }

        try
        {
            PutObjectResponse response = await client.PutObjectAsync(request, cancellationToken);
            return new SecretSyncRemoteWriteResult(
                response.ETag,
                metadata.TryGetValue("secret-sync-revision", out string? revision) ? revision : null,
                DateTimeOffset.UtcNow);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed || ex.ErrorCode == "ConditionalRequestConflict")
        {
            throw new SecretSyncConflictException(
                "Remote secret blob changed while this AppHost was running. Pull, merge, then retry.",
                ex);
        }
    }

    private static AmazonS3Client CreateClient(R2SecretSyncOptions r2)
    {
        var credentials = new BasicAWSCredentials(r2.AccessKeyId, r2.SecretAccessKey);
        var config = new AmazonS3Config
        {
            ServiceURL = r2.Endpoint,
            ForcePathStyle = r2.ForcePathStyle,
            AuthenticationRegion = string.IsNullOrWhiteSpace(r2.Region) ? "auto" : r2.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED
        };

        return new AmazonS3Client(credentials, config);
    }

    private static void Validate(SecretSyncProviderContext context)
    {
        if (string.IsNullOrWhiteSpace(context.BucketName))
        {
            throw new InvalidOperationException("SecretSync BucketName is required.");
        }

        if (string.IsNullOrWhiteSpace(context.ObjectKey))
        {
            throw new InvalidOperationException("SecretSync ObjectKey is required.");
        }

        R2SecretSyncOptions r2 = context.Options.R2;
        if (string.IsNullOrWhiteSpace(r2.Endpoint) ||
            string.IsNullOrWhiteSpace(r2.AccessKeyId) ||
            string.IsNullOrWhiteSpace(r2.SecretAccessKey))
        {
            throw new InvalidOperationException("SecretSync R2 Endpoint, AccessKeyId, and SecretAccessKey are required.");
        }
    }
}
