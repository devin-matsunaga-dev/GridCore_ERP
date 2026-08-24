namespace GridCore.Modules.Finance.Features.ChartOfAccounts;

/// <summary>
/// The utility's chart of accounts: reference data the application needs, not demo data.
/// </summary>
/// <remarks>
/// <para>
/// Declared here and seeded from here by <see cref="AccountConfiguration"/>, so the list a test
/// asserts against and the list the migration writes are the same list. SPEC.md scopes this to
/// "enough double-entry to show operations flowing into finance": the accounts below are the ones
/// the two demonstration workflows actually post to, plus the handful a trial balance needs to look
/// like a trial balance. It is not a utility's real chart and is not meant to grow into one.
/// </para>
/// <para>
/// Adding an account means a new migration — the rows are seeded by one, and migrations are
/// append-only (invariant 7). Never change an existing code: it is the account's identity, and
/// <see cref="Platform.Data.ReferenceId"/> derives the row's primary key from it.
/// </para>
/// </remarks>
public static class ChartOfAccounts
{
    /// <summary>
    /// The instant this reference set was authored, and the timestamp component of every account
    /// id. Fixed forever: changing it changes every id, which to the database is a different chart.
    /// </summary>
    public static readonly DateTimeOffset AuthoredAt = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Every account, in code order.</summary>
    public static IReadOnlyList<Account> All { get; } =
    [
        Account.Reference(FinanceAccounts.Cash, "Cash at bank", AccountType.Asset),
        Account.Reference(FinanceAccounts.AccountsReceivable, "Accounts receivable", AccountType.Asset),
        Account.Reference(FinanceAccounts.Inventory, "Inventory", AccountType.Asset),
        Account.Reference(FinanceAccounts.UtilityPlant, "Utility plant in service", AccountType.Asset),

        Account.Reference(FinanceAccounts.AccountsPayable, "Accounts payable", AccountType.Liability),
        Account.Reference(FinanceAccounts.CustomerDeposits, "Customer deposits", AccountType.Liability),
        Account.Reference(FinanceAccounts.AccruedLiabilities, "Accrued liabilities", AccountType.Liability),

        Account.Reference(FinanceAccounts.RetainedEarnings, "Retained earnings", AccountType.Equity),

        Account.Reference(FinanceAccounts.Revenue, "Utility revenue", AccountType.Revenue),
        Account.Reference(FinanceAccounts.ServiceFeeRevenue, "Connection and service fees", AccountType.Revenue),
        Account.Reference(FinanceAccounts.LateFeeRevenue, "Late payment fees", AccountType.Revenue),

        Account.Reference(FinanceAccounts.PurchasedPower, "Purchased power", AccountType.Expense),
        Account.Reference(FinanceAccounts.MaintenanceExpense, "Maintenance and repairs", AccountType.Expense),
        Account.Reference(FinanceAccounts.MaterialsExpense, "Materials and supplies", AccountType.Expense),
        Account.Reference(FinanceAccounts.BadDebtExpense, "Bad debt expense", AccountType.Expense),
    ];

    private static readonly Dictionary<string, Account> ByCode =
        All.ToDictionary(account => account.Code, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="code"/> is an account the chart declares.</summary>
    public static bool Contains(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        return ByCode.ContainsKey(code);
    }

    /// <summary>The account with <paramref name="code"/>.</summary>
    /// <remarks>
    /// Throws rather than returning null: a posting to an account that does not exist is a defect in
    /// the mapping, and a journal entry is not the place to discover it.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No account has that code.</exception>
    public static Account Require(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        return ByCode.TryGetValue(code, out var account)
            ? account
            : throw new KeyNotFoundException(
                $"'{code}' is not an account in the chart of accounts. Accounts are reference data; "
                + "adding one is a migration, never a runtime insert.");
    }
}
