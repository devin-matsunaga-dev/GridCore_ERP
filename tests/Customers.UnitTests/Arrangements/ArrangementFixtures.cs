using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;

namespace GridCore.Modules.Customers.UnitTests.Arrangements;

/// <summary>
/// The two rows an arrangement is built against, made by hand through the real factories.
/// </summary>
/// <remarks>
/// <b>Not a database.</b> WORK_PACKAGES.md's arrangement rules are about a schedule and a state
/// machine, and CONVENTIONS.md rule C says a behaviour that can be tested without infrastructure
/// must be — so the aggregate's own tests build their customer and their account in memory, and only
/// the service tests, which are about what commits together, take a host.
/// </remarks>
internal static class ArrangementFixtures
{
    private static readonly DateTimeOffset Opened = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A customer of <paramref name="customerClass"/>.</summary>
    public static Customer Customer(CustomerClass customerClass = CustomerClass.Residential) =>
        GridCore.Modules.Customers.Features.Customers.Customer.Register(
            "C-000001",
            "Rosa Manglona",
            customerClass,
            Opened);

    /// <summary>An open electric account.</summary>
    public static ServiceAccount Account() =>
        ServiceAccount.Open(
            "A-000001",
            Guid.CreateVersion7(Opened),
            Guid.CreateVersion7(Opened),
            ServiceType.Electricity,
            RegistryActor.Of(SystemUser.Instance),
            Opened);
}
