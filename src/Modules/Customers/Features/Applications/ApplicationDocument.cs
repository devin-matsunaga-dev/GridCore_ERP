using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.Features.Applications;

/// <summary>
/// One file attached to an application: what it is, who produced it, how big it was and what it
/// hashed to. The <i>record</i> of the evidence — the bytes are in the object store, behind
/// <c>IDocumentStore</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only, like every other register in this module.</b> There is no remove and no edit: a
/// wrong file is superseded by uploading a right one of the same kind, and the checklist reads the
/// newest. WORK_PACKAGES.md asks that "what was reviewed is provable years later", and a row a
/// later caller could delete proves nothing — the same argument WP-2.13 made about the note log and
/// WP-2.12 about the deposit ledger.
/// </para>
/// <para>
/// <b>The checksum is what makes the record evidence rather than a filename.</b> A row saying "a
/// lease was attached" is worth very little on its own; a row saying "the bytes reviewed on this
/// day hashed to <c>ab12…</c>" can be set against whatever the bucket holds today, and disagreement
/// is detectable. It is computed by this module over the bytes it was handed, deliberately not by
/// the store — an integrity check computed by the thing being checked proves nothing.
/// </para>
/// <para>
/// <b><see cref="StorageKey"/> is written once and never derived again.</b> The key is minted from
/// ids that already exist, so it could in principle be recomputed — but a stored key survives a
/// change to how keys are minted, and a recomputed one silently stops finding objects filed under
/// the old scheme.
/// </para>
/// </remarks>
public sealed class ApplicationDocument
{
    /// <summary>Longest stored form of a kind name.</summary>
    public const int EnumNameLength = 64;

    /// <summary>Longest file name kept. Long enough for anything a scanner produces.</summary>
    public const int FileNameLength = 256;

    /// <summary>Longest media type stored.</summary>
    public const int ContentTypeLength = 128;

    /// <summary>Length of the stored digest — SHA-256, lower-case hex.</summary>
    public const int ChecksumLength = 64;

    /// <summary>Longest object key stored.</summary>
    public const int StorageKeyLength = 512;

    private ApplicationDocument()
    {
        // EF materialisation.
        FileName = string.Empty;
        ContentType = string.Empty;
        Checksum = string.Empty;
        StorageKey = string.Empty;
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this document. Guid v7, so the key index already orders it chronologically.</summary>
    public Guid Id { get; private init; }

    /// <summary>The application it was attached to.</summary>
    public Guid ServiceApplicationId { get; private init; }

    /// <summary>What it is — which checklist line it answers, if any.</summary>
    public ApplicationDocumentKind Kind { get; private init; }

    /// <summary>What the uploader called it. Kept for the reviewer's sake; nothing keys off it.</summary>
    public string FileName { get; private init; }

    /// <summary>The media type it was filed under, normalised and lower-cased.</summary>
    public string ContentType { get; private init; }

    /// <summary>How many bytes were stored.</summary>
    public long SizeInBytes { get; private init; }

    /// <summary>SHA-256 of the bytes, lower-case hex — computed here, over what was actually stored.</summary>
    public string Checksum { get; private init; }

    /// <summary>Where the object store filed it.</summary>
    public string StorageKey { get; private init; }

    /// <summary>Subject id of whoever uploaded it.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>When it was uploaded.</summary>
    public DateTimeOffset UploadedAt { get; private init; }

    /// <summary>
    /// Records a document that has already been stored. Called only by
    /// <see cref="ServiceApplication.Attach"/>, which is what stops a row existing for an object
    /// nobody wrote.
    /// </summary>
    /// <exception cref="RegistryValidationException">The kind is not one GridCore declares, or a required field is empty.</exception>
    internal static ApplicationDocument For(
        Guid serviceApplicationId,
        Guid documentId,
        ApplicationDocumentKind kind,
        string fileName,
        string contentType,
        long sizeInBytes,
        string checksum,
        string storageKey,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // A value cast from an unmapped integer would be stored by name as a number and read back as
        // nothing anyone can act on — the guard every enum-carrying aggregate in this module makes.
        if (!Enum.IsDefined(kind))
        {
            throw new RegistryValidationException($"'{kind}' is not an {nameof(ApplicationDocumentKind)} GridCore declares.");
        }

        return new ApplicationDocument
        {
            Id = documentId,
            ServiceApplicationId = serviceApplicationId,
            Kind = kind,
            FileName = RegistryText.Clean(fileName, FileNameLength)
                ?? throw new RegistryValidationException("An uploaded document must have a file name."),
            ContentType = RegistryText.Clean(contentType, ContentTypeLength)
                ?? throw new RegistryValidationException("An uploaded document must declare a media type."),
            SizeInBytes = sizeInBytes,
            Checksum = RegistryText.Clean(checksum, ChecksumLength)
                ?? throw new RegistryValidationException("An uploaded document must carry a checksum."),
            StorageKey = RegistryText.Clean(storageKey, StorageKeyLength)
                ?? throw new RegistryValidationException("An uploaded document must record where it was filed."),
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new RegistryValidationException("An uploaded document must name who uploaded it."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
            UploadedAt = now,
        };
    }
}
