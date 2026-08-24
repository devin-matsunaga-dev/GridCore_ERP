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
}
