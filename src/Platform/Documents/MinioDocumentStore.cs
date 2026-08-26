using GridCore.Contracts.Providers;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace GridCore.Platform.Documents;

/// <summary>Options for the object store, bound from the <c>MinIO</c> section the AppHost supplies.</summary>
/// <remarks>
/// The AppHost passes these as plain environment variables rather than as a connection string —
/// MinIO has no first-party Aspire integration, so there is no connection-string format to parse
/// (see <c>InfrastructureComposition</c>).
/// </remarks>
public sealed class MinioDocumentStoreOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "MinIO";

    /// <summary>Absolute URL of the S3 API endpoint, e.g. <c>http://localhost:9000</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The access key. The MinIO root user in every environment GridCore composes.</summary>
    public string? AccessKey { get; set; }

    /// <summary>The secret key.</summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// Bucket every document is filed in. One bucket for the whole application, with the module's
    /// key doing the organising: buckets are an administrative unit — policy, lifecycle, replication
    /// — and cutting one per feature would mean a new bucket to provision every work package.
    /// </summary>
    public string Bucket { get; set; } = "gridcore-documents";
}

/// <summary>
/// <see cref="IDocumentStore"/> over MinIO — the object store the AppHost has composed since
/// WP-0.2 and WP-2.18 is the first user of.
/// </summary>
/// <remarks>
/// <para>
/// <b>A singleton holding one client.</b> <see cref="IMinioClient"/> owns an
/// <see cref="System.Net.Http.HttpClient"/>, so one per request would be the socket-exhaustion bug
/// every .NET codebase writes once. Nothing here is per-caller: the seam carries the whole of the
/// state a call needs.
/// </para>
/// <para>
/// <b>The bucket is created on first use, once, and never checked again.</b> A store that asked
/// "does the bucket exist" before every upload would double the round trips for a question whose
/// answer changes once in the life of a deployment; a store that demanded the bucket be provisioned
/// out of band would make a fresh developer volume fail at the counter rather than at startup. The
/// gate is a <see cref="SemaphoreSlim"/> rather than a <c>lock</c> because the check is async, and
/// a failed attempt leaves the flag down so the next call tries again — a MinIO that was still
/// starting must not poison the store for the life of the process.
/// </para>
/// <para>
/// <b>Every MinIO failure becomes a <see cref="DocumentStoreException"/>.</b> Callers above the seam
/// must not have to catch <c>Minio.Exceptions.*</c> — that is the whole point of the seam — and the
/// one outcome that is <i>not</i> a failure, "there is no such object", comes back as
/// <see langword="null"/>.
/// </para>
/// </remarks>
public sealed class MinioDocumentStore : IDocumentStore, IDisposable
{
    private readonly IMinioClient _client;
    private readonly string _bucket;
    private readonly SemaphoreSlim _bucketGate = new(1, 1);
    private bool _bucketReady;

    /// <summary>Builds the store from <paramref name="options"/>.</summary>
    /// <exception cref="InvalidOperationException">The endpoint or the credentials are not configured.</exception>
    public MinioDocumentStore(MinioDocumentStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Fail fast and by name, the call the platform's connection string already makes: a host
        // that starts without an object store would 500 on the first upload instead of refusing to
        // boot, and the operator would be reading a stack trace rather than a sentence.
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new InvalidOperationException(
                $"{MinioDocumentStoreOptions.SectionName}:Endpoint is not configured. The Aspire AppHost supplies it; "
                + $"set {MinioDocumentStoreOptions.SectionName}__Endpoint to run the host on its own.");
        }

        if (string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey))
        {
            throw new InvalidOperationException(
                $"{MinioDocumentStoreOptions.SectionName}:AccessKey and {MinioDocumentStoreOptions.SectionName}:SecretKey "
                + "are both required. The AppHost supplies them as parameters.");
        }

        // The scheme is checked as well as the parse, because Uri accepts "localhost:9000" as an
        // absolute URI with the scheme "localhost" — which is exactly the shape somebody types from
        // memory, and which the SDK then rejects several layers down with "Endpoint not initialized".
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{MinioDocumentStoreOptions.SectionName}:Endpoint is '{options.Endpoint}', which is not an absolute "
                + "http:// or https:// URL.");
        }

        _bucket = options.Bucket;

        // WithEndpoint(Uri) takes the scheme from the URL; WithSSL then has to be told the same
        // thing, because the SDK keeps the two apart and a mismatch signs the request for the wrong
        // scheme rather than failing to connect.
        _client = new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(options.AccessKey, options.SecretKey)
            .WithSSL(endpoint.Scheme == Uri.UriSchemeHttps)
            .Build();
    }

    /// <inheritdoc />
    public async Task<StoredDocument> PutAsync(DocumentUpload upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentException.ThrowIfNullOrWhiteSpace(upload.Key);

        await EnsureBucketAsync(cancellationToken).ConfigureAwait(false);

        // The length is known before the write starts, which is what keeps a small document a
        // single-part upload rather than a multipart negotiation. One array copy, deliberately:
        // MemoryStream cannot wrap a ReadOnlyMemory, and reaching for the SDK's own span extensions
        // would put a transitive package in this file for the sake of a few kilobytes.
        using var content = new MemoryStream(upload.Content.ToArray(), writable: false);

        try
        {
            await _client.PutObjectAsync(
                new PutObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(upload.Key)
                    .WithStreamData(content)
                    .WithObjectSize(content.Length)
                    .WithContentType(upload.ContentType),
                cancellationToken).ConfigureAwait(false);
        }
        catch (MinioException exception)
        {
            throw new DocumentStoreException($"The object store refused to file '{upload.Key}'.", exception);
        }

        return new StoredDocument(upload.Key, upload.ContentType, content.Length);
    }

    /// <inheritdoc />
    public async Task<StoredDocumentContent?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await EnsureBucketAsync(cancellationToken).ConfigureAwait(false);

        using var buffer = new MemoryStream();

        try
        {
            var stat = await _client.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(key)
                    .WithCallbackStream((stream, ct) => stream.CopyToAsync(buffer, ct)),
                cancellationToken).ConfigureAwait(false);

            return new StoredDocumentContent(key, stat.ContentType, stat.Size, buffer.ToArray());
        }
        catch (ObjectNotFoundException)
        {
            // Not a failure. "There is nothing filed there" is an answer, and a caller holding a row
            // whose object has gone needs to be able to say so rather than to 500.
            return null;
        }
        catch (BucketNotFoundException)
        {
            return null;
        }
        catch (MinioException exception)
        {
            throw new DocumentStoreException($"The object store could not return '{key}'.", exception);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client.Dispose();
        _bucketGate.Dispose();
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (_bucketReady)
        {
            return;
        }

        await _bucketGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_bucketReady)
            {
                return;
            }

            if (!await _client.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucket), cancellationToken).ConfigureAwait(false))
            {
                await _client.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucket), cancellationToken).ConfigureAwait(false);
            }

            _bucketReady = true;
        }
        catch (MinioException exception)
        {
            throw new DocumentStoreException($"The object store bucket '{_bucket}' could not be reached or created.", exception);
        }
        finally
        {
            _bucketGate.Release();
        }
    }
}
