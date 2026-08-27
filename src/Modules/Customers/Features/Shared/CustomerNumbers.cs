using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.Features.Shared;

/// <summary>
/// The prefixes this module's registry numbers are issued under. The <i>shape</i> of a number is
/// the platform's (<see cref="RegistryNumbers"/>); which letter stands for a customer, a premise or
/// a service account is the Customers module's own business.
/// </summary>
public static class CustomerNumbers
{
    /// <summary>Prefix of a customer account number, e.g. <c>C-000001</c>.</summary>
    public const string CustomerPrefix = "C-";

    /// <summary>Prefix of a service location code, e.g. <c>L-000001</c>.</summary>
    public const string ServiceLocationPrefix = "L-";

    /// <summary>Prefix of a service account number, e.g. <c>A-000001</c>.</summary>
    public const string ServiceAccountPrefix = "A-";

    /// <summary>
    /// Prefix of a service application number, e.g. <c>AP-000001</c> (WP-2.18).
    /// </summary>
    /// <remarks>
    /// Two letters where the other three take one, so an applicant reading a number down the
    /// telephone cannot confuse the application they filed with the account it may turn into —
    /// which are different things with different numbers for the whole of the review.
    /// </remarks>
    public const string ServiceApplicationPrefix = "AP-";

    /// <summary>
    /// Prefix of a payment arrangement number, e.g. <c>PA-000001</c> (WP-2.20).
    /// </summary>
    /// <remarks>
    /// Two letters, like the application's, and for the same reason: a customer ringing up about
    /// "PA-000012" is asking about the promise they made, not about the account it was made against
    /// — and those are different things a rep has to be able to tell apart down a telephone.
    /// </remarks>
    public const string PaymentArrangementPrefix = "PA-";
}
