using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.UnitTests.Registry;

/// <summary>
/// The three series this module issues. The shape itself — padding, parsing, ordering — belongs to
/// <c>RegistryNumbers</c> and is tested in Platform.UnitTests; what matters here is that the
/// customer, premise and account registries stay told apart.
/// </summary>
public class CustomerNumbersTests
{
    [Fact]
    public void The_three_series_are_told_apart_by_their_prefix() =>
        // Each registry issues its own series, so the same ordinal appears in all three. A number
        // that lost its prefix would parse as another registry's row.
        Assert.Equal(
            ["C-000001", "L-000001", "A-000001"],
            new[] { CustomerNumbers.CustomerPrefix, CustomerNumbers.ServiceLocationPrefix, CustomerNumbers.ServiceAccountPrefix }
                .Select(prefix => RegistryNumbers.Format(prefix, 1)));

    [Fact]
    public void A_number_from_another_series_does_not_continue_this_one() =>
        // Failure path: a location code read as the highest account number would restart the
        // account series on top of numbers already quoted to customers.
        Assert.Null(RegistryNumbers.OrdinalOf(CustomerNumbers.ServiceAccountPrefix, "C-000042"));
}
