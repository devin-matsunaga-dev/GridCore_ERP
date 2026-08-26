using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Applications;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.UnitTests.Applications;

/// <summary>
/// The aggregate's own rules, with no database anywhere near them: the state machine, the checklist,
/// and the fixed reason list. These are the rules WP-2.18 exists to add, so they are tested where
/// they live rather than only through the service that calls them.
/// </summary>
public class ServiceApplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Wanted = new(2026, 9, 15);
    private static readonly RegistryActor Agent = new("auth0|cs-agent", "Ana Cruz");

    private static Customer ACustomer(CustomerClass customerClass = CustomerClass.Residential) =>
        Customer.Register("C-000001", "Sablan Family Residence", customerClass, Now);

    private static ServiceApplication AnApplication(
        CustomerClass customerClass = CustomerClass.Residential,
        ServiceType serviceType = ServiceType.Electricity) =>
        ServiceApplication.Submit(
            "AP-000001",
            ACustomer(customerClass),
            Guid.CreateVersion7(Now),
            serviceType,
            Wanted,
            notes: null,
            replacesApplicationId: null,
            Agent,
            Now);

    private static ServiceAccount AnAccount(ServiceApplication application) =>
        ServiceAccount.Open("A-000001", application.CustomerId, application.ServiceLocationId, application.ServiceType, Agent, Now);

    private static int _sequence;

    private static ServiceApplication WithDocuments(ServiceApplication application, params ApplicationDocumentKind[] kinds)
    {
        foreach (var kind in kinds)
        {
            application.Attach(
                Guid.CreateVersion7(Now.AddSeconds(Interlocked.Increment(ref _sequence))),
                kind,
                $"{kind}.pdf",
                "application/pdf",
                sizeInBytes: 2048,
                checksum: new string('a', 64),
                storageKey: $"service-applications/{application.Id:D}/{Guid.NewGuid():D}.pdf",
                Agent,
                Now);
        }

        return application;
    }

    /// <summary>An application under review with everything the checklist asks for.</summary>
    private static ServiceApplication AReadyApplication(CustomerClass customerClass = CustomerClass.Residential)
    {
        var application = AnApplication(customerClass);

        WithDocuments(application, [.. ServiceApplicationTypes.RequiredDocuments(application.Type)]);
        application.StartReview(Agent, Now);

        return application;
    }

    // ------------------------------------------------------------- submission

    [Fact]
    public void A_submitted_application_starts_in_the_queue_with_nobody_reviewing_it()
    {
        var application = AnApplication();

        Assert.Equal(ServiceApplicationStatus.Submitted, application.Status);
        Assert.True(application.IsOpen);
        Assert.Null(application.ReviewStartedAt);
        Assert.Null(application.DecidedAt);
        Assert.Null(application.ServiceAccountId);
        Assert.Equal(Wanted, application.RequestedOn);
        Assert.Equal(Agent.Id, application.SubmittedById);
    }

    [Fact]
    public void The_checklist_is_stamped_from_the_customers_class_and_not_asked_for()
    {
        // The rule ServiceApplicationTypes exists for: a shop cannot be held to the household's
        // checklist, because nobody gets to tick a box saying which one applies.
        Assert.Equal(ServiceApplicationType.ResidentialConnection, AnApplication(CustomerClass.Residential).Type);
        Assert.Equal(ServiceApplicationType.CommercialConnection, AnApplication(CustomerClass.Commercial).Type);
    }

    [Fact]
    public void A_commercial_application_asks_for_the_business_licence_a_household_does_not()
    {
        var household = AnApplication(CustomerClass.Residential);
        var business = AnApplication(CustomerClass.Commercial);

        Assert.DoesNotContain(ApplicationDocumentKind.BusinessLicence, household.MissingDocuments);
        Assert.Contains(ApplicationDocumentKind.BusinessLicence, business.MissingDocuments);
        Assert.Equal(2, household.Checklist.Count);
        Assert.Equal(3, business.Checklist.Count);
    }

    [Fact]
    public void An_application_for_a_service_GridCore_does_not_declare_is_refused() =>
        Assert.Throws<RegistryValidationException>(() => ServiceApplication.Submit(
            "AP-000001",
            ACustomer(),
            Guid.CreateVersion7(Now),
            (ServiceType)99,
            Wanted,
            notes: null,
            replacesApplicationId: null,
            Agent,
            Now));

    // -------------------------------------------------------------- checklist

    [Fact]
    public void An_attached_document_satisfies_its_checklist_line_and_nothing_else()
    {
        var application = WithDocuments(AnApplication(), ApplicationDocumentKind.PhotoId);

        var photoId = application.Checklist.Single(line => line.Kind is ApplicationDocumentKind.PhotoId);
        var occupancy = application.Checklist.Single(line => line.Kind is ApplicationDocumentKind.ProofOfOccupancy);

        Assert.True(photoId.IsSatisfied);
        Assert.NotNull(photoId.DocumentId);
        Assert.False(occupancy.IsSatisfied);
        Assert.Null(occupancy.DocumentId);
        Assert.False(application.IsDocumentationComplete);
    }

    [Fact]
    public void An_Other_document_never_closes_a_checklist_line()
    {
        // The escape hatch that could satisfy a requirement would be a checklist in name only.
        var application = WithDocuments(
            AnApplication(),
            ApplicationDocumentKind.Other,
            ApplicationDocumentKind.Other,
            ApplicationDocumentKind.Other);

        Assert.Equal(3, application.Documents.Count);
        Assert.False(application.IsDocumentationComplete);
        Assert.Equal(
            [ApplicationDocumentKind.PhotoId, ApplicationDocumentKind.ProofOfOccupancy],
            application.MissingDocuments);
    }

    [Fact]
    public void A_second_document_of_one_kind_supersedes_the_first_on_the_checklist()
    {
        // The register is append-only, so a re-scan is a second row; the checklist points at the one
        // the reviewer actually looked at.
        var application = WithDocuments(AnApplication(), ApplicationDocumentKind.PhotoId, ApplicationDocumentKind.PhotoId);

        var newest = application.Documents.OrderByDescending(document => document.Id).First();

        Assert.Equal(2, application.Documents.Count);
        Assert.Equal(newest.Id, application.Checklist.Single(line => line.Kind is ApplicationDocumentKind.PhotoId).DocumentId);
    }

    [Fact]
    public void A_document_of_a_kind_GridCore_does_not_declare_is_refused() =>
        Assert.Throws<RegistryValidationException>(() => AnApplication().Attach(
            Guid.CreateVersion7(Now),
            (ApplicationDocumentKind)77,
            "scan.pdf",
            "application/pdf",
            sizeInBytes: 10,
            checksum: new string('a', 64),
            storageKey: "service-applications/x/y.pdf",
            Agent,
            Now));

    // ---------------------------------------------------------- state machine

    [Fact]
    public void A_decision_cannot_be_taken_before_the_application_has_been_picked_up()
    {
        // "CUC reviews an application before it establishes an account" — the whole of WP-2.18, as a
        // state machine rather than a convention.
        var application = WithDocuments(AnApplication(), [.. ServiceApplicationTypes.RequiredDocuments(ServiceApplicationType.ResidentialConnection)]);

        Assert.Equal(
            [ServiceApplicationStatus.UnderReview, ServiceApplicationStatus.Withdrawn],
            application.AllowedTransitions);

        Assert.Throws<RegistryWorkflowException>(() =>
            application.Approve(AnAccount(application), ApplicationReasonCode.DocumentsVerified, null, Agent, Now));

        Assert.Throws<RegistryWorkflowException>(() =>
            application.Reject(ApplicationReasonCode.DocumentsIncomplete, null, Agent, Now));
    }

    [Fact]
    public void Approval_is_blocked_while_a_required_document_is_missing()
    {
        var application = WithDocuments(AnApplication(), ApplicationDocumentKind.PhotoId);
        application.StartReview(Agent, Now);

        var refusal = Assert.Throws<RegistryWorkflowException>(() =>
            application.Approve(AnAccount(application), ApplicationReasonCode.DocumentsVerified, null, Agent, Now));

        Assert.Contains(nameof(ApplicationDocumentKind.ProofOfOccupancy), refusal.Message, StringComparison.Ordinal);
        Assert.Equal(ServiceApplicationStatus.UnderReview, application.Status);
        Assert.Null(application.ServiceAccountId);
    }

    [Fact]
    public void An_incomplete_application_may_still_be_approved_by_exception_if_it_says_why()
    {
        // The escape hatch, and it is deliberately uncomfortable: ApprovedByException is one of the
        // two codes that must write a sentence. See ApplicationReasons.RequiresNotes.
        var application = WithDocuments(AnApplication(), ApplicationDocumentKind.PhotoId);
        application.StartReview(Agent, Now);
        var account = AnAccount(application);

        Assert.Throws<RegistryWorkflowException>(() =>
            application.Approve(account, ApplicationReasonCode.ApprovedByException, null, Agent, Now));

        Assert.Equal(ServiceApplicationStatus.UnderReview, application.Status);
    }

    [Fact]
    public void Approval_records_the_account_the_decision_and_who_took_it()
    {
        var application = AReadyApplication();
        var account = AnAccount(application);

        application.Approve(account, ApplicationReasonCode.DocumentsVerified, "Lease sighted at the counter.", Agent, Now);

        Assert.Equal(ServiceApplicationStatus.Approved, application.Status);
        Assert.False(application.IsOpen);
        Assert.Equal(account.Id, application.ServiceAccountId);
        Assert.Equal(ApplicationReasonCode.DocumentsVerified, application.DecisionReasonCode);
        Assert.Equal("Lease sighted at the counter.", application.DecisionNotes);
        Assert.Equal(Agent.Id, application.DecidedById);
        Assert.Equal(Now, application.DecidedAt);
        Assert.Empty(application.AllowedTransitions);
    }

    [Fact]
    public void A_rejected_application_can_never_be_approved()
    {
        // WP-2.18's verify list, in the aggregate: the way forward is a fresh submission, and the
        // refusal says so rather than only saying no.
        var application = AReadyApplication();

        application.Reject(ApplicationReasonCode.IdentityNotVerified, null, Agent, Now);

        var refusal = Assert.Throws<RegistryWorkflowException>(() =>
            application.Approve(AnAccount(application), ApplicationReasonCode.DocumentsVerified, null, Agent, Now));

        Assert.Contains("fresh application", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(ServiceApplicationStatus.Rejected, application.Status);
    }

    [Fact]
    public void A_decided_application_takes_no_further_documents()
    {
        var application = AReadyApplication();
        application.Withdraw(ApplicationReasonCode.ApplicantWithdrew, null, Agent, Now);

        Assert.Throws<RegistryWorkflowException>(() => WithDocuments(application, ApplicationDocumentKind.Other));
    }

    [Fact]
    public void An_application_may_be_withdrawn_straight_out_of_the_queue() =>
        // Unlike a decision: nobody has to pick a form up to record that the applicant changed their
        // mind about filing it.
        Assert.Contains(ServiceApplicationStatus.Withdrawn, AnApplication().AllowedTransitions);

    // ------------------------------------------------------------ reason codes

    [Theory]
    [InlineData(ApplicationReasonCode.DocumentsIncomplete)]
    [InlineData(ApplicationReasonCode.ApplicantWithdrew)]
    public void A_reason_code_that_does_not_fit_an_approval_is_refused(ApplicationReasonCode code)
    {
        var application = AReadyApplication();

        Assert.Throws<RegistryValidationException>(() =>
            application.Approve(AnAccount(application), code, "Because.", Agent, Now));
    }

    [Fact]
    public void A_rejection_may_not_be_recorded_as_a_withdrawal_reason()
    {
        // The list keeps the utility's own decision off the applicant's record.
        var application = AReadyApplication();

        Assert.Throws<RegistryValidationException>(() =>
            application.Reject(ApplicationReasonCode.ApplicantWithdrew, null, Agent, Now));
    }

    [Fact]
    public void The_escape_hatch_has_to_explain_itself()
    {
        var application = AReadyApplication();

        Assert.Throws<RegistryValidationException>(() =>
            application.Reject(ApplicationReasonCode.Other, "   ", Agent, Now));

        application.Reject(ApplicationReasonCode.Other, "Applicant is a minor.", Agent, Now);

        Assert.Equal("Applicant is a minor.", application.DecisionNotes);
    }

    [Fact]
    public void Every_reason_code_that_escapes_the_list_demands_notes() =>
        Assert.Equal(
            [ApplicationReasonCode.Other, ApplicationReasonCode.ApprovedByException],
            Enum.GetValues<ApplicationReasonCode>().Where(ApplicationReasons.RequiresNotes));

    [Fact]
    public void Every_decision_has_a_reason_list_and_every_list_offers_an_escape_hatch() =>
        Assert.All(
            new[] { ServiceApplicationStatus.Approved, ServiceApplicationStatus.Rejected, ServiceApplicationStatus.Withdrawn },
            status =>
            {
                var codes = ApplicationReasons.For(status);

                Assert.NotEmpty(codes);
                Assert.Contains(ApplicationReasonCode.Other, codes);
            });

    [Fact]
    public void Asking_for_the_reason_list_of_a_status_that_is_not_a_decision_throws() =>
        // Not an empty list: a caller asking this of Submitted has confused a hand-off with a
        // decision, and an empty answer would let them record a reason against neither.
        Assert.Throws<ArgumentOutOfRangeException>(() => ApplicationReasons.For(ServiceApplicationStatus.Submitted));

    [Fact]
    public void Every_application_type_declares_a_checklist() =>
        Assert.All(
            Enum.GetValues<ServiceApplicationType>(),
            type => Assert.NotEmpty(ServiceApplicationTypes.RequiredDocuments(type)));

    [Fact]
    public void Every_customer_class_maps_to_an_application_type() =>
        Assert.All(
            Enum.GetValues<CustomerClass>(),
            customerClass => Assert.True(Enum.IsDefined(ServiceApplicationTypes.For(customerClass))));
}
