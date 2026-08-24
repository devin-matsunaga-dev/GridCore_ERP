using GridCore.Platform.Data;

namespace GridCore.Modules.Finance.Features.ChartOfAccounts;

/// <summary>The five kinds of account double-entry recognises.</summary>
public enum AccountType
{
    /// <summary>What the utility owns — cash, receivables, inventory, plant.</summary>
    Asset,

    /// <summary>What the utility owes — payables, customer deposits, accruals.</summary>
    Liability,

    /// <summary>The owners' residual interest.</summary>
    Equity,

    /// <summary>What the utility earns — service revenue and fees.</summary>
    Revenue,

    /// <summary>What the utility spends earning it.</summary>
    Expense,
}

/// <summary>Which side of an account its balance normally sits on.</summary>
public enum NormalBalance
{
    /// <summary>Increased by debits: assets and expenses.</summary>
    Debit,

    /// <summary>Increased by credits: liabilities, equity and revenue.</summary>
    Credit,
}

/// <summary>
/// One account in the chart of accounts — reference data, shipped by migration because a ledger
/// with nothing to post to is not a working application (ARCHITECTURE.md invariant 8).
/// </summary>
/// <remarks>
/// There are no balances here. The ledger is append-only (invariant 3), so an account's balance is
/// the sum of its journal lines and never a column that could drift from them; WP-2.6 adds the
/// entries and the trial balance that reads them.
/// </remarks>
public sealed class Account
{
    /// <summary>Longest account code stored.</summary>
    public const int CodeLength = 16;

    /// <summary>Longest account name stored.</summary>
    public const int NameLength = 128;

    private Account()
    {
        // EF materialisation.
        Code = string.Empty;
        Name = string.Empty;
    }

    /// <summary>Identifier of this account.</summary>
    public Guid Id { get; private init; }

    /// <summary>The account code a person quotes, e.g. <c>1100</c>. Unique across the chart.</summary>
    public string Code { get; private init; }

    /// <summary>What the account is called on a report.</summary>
    public string Name { get; private init; }

    /// <summary>Which of the five kinds this is.</summary>
    public AccountType Type { get; private init; }

    /// <summary>
    /// The side this account's balance normally sits on. Derived from <see cref="Type"/> rather
    /// than stored: a column could disagree with the account's own kind, and nothing would notice
    /// until a trial balance came out backwards.
    /// </summary>
    public NormalBalance NormalBalance => NormalBalanceOf(Type);

    /// <summary>Which side <paramref name="type"/> is normally increased on.</summary>
    public static NormalBalance NormalBalanceOf(AccountType type) => type switch
    {
        AccountType.Asset or AccountType.Expense => NormalBalance.Debit,
        AccountType.Liability or AccountType.Equity or AccountType.Revenue => NormalBalance.Credit,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown account type."),
    };

    /// <summary>
    /// Builds a reference account. The id is derived from the code so the migration seeds the same
    /// rows every time it is generated — see <see cref="ReferenceId"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The code or name is missing or too long.</exception>
    public static Account Reference(string code, string name, AccountType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(code.Length, CodeLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, NameLength);

        // Validates the type as a side effect: an account whose kind has no normal balance would
        // be unusable in a trial balance, and the chart is built once, at model-build time.
        _ = NormalBalanceOf(type);

        return new Account
        {
            Id = ReferenceId.For(ChartOfAccounts.AuthoredAt, code),
            Code = code,
            Name = name,
            Type = type,
        };
    }
}
