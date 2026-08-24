namespace GridCore.Modules.Finance.Features.ChartOfAccounts;

/// <summary>
/// The account codes the event seam posts to, named so a posting reads as accounting rather than as
/// a magic string.
/// </summary>
/// <remarks>
/// These are the codes; <see cref="ChartOfAccounts"/> is the accounts themselves, seeded by the
/// WP-0.8 migration. A fast test asserts every code here exists in the chart, which is what keeps
/// the two from drifting. WP-2.6 posts against the chart rows rather than the codes.
/// </remarks>
public static class FinanceAccounts
{
    /// <summary>Cash at bank.</summary>
    public const string Cash = "1000";

    /// <summary>Accounts receivable — what customers owe.</summary>
    public const string AccountsReceivable = "1100";

    /// <summary>Inventory held in warehouses.</summary>
    public const string Inventory = "1300";

    /// <summary>Utility plant in service — the asset registry's capitalised value.</summary>
    public const string UtilityPlant = "1400";

    /// <summary>Accounts payable — what we owe vendors.</summary>
    public const string AccountsPayable = "2000";

    /// <summary>Customer deposits held against service accounts.</summary>
    public const string CustomerDeposits = "2100";

    /// <summary>Accrued liabilities.</summary>
    public const string AccruedLiabilities = "2200";

    /// <summary>Retained earnings.</summary>
    public const string RetainedEarnings = "3000";

    /// <summary>Utility revenue — the consumption a bill charges for.</summary>
    public const string Revenue = "4000";

    /// <summary>Connection and service fees.</summary>
    public const string ServiceFeeRevenue = "4100";

    /// <summary>Late payment fees.</summary>
    public const string LateFeeRevenue = "4200";

    /// <summary>Purchased power — the cost of what is sold.</summary>
    public const string PurchasedPower = "5000";

    /// <summary>Maintenance and repairs — what completed work orders cost.</summary>
    public const string MaintenanceExpense = "5100";

    /// <summary>Materials and supplies consumed on work orders.</summary>
    public const string MaterialsExpense = "5200";

    /// <summary>Bad debt expense.</summary>
    public const string BadDebtExpense = "5900";
}
