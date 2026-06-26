using System.Net;
using AditiKraft.Aspire.Hosting.SecretSync.Abstractions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace AditiKraft.Aspire.Hosting.SecretSync.Providers;

public sealed class S3SecretSyncProvider : ISecretSyncProvider
{
    public string Name => "S3";

    public async Task<SecretSyncRemoteObject?> GetAsync(
        SecretSyncProviderContext context,
        CancellationToken cancellationToken)
    {
        Validate(context);

        try
        {
            using AmazonS3Client client = CreateClient(context.Options.S3);
            using GetObjectResponse response = await client.GetObjectAsync(
                context.BucketName,
                context.ObjectKey,
                cancellationToken);

            using MemoryStream ms = new();
            await response.ResponseStream.CopyToAsync(ms, cancellationToken);

            Dictionary<string, string> metadata = new(StringComparer.OrdinalIgnoreCase);
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

        using AmazonS3Client client = CreateClient(context.Options.S3);
        using MemoryStream stream = new(body);

        PutObjectRequest request = new()
        {
            BucketName = context.BucketName,
            Key = context.ObjectKey,
            InputStream = stream,
            AutoCloseStream = false,
            ContentType = "application/vnd.aditikraft.secretsync+json",
            DisableDefaultChecksumValidation = context.Options.S3.DisableDefaultChecksumValidation,
            DisablePayloadSigning = context.Options.S3.DisablePayloadSigning,
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

    private static AmazonS3Client CreateClient(S3SecretSyncOptions s3)
    {
        BasicAWSCredentials credentials = new(s3.AccessKeyId, s3.SecretAccessKey);
        AmazonS3Config config = new()
        {
            ServiceURL = s3.Endpoint,
            ForcePathStyle = s3.ForcePathStyle,
            AuthenticationRegion = string.IsNullOrWhiteSpace(s3.Region) ? "us-east-1" : s3.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED
        };

        return new AmazonS3Client(credentials, config);
    }

    private static void Validate(SecretSyncProviderContext context)
    {
        if (string.IsNullOrWhiteSpace(context.BucketName))
        {
            throw new InvalidOperationException("SecretSync S3 BucketName is required.");
        }

        if (string.IsNullOrWhiteSpace(context.ObjectKey))
        {
            throw new InvalidOperationException("SecretSync S3 ManifestKey is required.");
        }

        S3SecretSyncOptions s3 = context.Options.S3;
        if (string.IsNullOrWhiteSpace(s3.Endpoint) ||
            string.IsNullOrWhiteSpace(s3.AccessKeyId) ||
            string.IsNullOrWhiteSpace(s3.SecretAccessKey))
        {
            throw new InvalidOperationException("SecretSync S3 Endpoint, AccessKeyId, and SecretAccessKey are required.");
        }
    }
}
