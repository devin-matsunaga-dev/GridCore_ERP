using GridCore.Modules.Customers.Features.Customers;

namespace GridCore.Modules.Customers.Features.Applications;

/// <summary>
/// What kind of connection is being applied for — the key the required-document checklist is
/// keyed on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived from the customer's class and then stamped, never typed.</b> The type is not a
/// separate fact an applicant asserts: a business applying for a supply is applying for a
/// commercial connection, and letting the form say otherwise would let a shop dodge the business
/// licence by ticking "residential". <see cref="ServiceApplicationTypes.For"/> is the whole of the
/// mapping and <see cref="ServiceApplication.Type"/> is where the answer is kept — stamped at
/// submission, the way <c>DepositAssessment.RuleId</c> is, so an application reviewed years ago
/// still says which checklist it was held to even after WP-2.15 re-classifies the customer.
/// </para>
/// <para>
/// Stored by name, so the numbering here is never load-bearing. Adding a member means giving it a
/// document list in <see cref="ServiceApplicationTypes"/>, which throws rather than defaulting —
/// so a type added without one cannot be submitted at all, rather than quietly requiring nothing.
/// </para>
/// </remarks>
public enum ServiceApplicationType
{
    /// <summary>A household taking supply. Photo ID and proof of occupancy.</summary>
    ResidentialConnection = 1,

    /// <summary>A business or institution taking supply. The household's two documents and a business licence.</summary>
    CommercialConnection = 2,
}

/// <summary>Which documents each kind of application must carry before it may be approved.</summary>
/// <remarks>
/// Pure and static, deliberately: the checklist is what makes an approval mean something, and a
/// rule held in a table would be a rule an administrator could empty on the afternoon a difficult
/// application arrives. The figures a utility genuinely re-publishes — fees, deposits, tariffs —
/// are reference data; what evidence identifies an applicant is not.
/// </remarks>
public static class ServiceApplicationTypes
{
    private static readonly Dictionary<ServiceApplicationType, ApplicationDocumentKind[]> Required = new()
    {
        [ServiceApplicationType.ResidentialConnection] =
        [
            ApplicationDocumentKind.PhotoId,
            ApplicationDocumentKind.ProofOfOccupancy,
        ],

        // The business licence is the one document that separates the two lists, and it is the
        // reason the type exists at all — see WORK_PACKAGES.md WP-2.18, "business licence for a
        // commercial connection".
        [ServiceApplicationType.CommercialConnection] =
        [
            ApplicationDocumentKind.PhotoId,
            ApplicationDocumentKind.ProofOfOccupancy,
            ApplicationDocumentKind.BusinessLicence,
        ],
    };

    /// <summary>The kind of connection a customer of <paramref name="customerClass"/> is applying for.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The class is not one GridCore declares. Not a default: a class added later without a line
    /// here would silently be held to the household's checklist, which is exactly the failure the
    /// business licence is there to prevent.
    /// </exception>
    public static ServiceApplicationType For(CustomerClass customerClass) => customerClass switch
    {
        CustomerClass.Residential => ServiceApplicationType.ResidentialConnection,
        CustomerClass.Commercial => ServiceApplicationType.CommercialConnection,
        _ => throw new ArgumentOutOfRangeException(nameof(customerClass), customerClass, "Not a customer class GridCore declares."),
    };

    /// <summary>The documents an application of <paramref name="type"/> must carry to be approved.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The type is not one GridCore declares. Not an empty list, for the reason
    /// <c>TransitionReasons.For</c> throws: a type with no checklist would be a type that approves
    /// with no evidence, and the failure would look like a feature.
    /// </exception>
    public static IReadOnlyList<ApplicationDocumentKind> RequiredDocuments(ServiceApplicationType type) =>
        Required.TryGetValue(type, out var kinds)
            ? kinds
            : throw new ArgumentOutOfRangeException(nameof(type), type, "Not an application type GridCore declares.");

    /// <summary>Whether an application of <paramref name="type"/> must carry a document of <paramref name="kind"/>.</summary>
    public static bool Requires(ServiceApplicationType type, ApplicationDocumentKind kind) =>
        RequiredDocuments(type).Contains(kind);
}
