namespace GridCore.Modules.Customers.Features.Applications;

/// <summary>
/// What a document attached to an application <i>is</i> — not what file format it happens to be in.
/// </summary>
/// <remarks>
/// The checklist is expressed in these terms because that is how the counter asks for them: a rep
/// says "we still need proof you live there", never "we still need a second PDF". Which kinds an
/// application must have is <see cref="ServiceApplicationTypes.RequiredDocuments"/>.
/// </remarks>
public enum ApplicationDocumentKind
{
    /// <summary>A government-issued identity document for the applicant. Required on every application.</summary>
    PhotoId = 1,

    /// <summary>
    /// A lease or a deed — evidence that the applicant is entitled to take service at the premise.
    /// One kind rather than two, because the utility's question is "may you", and a tenant answers
    /// it with a lease and an owner with a deed.
    /// </summary>
    ProofOfOccupancy = 2,

    /// <summary>A CNMI business licence. Required for a commercial connection and meaningless on a household's.</summary>
    BusinessLicence = 3,

    /// <summary>
    /// Anything else the applicant or the reviewer thought worth attaching — a site plan, a letter
    /// from a landlord, a previous account number. Never required, and never satisfies a
    /// requirement: an escape hatch that could close the checklist would be a checklist in name only.
    /// </summary>
    Other = 99,
}

/// <summary>
/// What GridCore will accept as an uploaded document, and how large it may be.
/// </summary>
/// <remarks>
/// <para>
/// Pure and static so the same rules reach the endpoint, the service and a browser rendering the
/// file picker's <c>accept</c> attribute — one list, not three that drift.
/// </para>
/// <para>
/// <b>An allow-list, and a short one.</b> The counter scans documents; a scanner produces a PDF or
/// an image and nothing else. Everything a utility has ever been attacked through — an office
/// document with a macro, an archive, an SVG with script in it — is absent because it was never
/// asked for, which is a cheaper defence than trying to sanitise the alternative.
/// </para>
/// </remarks>
public static class ApplicationDocuments
{
    /// <summary>Largest upload accepted, in bytes. A scanned identity page is a few hundred kilobytes.</summary>
    /// <remarks>
    /// Generous rather than tight: a multi-page lease scanned at 300 dpi is the biggest thing this
    /// register will legitimately hold, and refusing one at the counter is worse than storing it.
    /// The limit exists so a single request cannot fill the bucket, not to ration honest documents.
    /// </remarks>
    public const long MaxSizeInBytes = 10L * 1024 * 1024;

    /// <summary>The media types an upload may declare, lower-cased.</summary>
    public static IReadOnlySet<string> AllowedContentTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/jpeg",
        "image/png",
    };

    /// <summary>Whether <paramref name="contentType"/> is one GridCore accepts.</summary>
    /// <remarks>
    /// Any parameters after a semicolon — <c>image/jpeg; charset=binary</c>, which some browsers
    /// send — are stripped before the comparison, so a well-formed header is not refused for
    /// carrying something the allow-list does not care about.
    /// </remarks>
    public static bool IsAllowed(string? contentType) => AllowedContentTypes.Contains(Normalise(contentType) ?? string.Empty);

    /// <summary>
    /// <paramref name="contentType"/> trimmed of its parameters and lower-cased, or
    /// <see langword="null"/> when there is nothing there.
    /// </summary>
    public static string? Normalise(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var separator = contentType.IndexOf(';', StringComparison.Ordinal);
        var media = separator < 0 ? contentType : contentType[..separator];

        return media.Trim().ToLowerInvariant() is { Length: > 0 } normalised ? normalised : null;
    }

    /// <summary>
    /// The file extension an accepted media type is filed under, so an object pulled straight out
    /// of the bucket by an administrator opens in the right application.
    /// </summary>
    /// <remarks>
    /// Only the accepted types have an answer — the caller has already been past
    /// <see cref="IsAllowed"/> by the time a key is minted — and an unrecognised type falls back to
    /// <c>.bin</c> rather than throwing, because a filing decision is not the place to discover a
    /// validation failure.
    /// </remarks>
    public static string ExtensionFor(string? contentType) => Normalise(contentType) switch
    {
        "application/pdf" => ".pdf",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        _ => ".bin",
    };
}
