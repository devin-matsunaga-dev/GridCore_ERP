namespace GridCore.Contracts.Services;

/// <summary>
/// A utility service a customer can take: what is supplied, not how it is measured or what it
/// costs.
/// </summary>
/// <remarks>
/// <para>
/// <b>In <c>Contracts</c> since WP-2.17, and that move is the package.</b> This began life inside
/// Billing as "what a rate plan charges for", which was true while a tariff was the only thing that
/// had an opinion. It is not any more: a service account declares which service it is, the deposit
/// schedule is keyed on it, and a published fee is quoted against it — three modules asking the
/// same question, which is exactly what <c>Contracts</c> is for.
/// </para>
/// <para>
/// <b>The one enum in <c>Contracts</c> that crosses a boundary as itself rather than by name.</b>
/// Every other cross-module DTO carries a module's enum as a <c>string</c> — see
/// <c>ServiceAccountSummary.Status</c> — because those enums belong to one module's lifecycle and
/// nothing outside it may take a dependency on today's member list. This one is different in kind:
/// it is declared here, owned by nobody, and a consumer that could not switch on it would be a
/// consumer parsing a string back into the type it was just handed.
/// </para>
/// <para>
/// Explicitly numbered, the rule every stored enum in GridCore follows — though both tables that
/// hold it store it by <i>name</i>, so a record read years from now depends on neither.
/// </para>
/// </remarks>
public enum ServiceType
{
    /// <summary>Electricity, metered in kWh. The only service the demonstration utility distributes.</summary>
    Electricity = 1,

    /// <summary>Potable water, metered in cubic metres.</summary>
    Water = 2,

    /// <summary>Gas, metered in therms.</summary>
    Gas = 3,

    /// <summary>
    /// Wastewater — sewerage collection and treatment.
    /// </summary>
    /// <remarks>
    /// <b>The first unmetered service, and the reason WP-2.17 needed one.</b> There is no wastewater
    /// meter: the utility charges a flat rate, or bills off the water consumed, and either way no
    /// device is fitted at the premise. That makes it a shape the rest of GridCore has to
    /// <i>refuse</i> — a meter cannot be fitted where the only service taken is unmetered, and a
    /// consumption bill cannot be raised for an account with nothing to consume — rather than one it
    /// merely never happens to produce. See <see cref="ServiceTypes.IsMetered"/>.
    /// </remarks>
    Wastewater = 4,
}

/// <summary>Facts about a <see cref="ServiceType"/> that every module agrees on.</summary>
/// <remarks>
/// Pure and static, deliberately: whether a service is measured by a device at the premise is a
/// property of the service itself, not a policy Billing or Metering each get to hold an opinion
/// about. Two modules disagreeing on that is how an unmetered account acquires a meter.
/// </remarks>
public static class ServiceTypes
{
    /// <summary>Every service GridCore declares, in enum order.</summary>
    public static IReadOnlyList<ServiceType> All { get; } = [.. Enum.GetValues<ServiceType>()];

    /// <summary>
    /// Whether a device at the premise measures what is consumed — and therefore whether a meter may
    /// be fitted, a reading taken, and a consumption bill raised.
    /// </summary>
    /// <remarks>
    /// A whitelist of the unmetered rather than of the metered, so a service added later is metered
    /// unless somebody says otherwise. That is the safer default of the two: a new metered service
    /// wrongly treated as unmetered would silently refuse every meter fitted to it, while the
    /// reverse merely allows a meter nobody was going to fit.
    /// </remarks>
    public static bool IsMetered(ServiceType serviceType) => serviceType is not ServiceType.Wastewater;

    /// <summary>Whether <paramref name="serviceType"/> is a service GridCore declares at all.</summary>
    public static bool IsDeclared(ServiceType serviceType) => Enum.IsDefined(serviceType);
}
