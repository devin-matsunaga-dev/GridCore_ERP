namespace GridCore.Modules.Customers.Features.Delinquency;

/// <summary>
/// The notices the utility serves on the way to cutting somebody off, in the order it serves them.
/// </summary>
/// <remarks>
/// <para>
/// <b>A fixed sequence rather than free text, because the record has to prove something.</b> CNMI
/// Public Law 16-17 and CUC's published regulations oblige the utility to give a customer an
/// opportunity to pay before disconnection, and "we wrote to them" is not evidence — a served
/// notice of a named type, on a stated day, is. A vocabulary a clerk could add to would be a
/// vocabulary a disconnection could be justified by after the fact.
/// </para>
/// <para>
/// Stored by name, so a notice served years ago does not depend on today's numbering.
/// </para>
/// </remarks>
public enum DunningNoticeType
{
    /// <summary>
    /// The first letter: the bill is late and here is what is owed. Carries no threat and starts no
    /// clock — it is the courtesy that makes the two below defensible.
    /// </summary>
    Reminder = 1,

    /// <summary>
    /// The account is formally delinquent. Says what happens next and by when, and is the step a
    /// payment arrangement (WP-2.20) is usually agreed at.
    /// </summary>
    Delinquency = 2,

    /// <summary>
    /// Notice that supply will be disconnected. <b>The one that matters legally</b>: serving it
    /// starts the statutory waiting period, and until that period has elapsed no account is
    /// eligible for disconnection however far behind it is.
    /// </summary>
    Disconnection = 3,
}
