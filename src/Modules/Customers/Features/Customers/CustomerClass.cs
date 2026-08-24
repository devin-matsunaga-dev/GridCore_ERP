namespace GridCore.Modules.Customers.Features.Customers;

/// <summary>
/// What kind of customer this is. It decides which tariff applies and how a bill reads, so it is
/// part of the customer record rather than of the service account.
/// </summary>
public enum CustomerClass
{
    /// <summary>A household. Billed on the residential inclining-block tariff by default.</summary>
    Residential = 1,

    /// <summary>A business or institution. Billed on a commercial tariff.</summary>
    Commercial = 2,
}
