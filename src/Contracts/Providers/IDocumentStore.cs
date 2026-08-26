namespace GridCore.Contracts.Providers;

/// <summary>
/// A file to be put in the object store, with the key it is to be filed under.
/// </summary>
/// <remarks>
/// <b>The caller mints the key, not the store.</b> A key is a filing decision — "this is document
/// <c>d</c> of application <c>a</c>" — and a store that invented one would be a store that decides
/// how a module organises its evidence. It also makes the round trip provable: the module that
/// wrote the key is the module that can go and find the object again years later, holding nothing
/// but its own row.
/// </remarks>
/// <param name="Key">Where to file it, relative to the store's own container.</param>
/// <param name="ContentType">The IANA media type of the bytes. Stored with the object, so a reader gets it back.</param>
/// <param name="Content">The bytes themselves.</param>
public sealed record DocumentUpload(string Key, string ContentType, ReadOnlyMemory<byte> Content);

/// <summary>What the store confirms it holds after a <see cref="IDocumentStore.PutAsync"/>.</summary>
/// <param name="Key">Where it was filed.</param>
/// <param name="ContentType">The media type it was filed with.</param>
/// <param name="SizeInBytes">How large the object is, as the store measured it.</param>
public sealed record StoredDocument(string Key, string ContentType, long SizeInBytes);

/// <summary>A file read back out of the object store.</summary>
/// <param name="Key">Where it was filed.</param>
/// <param name="ContentType">The media type it was filed with.</param>
/// <param name="SizeInBytes">How large the object is.</param>
/// <param name="Content">The bytes.</param>
public sealed record StoredDocumentContent(string Key, string ContentType, long SizeInBytes, ReadOnlyMemory<byte> Content);

/// <summary>
/// The object store, as a module sees it: put a file somewhere, get it back later. MinIO is behind
/// it in every environment GridCore ships, and nothing above this line knows that.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately the smallest seam that does the job.</b> No listing, no presigned URLs, no
/// copying — an upload and a read back are the whole of what an evidence register needs, and every
/// method here is one a fake in the fast tier can implement honestly in three lines
/// (CONVENTIONS.md rule C). A wider seam would be a wider fake, and a fake that has to guess is
/// a fake that lets a bug through.
/// </para>
/// <para>
/// <b>And no delete.</b> The first user of this seam is WP-2.18's application documents, which is
/// an append-only evidence register: what was reviewed has to stay provable, so a wrong file is
/// superseded by a right one rather than removed. A store with no delete cannot be talked into
/// destroying evidence by a later caller in a hurry, and the day something genuinely has to expire
/// — a retention policy, not a mistake — it is a lifecycle rule on the bucket rather than a method
/// somebody can reach from a request.
/// </para>
/// <para>
/// <b>Bytes, not streams.</b> Every document this seam carries is a scanned identity page or a
/// lease, capped by its own module at a few megabytes, and a <see cref="ReadOnlyMemory{T}"/> is
/// what lets the caller checksum exactly what it stored without reading the stream twice or
/// rewinding one it does not own. A seam that had to carry a report or a video would want a stream,
/// and would want changing when that day comes.
/// </para>
/// <para>
/// <b>The checksum is the caller's, not the store's.</b> An integrity check computed by the thing
/// being checked proves nothing; the module hashes the bytes it was handed and records the digest
/// on its own row, so a later read back can be compared against a figure the store never touched.
/// </para>
/// </remarks>
public interface IDocumentStore
{
    /// <summary>Files <paramref name="upload"/> under its key, replacing anything already there.</summary>
    /// <exception cref="DocumentStoreException">The store could not be reached, or refused the write.</exception>
    Task<StoredDocument> PutAsync(DocumentUpload upload, CancellationToken cancellationToken = default);

    /// <summary>Reads back the object at <paramref name="key"/>, or <see langword="null"/> if there is nothing there.</summary>
    /// <exception cref="DocumentStoreException">The store could not be reached, or refused the read.</exception>
    Task<StoredDocumentContent?> GetAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// The object store could not do what was asked. Thrown by every implementation so a caller can
/// tell "the store is unavailable" from "there is no such object", which is
/// <see langword="null"/> and not an exception.
/// </summary>
public sealed class DocumentStoreException(string message, Exception? innerException = null)
    : Exception(message, innerException);
