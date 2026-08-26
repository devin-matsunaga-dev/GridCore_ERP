using System.Text;
using GridCore.Contracts.Events;
using GridCore.Contracts.Providers;
using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Applications;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.UnitTests.Applications;

/// <summary>
/// WP-2.18 over the schema: what reaches the database, what the audit trail says about it, what the
/// object store ends up holding, and — for an approval — that the account and the deposit quote
/// arrive with it.
/// </summary>
/// <remarks>
/// <para>
/// The object store is a double (CONVENTIONS.md rule C): a checklist is a rule about rows, and
/// spinning a container to prove one would be exactly the mistake the ⚡ section is about. MinIO's
/// own round trip is one gate-tier test.
/// </para>
/// <para>
/// The account state machine is NOT re-tested here — <c>ServiceAccountTests</c> owns it. What is
/// tested is that approval goes <i>through</i> it: the account it opens is a real
/// <c>ServiceAccountService.OpenAsync</c> account, with the history line, the audit entry and the
/// <c>ServiceAccountOpened</c> event that come with one.
/// </para>
/// </remarks>
public class ServiceApplicationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 9, 30, 0, TimeSpan.Zero);

    private static CustomersTestHost NewHost(ICurrentUser? caller = null, TimeProvider? clock = null) =>
        new(clock ?? new FakeClock(Now), caller ?? new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

    private static Task<Customer> ACustomer(
        CustomersTestHost host,
        CustomerClass customerClass = CustomerClass.Residential,
        string name = "Sablan Family Residence") =>
        host.WithCustomersAsync(customers => customers.RegisterAsync(new RegisterCustomerInput(name, customerClass)));

    private static Task<ServiceLocation> APremise(CustomersTestHost host, string line1 = "128 As Nieves Road") =>
        host.WithLocationsAsync(locations => locations.RegisterAsync(
            new ServiceLocationInput(Address.Create(line1, "Songsong", "Rota", "MP", postalCode: "96951"), "House")));

    private static ReadOnlyMemory<byte> AScan(string content = "%PDF-1.7 scanned lease") => Encoding.UTF8.GetBytes(content);

    private static Task<ServiceApplication> Submit(
        CustomersTestHost host,
        Guid customerId,
        Guid premiseId,
        ServiceType serviceType = ServiceType.Electricity) =>
        host.WithApplicationsAsync(applications => applications.SubmitAsync(
            new SubmitApplicationInput(customerId, premiseId, serviceType, Notes: "Filed at the counter.")));

    private static Task<ApplicationDocument> Attach(
        CustomersTestHost host,
        Guid applicationId,
        ApplicationDocumentKind kind,
        string contentType = "application/pdf") =>
        host.WithApplicationsAsync(applications => applications.AttachDocumentAsync(
            applicationId,
            new AttachDocumentInput(kind, $"{kind}.pdf", contentType, AScan($"{kind} bytes"))));

    /// <summary>An application under review with every required document on it — ready to approve.</summary>
    private static async Task<(Customer Customer, ServiceLocation Premise, ServiceApplication Application)> AReadyApplication(
        CustomersTestHost host,
        CustomerClass customerClass = CustomerClass.Residential)
    {
        var customer = await ACustomer(host, customerClass);
        var premise = await APremise(host);
        var application = await Submit(host, customer.Id, premise.Id);

        foreach (var kind in ServiceApplicationTypes.RequiredDocuments(application.Type))
        {
            await Attach(host, application.Id, kind);
        }

        await host.WithApplicationsAsync(applications => applications.StartReviewAsync(application.Id));

        return (customer, premise, (await host.WithApplicationsAsync(applications => applications.FindAsync(application.Id)))!);
    }

    // ------------------------------------------------------------- submission

    [Fact]
    public async Task A_submitted_application_is_numbered_stored_and_audited()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var premise = await APremise(host);

        var application = await Submit(host, customer.Id, premise.Id);

        Assert.Equal("AP-000001", application.ApplicationNumber);

        await using var database = host.NewCustomersContext();
        var stored = await database.ServiceApplications.SingleAsync();

        Assert.Equal(ServiceApplicationStatus.Submitted, stored.Status);
        Assert.Equal(ServiceApplicationType.ResidentialConnection, stored.Type);
        Assert.Equal(ServiceType.Electricity, stored.ServiceType);

        await using var platform = host.NewPlatformContext();
        var entry = await platform.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.ServiceApplicationSubmitted);

        Assert.Equal(AuditEntityTypes.ServiceApplication, entry.EntityType);
        Assert.Equal(application.Id.ToString(), entry.EntityId);
    }

    [Fact]
    public async Task Filing_an_application_opens_no_account_and_publishes_nothing()
    {
        // The whole point of the package: WP-2.8's wizard opened an account the moment a form was
        // finished, and an application is what stands in front of that.
        using var host = NewHost();
        var customer = await ACustomer(host);
        var premise = await APremise(host);

        await Submit(host, customer.Id, premise.Id);

        await using var database = host.NewCustomersContext();

        Assert.Empty(await database.ServiceAccounts.ToListAsync());
        Assert.Empty(host.Events.Published.OfType<ServiceAccountOpened>());
    }

    [Fact]
    public async Task An_application_for_a_supply_already_taken_at_the_premise_is_refused()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var premise = await APremise(host);

        await host.WithAccountsAsync(accounts => accounts.OpenAsync(
            new OpenServiceAccountInput(customer.Id, premise.Id, ServiceType.Electricity)));

        await Assert.ThrowsAsync<RegistryWorkflowException>(() => Submit(host, customer.Id, premise.Id));
    }

    [Fact]
    public async Task A_second_open_application_for_the_same_supply_is_refused_and_a_different_supply_is_not()
    {
        // Two reps taking one telephone call is the failure; a house taking water as well as
        // electricity is not — the distinction WP-2.17 made on accounts, applied to applications.
        using var host = NewHost();
        var customer = await ACustomer(host);
        var premise = await APremise(host);

        await Submit(host, customer.Id, premise.Id);

        await Assert.ThrowsAsync<RegistryWorkflowException>(() => Submit(host, customer.Id, premise.Id));

        var water = await Submit(host, customer.Id, premise.Id, ServiceType.Water);

        Assert.Equal("AP-000002", water.ApplicationNumber);
    }

    [Fact]
    public async Task An_application_against_a_customer_or_premise_that_does_not_exist_is_a_404()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var premise = await APremise(host);

        await Assert.ThrowsAsync<CustomerNotFoundException>(() => Submit(host, Guid.CreateVersion7(), premise.Id));
        await Assert.ThrowsAsync<ServiceLocationNotFoundException>(() => Submit(host, customer.Id, Guid.CreateVersion7()));
    }

    // --------------------------------------------------------------- documents

    [Fact]
    public async Task An_uploaded_document_goes_to_the_store_first_and_the_row_records_where()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var premise = await APremise(host);
        var application = await Submit(host, customer.Id, premise.Id);

        var document = await Attach(host, application.Id, ApplicationDocumentKind.PhotoId);

        var stored = host.Documents.At(document.StorageKey);

        Assert.NotNull(stored);
        Assert.Equal(document.SizeInBytes, stored.SizeInBytes);
        Assert.Equal("application/pdf", document.ContentType);
        Assert.Equal(ServiceApplicationService.Checksum(stored.Content.Span), document.Checksum);
        Assert.StartsWith($"{ServiceApplicationService.StoragePrefix}/{application.Id:D}/", document.StorageKey, StringComparison.Ordinal);
        Assert.EndsWith(".pdf", document.StorageKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_storage_key_never_carries_the_uploaders_own_file_name()
    {
        // A key built from an attacker-controlled name is a request that gets to choose its own path
        // in the bucket. The name is kept on the row for a reviewer to read and for nothing else.
        using var host = NewHost();
        var customer = await ACustomer(host);
        var premise = await APremise(host);
        var application = await Submit(host, customer.Id, premise.Id);

        var document = await host.WithApplicationsAsync(applications => applications.AttachDocumentAsync(
            application.Id,
            new AttachDocumentInput(
                ApplicationDocumentKind.PhotoId,
                "../../../etc/passwd",
                "application/pdf",
                AScan())));

        Assert.Equal("../../../etc/passwd", document.FileName);
        Assert.DoesNotContain("..", document.StorageKey, StringComparison.Ordinal);
        Assert.Equal(
            ServiceApplicationService.StorageKeyFor(application.Id, document.Id, "application/pdf"),
            document.StorageKey);
    }

    [Theory]
    [InlineData("application/x-msdownload")]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("")]
    public async Task An_upload_of_a_disallowed_content_type_is_refused_and_stores_nothing(string contentType)
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var premise = await APremise(host);
        var application = await Submit(host, customer.Id, premise.Id);

        await Assert.ThrowsAsync<RegistryValidationException>(() =>
            Attach(host, application.Id, ApplicationDocumentKind.PhotoId, contentType));

        Assert.Equal(0, host.Documents.Count);

        await using var database = host.NewCustomersContext();

        Assert.Empty(await database.ApplicationDocuments.ToListAsync());
    }

    [Fact]
    public async Task A_content_type_carrying_parameters_is_still_accepted()
    {
        // Some browsers send "image/jpeg; charset=binary". Refusing a well-formed header for
        // carrying something the allow-list does not care about would be a bug at the counter.
        using var host = NewHost();
        var customer = await ACustomer(host);
        var premise = await APremise(host);
        var application = await Submit(host, customer.Id, premise.Id);

        var document = await Attach(host, application.Id, ApplicationDocumentKind.PhotoId, "image/jpeg; charset=binary");

        Assert.Equal("image/jpeg", document.ContentType);
    }

    [Fact]
    public async Task An_empty_upload_is_refused()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var premise = await APremise(host);
        var application = await Submit(host, customer.Id, premise.Id);

        await Assert.ThrowsAsync<RegistryValidationException>(() =>
            host.WithApplicationsAsync(applications => applications.AttachDocumentAsync(
                application.Id,
                new AttachDocumentInput(ApplicationDocumentKind.PhotoId, "empty.pdf", "application/pdf", ReadOnlyMemory<byte>.Empty))));
    }

    [Fact]
    public async Task A_store_that_refuses_the_write_leaves_no_row_behind()
    {
        // The failure path the ordering exists for: the bytes go first, so a store that is down
        // produces no record claiming evidence that cannot be produced.
        using var host = NewHost();
        var customer = await ACustomer(host);
        var premise = await APremise(host);
        var application = await Submit(host, customer.Id, premise.Id);

        host.Documents.FailNextPut = true;

        await Assert.ThrowsAsync<DocumentStoreException>(() => Attach(host, application.Id, ApplicationDocumentKind.PhotoId));

        await using var database = host.NewCustomersContext();

        Assert.Empty(await database.ApplicationDocuments.ToListAsync());
    }

    [Fact]
    public async Task Reading_a_document_back_needs_the_documents_permission_and_is_audited()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var premise = await APremise(host);
        var application = await Submit(host, customer.Id, premise.Id);
        var document = await Attach(host, application.Id, ApplicationDocumentKind.PhotoId);

        var clerk = FakeCurrentUser.Holding(Permissions.Customers.Read, Permissions.Customers.Write);

        await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.AsAsync(clerk, applications => applications.ReadDocumentAsync(application.Id, document.Id)));

        var content = await host.AsAsync(
            FakeCurrentUser.Holding(Permissions.Customers.Documents),
            applications => applications.ReadDocumentAsync(application.Id, document.Id));

        Assert.Equal(document.SizeInBytes, content.SizeInBytes);
        Assert.Equal(document.Checksum, ServiceApplicationService.Checksum(content.Content.Span));

        await using var platform = host.NewPlatformContext();

        Assert.Single(
            await platform.AuditEntries
                .Where(entry => entry.Action == AuditActions.ServiceApplicationDocumentRead)
                .ToListAsync());
    }

    [Fact]
    public async Task A_row_whose_object_has_gone_reads_as_a_missing_document_rather_than_a_failure()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var premise = await APremise(host);
        var application = await Submit(host, customer.Id, premise.Id);
        var document = await Attach(host, application.Id, ApplicationDocumentKind.PhotoId);

        host.Documents.Lose(document.StorageKey);

        await Assert.ThrowsAsync<ApplicationDocumentNotFoundException>(() =>
            host.WithApplicationsAsync(applications => applications.ReadDocumentAsync(application.Id, document.Id)));
    }

    // ---------------------------------------------------------------- approval

    [Fact]
    public async Task Approval_opens_the_account_and_quotes_what_the_deposit_now_asks()
    {
        using var host = NewHost();
        var (customer, premise, application) = await AReadyApplication(host);

        var approval = await host.WithApplicationsAsync(applications => applications.ApproveAsync(
            application.Id,
            new DecideApplicationInput(ApplicationReasonCode.DocumentsVerified, "Lease and ID sighted.")));

        Assert.Equal(ServiceApplicationStatus.Approved, approval.Application.Status);
        Assert.Equal(approval.Account.Id, approval.Application.ServiceAccountId);

        // A real ServiceAccountService account, not a row this service invented: Pending, numbered
        // from the shared series, and carrying its opening history line.
        Assert.Equal(ServiceAccountStatus.Pending, approval.Account.Status);
        Assert.Equal("A-000001", approval.Account.AccountNumber);
        Assert.Equal(customer.Id, approval.Account.CustomerId);
        Assert.Equal(premise.Id, approval.Account.ServiceLocationId);

        // The deposit is a QUOTE — the ledger is untouched, and the new account is in the figure.
        Assert.Single(approval.Deposit.Accounts);
        Assert.True(approval.Deposit.RequiredAmount > 0m);
        Assert.Equal(approval.Deposit.RequiredAmount, approval.Deposit.ShortfallAmount);

        await using var database = host.NewCustomersContext();

        Assert.Empty(await database.DepositEntries.ToListAsync());
    }

    [Fact]
    public async Task Approval_commits_the_account_the_decision_the_audit_trail_and_the_event_together()
    {
        using var host = NewHost();
        var (_, _, application) = await AReadyApplication(host);

        await host.WithApplicationsAsync(applications => applications.ApproveAsync(
            application.Id,
            new DecideApplicationInput(ApplicationReasonCode.DocumentsVerified)));

        await using var database = host.NewCustomersContext();
        var stored = await database.ServiceApplications.SingleAsync();
        var account = await database.ServiceAccounts.Include(candidate => candidate.History).SingleAsync();

        Assert.Equal(ServiceApplicationStatus.Approved, stored.Status);
        Assert.Equal(account.Id, stored.ServiceAccountId);
        Assert.Single(account.History);

        await using var platform = host.NewPlatformContext();
        var actions = await platform.AuditEntries.Select(entry => entry.Action).ToListAsync();

        Assert.Contains(AuditActions.ServiceApplicationApproved, actions);
        Assert.Contains(AuditActions.ServiceAccountOpened, actions);
        Assert.Single(host.Events.Published.OfType<ServiceAccountOpened>());
    }

    [Fact]
    public async Task Approval_is_blocked_while_a_required_document_is_missing_and_opens_no_account()
    {
        using var host = NewHost();
        var customer = await ACustomer(host, CustomerClass.Commercial, "Songsong Village Market");
        var premise = await APremise(host, "1 Market Row");
        var application = await Submit(host, customer.Id, premise.Id);

        await Attach(host, application.Id, ApplicationDocumentKind.PhotoId);
        await Attach(host, application.Id, ApplicationDocumentKind.ProofOfOccupancy);
        await host.WithApplicationsAsync(applications => applications.StartReviewAsync(application.Id));

        var refusal = await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithApplicationsAsync(applications => applications.ApproveAsync(
                application.Id,
                new DecideApplicationInput(ApplicationReasonCode.DocumentsVerified))));

        Assert.Contains(nameof(ApplicationDocumentKind.BusinessLicence), refusal.Message, StringComparison.Ordinal);

        await using var database = host.NewCustomersContext();

        // The whole transaction rolled back: no account, and the application is still under review.
        Assert.Empty(await database.ServiceAccounts.ToListAsync());
        Assert.Equal(ServiceApplicationStatus.UnderReview, (await database.ServiceApplications.SingleAsync()).Status);
    }

    [Fact]
    public async Task Approving_without_the_permission_is_refused_before_anything_is_written()
    {
        using var host = NewHost();
        var (_, _, application) = await AReadyApplication(host);

        var clerk = FakeCurrentUser.Holding(Permissions.Customers.Read, Permissions.Customers.Write);

        await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.AsAsync(clerk, applications => applications.ApproveAsync(
                application.Id,
                new DecideApplicationInput(ApplicationReasonCode.DocumentsVerified))));

        await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.AsAsync(clerk, applications => applications.RejectAsync(
                application.Id,
                new DecideApplicationInput(ApplicationReasonCode.DocumentsIncomplete))));

        await using var database = host.NewCustomersContext();

        Assert.Empty(await database.ServiceAccounts.ToListAsync());
        Assert.Equal(ServiceApplicationStatus.UnderReview, (await database.ServiceApplications.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_withdrawal_needs_no_approval_permission()
    {
        // The applicant's own act, relayed by the desk. Gating it would teach a desk to reject
        // instead, which puts the utility's decision on the applicant's record.
        using var host = NewHost();
        var (_, _, application) = await AReadyApplication(host);

        var clerk = FakeCurrentUser.Holding(Permissions.Customers.Read, Permissions.Customers.Write);

        var withdrawn = await host.AsAsync(clerk, applications => applications.WithdrawAsync(
            application.Id,
            new DecideApplicationInput(ApplicationReasonCode.ApplicantWithdrew)));

        Assert.Equal(ServiceApplicationStatus.Withdrawn, withdrawn.Status);
    }

    // ----------------------------------------------------------- resubmission

    [Fact]
    public async Task A_rejected_application_is_replaced_by_a_fresh_one_that_names_it_and_carries_no_evidence()
    {
        using var host = NewHost();
        var (customer, premise, application) = await AReadyApplication(host);

        await host.WithApplicationsAsync(applications => applications.RejectAsync(
            application.Id,
            new DecideApplicationInput(ApplicationReasonCode.OccupancyNotProven)));

        var fresh = await host.WithApplicationsAsync(applications => applications.ResubmitAsync(
            application.Id,
            new ResubmitApplicationInput(Notes: "Landlord's letter obtained.")));

        Assert.Equal("AP-000002", fresh.ApplicationNumber);
        Assert.Equal(application.Id, fresh.ReplacesApplicationId);
        Assert.Equal(customer.Id, fresh.CustomerId);
        Assert.Equal(premise.Id, fresh.ServiceLocationId);
        Assert.Equal(ServiceApplicationStatus.Submitted, fresh.Status);

        // The evidence does NOT carry over: a fresh review needs something new produced, or a
        // rejection could be overturned by pressing a button.
        Assert.Empty(fresh.Documents);
        Assert.False(fresh.IsDocumentationComplete);
    }

    [Fact]
    public async Task An_application_still_on_the_desk_cannot_be_resubmitted()
    {
        using var host = NewHost();
        var (_, _, application) = await AReadyApplication(host);

        await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithApplicationsAsync(applications => applications.ResubmitAsync(application.Id)));
    }

    // ------------------------------------------------------------------ reads

    [Fact]
    public async Task The_review_queue_returns_only_applications_still_on_the_desk()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var open = await Submit(host, customer.Id, (await APremise(host, "1 A Street")).Id);
        var decided = await Submit(host, customer.Id, (await APremise(host, "2 B Street")).Id);

        await host.WithApplicationsAsync(applications => applications.WithdrawAsync(
            decided.Id,
            new DecideApplicationInput(ApplicationReasonCode.ApplicantUnreachable)));

        var queue = await host.WithApplicationsAsync(applications =>
            applications.ListAsync(new ServiceApplicationQuery(OpenOnly: true)));

        Assert.Equal(open.Id, Assert.Single(queue).Id);
    }

    [Fact]
    public async Task A_listed_application_carries_the_checklist_the_queue_renders()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var premise = await APremise(host);
        var application = await Submit(host, customer.Id, premise.Id);

        await Attach(host, application.Id, ApplicationDocumentKind.PhotoId);

        var listed = Assert.Single(await host.WithApplicationsAsync(applications =>
            applications.ListAsync(new ServiceApplicationQuery(CustomerId: customer.Id))));

        Assert.Single(listed.Documents);
        Assert.Equal([ApplicationDocumentKind.ProofOfOccupancy], listed.MissingDocuments);
    }
}
