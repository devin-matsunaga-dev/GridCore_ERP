using GridCore.Modules.Customers.Features.Transitions;

namespace GridCore.Modules.Customers.UnitTests.Transitions;

/// <summary>
/// The fixed list itself: which reason codes fit which kind of transition, and which of them has to
/// explain itself. Pure — no host, no database — because this is a map, and a UI renders its selects
/// straight off it.
/// </summary>
public class TransitionReasonTests
{
    [Fact]
    public void Every_declared_kind_has_a_reason_list() =>
        // A kind added without one would be a transition nobody could ever record, and the failure
        // would surface as a 400 on a legal request rather than as the missing line it is.
        Assert.All(
            Enum.GetValues<AccountTransitionKind>(),
            kind => Assert.NotEmpty(TransitionReasons.For(kind)));

    [Fact]
    public void A_kind_GridCore_does_not_declare_has_no_reason_list() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TransitionReasons.For((AccountTransitionKind)99));

    [Fact]
    public void Every_kind_offers_the_escape_hatch() =>
        // A fixed list without one is a list somebody defeats by picking the nearest wrong code,
        // which is worse than an honest sentence: the wrong code is what a report would then add up.
        Assert.All(
            Enum.GetValues<AccountTransitionKind>(),
            kind => Assert.Contains(TransitionReasonCode.Other, TransitionReasons.For(kind)));

    [Fact]
    public void Only_the_escape_hatch_has_to_explain_itself() =>
        // Demanding a sentence beside "End of tenancy" would train a desk to type a full stop, which
        // is worse than nothing because it reads as an explanation.
        Assert.Equal(
            [TransitionReasonCode.Other],
            Enum.GetValues<TransitionReasonCode>().Where(TransitionReasons.RequiresNotes));

    [Fact]
    public void Every_declared_code_fits_at_least_one_kind() =>
        // A code nothing may be recorded under is a select option that always 400s. This is what
        // fails the day somebody adds a member and forgets the list it belongs on.
        Assert.All(
            Enum.GetValues<TransitionReasonCode>(),
            code => Assert.Contains(
                Enum.GetValues<AccountTransitionKind>(),
                kind => TransitionReasons.IsAllowed(kind, code)));

    [Fact]
    public void A_class_change_cannot_be_made_because_the_customer_asked() =>
        // A class is what the premise is used for, not what its occupant would prefer to be billed
        // as. A customer who asks to be re-classified is saying one of the three fixed things has
        // happened, and the record should say which.
        Assert.False(TransitionReasons.IsAllowed(AccountTransitionKind.ClassChanged, TransitionReasonCode.CustomerRequest));

    [Theory]
    [InlineData(TransitionReasonCode.PropertyDemolished)]
    [InlineData(TransitionReasonCode.Deceased)]
    [InlineData(TransitionReasonCode.EndOfTenancy)]
    public void A_transfer_cannot_be_made_for_a_reason_that_ends_a_supply_for_good(TransitionReasonCode code) =>
        // Offering these on a transfer would let a rep record a customer as having left while opening
        // them an account somewhere else — two claims that cannot both be true.
        Assert.False(TransitionReasons.IsAllowed(AccountTransitionKind.Transferred, code));

    [Fact]
    public void The_two_class_codes_say_which_way_the_premise_moved() =>
        Assert.All(
            [TransitionReasonCode.PremiseNowTrading, TransitionReasonCode.PremiseNowResidential],
            code => Assert.True(TransitionReasons.IsAllowed(AccountTransitionKind.ClassChanged, code)));

    [Fact]
    public void No_class_code_leaks_into_a_status_change() =>
        // The two lists exist because the vocabularies are genuinely different: "premise now trading"
        // explains a tariff, and says nothing about why somebody was suspended.
        Assert.All(
            [TransitionReasonCode.PremiseNowTrading, TransitionReasonCode.PremiseNowResidential, TransitionReasonCode.MisclassifiedAtIntake],
            code => Assert.False(TransitionReasons.IsAllowed(AccountTransitionKind.StatusChanged, code)));
}
