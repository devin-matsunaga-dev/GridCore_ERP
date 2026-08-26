using System.Globalization;
using System.Security.Cryptography;
using GridCore.Contracts.Providers;
using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.Applications;

/// <summary>What a caller supplies to file an application.</summary>
/// <param name="CustomerId">Who is applying. A customer record exists first — an applicant is a prospect.</param>
/// <param name="ServiceLocationId">Where they want to be served.</param>
/// <param name="ServiceType">
/// Which supply. Required and not defaulted, deliberately: WP-2.17 made the same call on
/// <see cref="OpenServiceAccountInput"/>, and this is the form the account is opened from — a
/// default here would be GridCore deciding what somebody applied for, which then decides their
/// deposit and their tariff.
/// </param>
/// <param name="RequestedOn">The day supply is wanted from. Today when the caller does not say.</param>
/// <param name="Notes">What the applicant or the rep wrote.</param>
/// <param name="ReplacesApplicationId">The decided application this one is filed to replace, where there is one.</param>
public sealed record SubmitApplicationInput(
    Guid CustomerId,
    Guid ServiceLocationId,
    ServiceType ServiceType,
    DateOnly? RequestedOn = null,
    string? Notes = null,
    Guid? ReplacesApplicationId = null);

/// <summary>What a caller supplies to attach a document.</summary>
/// <param name="Kind">Which checklist line it answers.</param>
/// <param name="FileName">What the uploader called it.</param>
/// <param name="ContentType">The media type the upload declared.</param>
/// <param name="Content">The bytes.</param>
public sealed record AttachDocumentInput(
    ApplicationDocumentKind Kind,
    string FileName,
    string ContentType,
    ReadOnlyMemory<byte> Content);

/// <summary>
/// What a caller may change when filing a fresh application to replace a decided one.
/// </summary>
/// <remarks>
/// <b>The premise and the supply are deliberately absent.</b> A resubmission is the <i>same</i>
/// request with fresh evidence — that is what makes it a resubmission rather than a new
/// application — so the only things worth restating are when supply is wanted and what the desk
/// wants to note. Somebody applying for a different premise or a different supply is applying for
/// something else, and files it as such.
/// </remarks>
/// <param name="RequestedOn">A new date for supply, where the applicant has given one.</param>
/// <param name="Notes">What to record on the new application.</param>
public sealed record ResubmitApplicationInput(DateOnly? RequestedOn = null, string? Notes = null);

/// <summary>What a caller supplies to decide an application.</summary>
/// <param name="ReasonCode">Why, from the fixed list.</param>
/// <param name="Notes">What the reviewer wants to add. Required with the codes that escape the list.</param>
public sealed record DecideApplicationInput(ApplicationReasonCode ReasonCode, string? Notes = null);

/// <summary>How the application register is filtered.</summary>
/// <param name="Search">Matched against the application number, case-insensitively.</param>
/// <param name="CustomerId">Only applications from this customer — the 360 query.</param>
/// <param name="ServiceLocationId">Only applications for this premise.</param>
/// <param name="Status">Only applications in this status.</param>
/// <param name="ServiceType">Only applications for this supply.</param>
/// <param name="OpenOnly">Only applications still on the desk — the review queue.</param>
/// <param name="Limit">Most rows to return.</param>
public sealed record ServiceApplicationQuery(
    string? Search = null,
    Guid? CustomerId = null,
    Guid? ServiceLocationId = null,
    ServiceApplicationStatus? Status = null,
    ServiceType? ServiceType = null,
    bool? OpenOnly = null,
    int Limit = 50);

/// <summary>
/// What an approval produced: the decided application, the account it opened, and what the deposit
/// schedule now asks of the customer.
/// </summary>
/// <remarks>
/// <b>The deposit is quoted, never taken.</b> Approving an application is not a counter transaction
/// — nobody has handed over any money — so this is WP-2.17's re-assessment run against the customer
/// with their new account included, which is exactly the figure a rep reads down the telephone
/// before the supply is connected. Collecting it is <c>ICustomerDepositService</c>, gates on
/// <c>customers.deposit</c>, and happens when the customer pays.
/// </remarks>
/// <param name="Application">The application, now approved.</param>
/// <param name="Account">The service account it opened, Pending until somebody connects it.</param>
/// <param name="Deposit">What is held against what is now required, the new account included.</param>
public sealed record ApplicationApproval(
    ServiceApplication Application,
    ServiceAccount Account,
    DepositRequirement Deposit);

/// <summary>The service application register and its review workflow.</summary>
public interface IServiceApplicationService
{
    /// <summary>Files an application, issuing the next application number.</summary>
    /// <exception cref="CustomerNotFoundException">There is no such customer.</exception>
    /// <exception cref="ServiceLocationNotFoundException">There is no such premise.</exception>
    /// <exception cref="RegistryValidationException">The application is incomplete or names a service GridCore does not declare.</exception>
    /// <exception cref="RegistryWorkflowException">
    /// The customer may not take on new service, the premise is deactivated, the supply is already
    /// taken there, or an application for it is already open.
    /// </exception>
    Task<ServiceApplication> SubmitAsync(SubmitApplicationInput input, CancellationToken cancellationToken = default);

    /// <summary>Picks an application up for review — the move that makes a decision possible.</summary>
    /// <exception cref="ServiceApplicationNotFoundException">There is no such application.</exception>
    /// <exception cref="RegistryWorkflowException">It is not in the queue any more.</exception>
    Task<ServiceApplication> StartReviewAsync(Guid applicationId, CancellationToken cancellationToken = default);

    /// <summary>Stores a document in the object store and records it against the application.</summary>
    /// <exception cref="ServiceApplicationNotFoundException">There is no such application.</exception>
    /// <exception cref="RegistryValidationException">The content type is not accepted, the upload is empty, or it is too large.</exception>
    /// <exception cref="RegistryWorkflowException">The application has already been decided.</exception>
    /// <exception cref="DocumentStoreException">The object store refused the write.</exception>
    Task<ApplicationDocument> AttachDocumentAsync(Guid applicationId, AttachDocumentInput input, CancellationToken cancellationToken = default);

    /// <summary>Reads an attached document back out of the object store.</summary>
    /// <exception cref="RegistryPermissionException">The caller may not produce customer documents.</exception>
    /// <exception cref="ServiceApplicationNotFoundException">There is no such application.</exception>
    /// <exception cref="ApplicationDocumentNotFoundException">There is no such document on it, or the object has gone.</exception>
    Task<StoredDocumentContent> ReadDocumentAsync(Guid applicationId, Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>Approves the application, which is what opens the service account.</summary>
    /// <exception cref="RegistryPermissionException">The caller may not decide applications.</exception>
    /// <exception cref="ServiceApplicationNotFoundException">There is no such application.</exception>
    /// <exception cref="RegistryWorkflowException">It is not under review, or a required document is missing.</exception>
    /// <exception cref="RegistryValidationException">The reason code does not fit an approval, or needs notes it was not given.</exception>
    Task<ApplicationApproval> ApproveAsync(Guid applicationId, DecideApplicationInput input, CancellationToken cancellationToken = default);

    /// <summary>Refuses the application.</summary>
    /// <exception cref="RegistryPermissionException">The caller may not decide applications.</exception>
    /// <exception cref="ServiceApplicationNotFoundException">There is no such application.</exception>
    /// <exception cref="RegistryWorkflowException">It is not under review.</exception>
    Task<ServiceApplication> RejectAsync(Guid applicationId, DecideApplicationInput input, CancellationToken cancellationToken = default);

    /// <summary>Takes the application back on the applicant's behalf.</summary>
    /// <exception cref="ServiceApplicationNotFoundException">There is no such application.</exception>
    /// <exception cref="RegistryWorkflowException">It has already been decided.</exception>
    Task<ServiceApplication> WithdrawAsync(Guid applicationId, DecideApplicationInput input, CancellationToken cancellationToken = default);

    /// <summary>Files a fresh application to replace a decided one, copying what it asked for and none of its evidence.</summary>
    /// <exception cref="ServiceApplicationNotFoundException">There is no such application.</exception>
    /// <exception cref="RegistryWorkflowException">The application named is still open.</exception>
    Task<ServiceApplication> ResubmitAsync(Guid applicationId, ResubmitApplicationInput? overrides = null, CancellationToken cancellationToken = default);

    /// <summary>One application with its documents, or <see langword="null"/> if there is no such id.</summary>
    Task<ServiceApplication?> FindAsync(Guid applicationId, CancellationToken cancellationToken = default);

    /// <summary>The application register, newest first.</summary>
    Task<IReadOnlyList<ServiceApplication>> ListAsync(ServiceApplicationQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// The application register over the customers schema and the object store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Approval composes <see cref="IServiceAccountService"/> rather than reimplementing it.</b>
/// Opening an account issues a number, checks the customer may take on service, checks the premise
/// is free for this supply, writes the opening history line, audits it and publishes
/// <c>ServiceAccountOpened</c> — none of which belongs in a second copy here. What this service owns
/// is the two rules that make a reviewed application worth having: the checklist, and the state
/// machine that will not let a decision be taken off the queue. The nested
/// <see cref="IUnitOfWork.ExecuteAsync"/> joins this one, so the account, the decided application,
/// both audit entries and the outbox row commit together — WP-2.8's shape, unchanged.
/// </para>
/// <para>
/// <b>The bytes go to the store first and the row second, and that order is deliberate.</b> An
/// object with no row is litter in a bucket; a row with no object is a record claiming evidence that
/// cannot be produced, which is worse in exactly the situation this register exists for. So the
/// upload happens outside the transaction, and only a successful <c>PutAsync</c> is allowed to write
/// a row. A rolled-back transaction can therefore leave an orphaned object, which is the cheap
/// failure of the two and is why the seam has no delete for anyone to reach for.
/// </para>
/// <para>
/// <b>Two gates, and they are different jobs.</b> Filing an application and attaching evidence are
/// clerical, and travel on <c>customers.write</c> from the route. <i>Deciding</i> one is
/// <see cref="Permissions.Customers.Approve"/> and is demanded here as well as on the route, because
/// approval opens an account and assesses a deposit; and reading an uploaded identity document back
/// is <see cref="Permissions.Customers.Documents"/> — the same gate a statement leaving the building
/// carries (WP-2.14), for the same reason.
/// </para>
/// </remarks>
public sealed class ServiceApplicationService(
    CustomersDbContext database,
    IServiceAccountService accounts,
    IDepositReassessmentService deposits,
    IDocumentStore documents,
    IRegistryNumberGenerator numbers,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider clock) : IServiceApplicationService
{
    /// <summary>The largest page <see cref="ListAsync"/> will return, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// Prefix every application document is filed under in the object store. One folder for the
    /// feature, so a bucket shared with WP-4's reports stays legible to whoever has to look in it.
    /// </summary>
    public const string StoragePrefix = "service-applications";

    /// <summary>
    /// The customer statuses an application may be filed against — the same two
    /// <see cref="ServiceAccountService.OpenableCustomerStatuses"/> allows an account to be opened
    /// under, because an application that could never be approved is not worth taking.
    /// </summary>
    public static IReadOnlyList<CustomerStatus> ApplicableCustomerStatuses { get; } =
        ServiceAccountService.OpenableCustomerStatuses;

    /// <inheritdoc />
    public Task<ServiceApplication> SubmitAsync(SubmitApplicationInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                // FindAsync, not a query: a lookup by primary key checks the change tracker first,
                // so a caller that registered the applicant moments earlier in this same transaction
                // is not told there is no such customer. The call ServiceAccountService.OpenAsync
                // makes, for the same reason.
                var customer = await database.Customers.FindAsync([input.CustomerId], ct).ConfigureAwait(false)
                    ?? throw new CustomerNotFoundException(input.CustomerId);

                var location = await database.ServiceLocations.FindAsync([input.ServiceLocationId], ct).ConfigureAwait(false)
                    ?? throw new ServiceLocationNotFoundException(input.ServiceLocationId);

                if (!ApplicableCustomerStatuses.Contains(customer.Status))
                {
                    throw new RegistryWorkflowException(
                        $"Customer {customer.AccountNumber} is {customer.Status} and cannot apply for new service.");
                }

                if (!location.IsActive)
                {
                    throw new RegistryWorkflowException(
                        $"Service location {location.LocationCode} is deactivated and cannot be applied for.");
                }

                await RequireSupplyIsFreeAsync(location.LocationCode, input.ServiceLocationId, input.ServiceType, ct).ConfigureAwait(false);

                var applicationNumber = await numbers.NextServiceApplicationNumberAsync(ct).ConfigureAwait(false);

                // The unique index is the real guarantee; this turns the loser of a race into a 409
                // the caller can retry rather than a 500 out of the database.
                if (await database.ServiceApplications
                        .AnyAsync(existing => existing.ApplicationNumber == applicationNumber, ct).ConfigureAwait(false))
                {
                    throw new RegistryWorkflowException(
                        $"Application number {applicationNumber} has just been taken by another submission. Try again.");
                }

                var application = ServiceApplication.Submit(
                    applicationNumber,
                    customer,
                    input.ServiceLocationId,
                    input.ServiceType,
                    input.RequestedOn ?? DateOnly.FromDateTime(now.UtcDateTime),
                    input.Notes,
                    input.ReplacesApplicationId,
                    RegistryActor.Of(currentUser),
                    now);

                database.ServiceApplications.Add(application);

                audit.Record(
                    AuditActions.ServiceApplicationSubmitted,
                    AuditEntityTypes.ServiceApplication,
                    application.Id.ToString(),
                    before: null,
                    after: ServiceApplicationSnapshot.Of(application));

                // No event. Nothing outside this module acts on an application being filed — the
                // fact other modules care about is the account, and ServiceAccountOpened already
                // says that at approval. Publishing one nobody consumes would be an instruction
                // rather than a fact, the call WP-2.15 made about a status change.
                return application;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ServiceApplication> StartReviewAsync(Guid applicationId, CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();
                var application = await RequireAsync(applicationId, ct).ConfigureAwait(false);
                var before = ServiceApplicationSnapshot.Of(application);

                application.StartReview(RegistryActor.Of(currentUser), now);

                audit.Record(
                    AuditActions.ServiceApplicationReviewStarted,
                    AuditEntityTypes.ServiceApplication,
                    application.Id.ToString(),
                    before,
                    ServiceApplicationSnapshot.Of(application));

                return application;
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<ApplicationDocument> AttachDocumentAsync(
        Guid applicationId,
        AttachDocumentInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var contentType = RequireStorableUpload(input);

        // Read BEFORE the transaction, and refused here rather than after the object is written: a
        // decided application takes no more documents, and finding that out after the bucket has the
        // bytes would leave litter for a request that was never going to succeed. It is checked
        // again inside the aggregate, where the rule actually lives.
        var application = await FindAsync(applicationId, cancellationToken).ConfigureAwait(false)
            ?? throw new ServiceApplicationNotFoundException(applicationId);

        if (!application.IsOpen)
        {
            throw new RegistryWorkflowException(
                $"Application {application.ApplicationNumber} is {application.Status} and takes no further documents.");
        }

        // The id is minted first because the key is minted from it: one document, one object, and a
        // key that can be derived from nothing but the row it belongs to.
        var now = clock.GetUtcNow();
        var documentId = Guid.CreateVersion7(now);
        var storageKey = StorageKeyFor(applicationId, documentId, contentType);

        // Ours, over the bytes we were handed — not the store's. An integrity check computed by the
        // thing being checked proves nothing; this is the figure a later read back is set against.
        var checksum = Checksum(input.Content.Span);

        var stored = await documents.PutAsync(
            new DocumentUpload(storageKey, contentType, input.Content),
            cancellationToken).ConfigureAwait(false);

        return await unitOfWork.ExecuteAsync(
            async ct =>
            {
                // Re-read inside the transaction. The application was loaded untracked above, and a
                // row attached to a stale copy would be written against a snapshot of the register
                // rather than against the register.
                var tracked = await RequireAsync(applicationId, ct).ConfigureAwait(false);
                var before = ServiceApplicationSnapshot.Of(tracked);

                var document = tracked.Attach(
                    documentId,
                    input.Kind,
                    input.FileName,
                    contentType,
                    stored.SizeInBytes,
                    checksum,
                    storageKey,
                    RegistryActor.Of(currentUser),
                    now);

                audit.Record(
                    AuditActions.ServiceApplicationDocumentAttached,
                    AuditEntityTypes.ServiceApplication,
                    tracked.Id.ToString(),
                    before,
                    ServiceApplicationSnapshot.Of(tracked));

                return document;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StoredDocumentContent> ReadDocumentAsync(
        Guid applicationId,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        // The gate a statement carries (WP-2.14). A scanned passport is the most sensitive thing
        // this module holds, and reading one is the same act as producing a document: something
        // with a customer's affairs on it leaves the building.
        if (!currentUser.HasPermission(Permissions.Customers.Documents))
        {
            throw new RegistryPermissionException(
                $"Reading a document attached to an application requires the '{Permissions.Customers.Documents}' permission. "
                + "The checklist can be read without it; the scanned identity page behind it cannot.");
        }

        var application = await FindAsync(applicationId, cancellationToken).ConfigureAwait(false)
            ?? throw new ServiceApplicationNotFoundException(applicationId);

        var document = application.Documents.FirstOrDefault(candidate => candidate.Id == documentId)
            ?? throw new ApplicationDocumentNotFoundException(documentId);

        // Null is "the object has gone", which is a 404 rather than a 500: the row is the record and
        // the bucket is the storage, and a register that could not say the two had parted company
        // would be a register nobody could audit.
        var content = await documents.GetAsync(document.StorageKey, cancellationToken).ConfigureAwait(false)
            ?? throw new ApplicationDocumentNotFoundException(documentId);

        await audit.RecordAsync(
            AuditActions.ServiceApplicationDocumentRead,
            AuditEntityTypes.ServiceApplication,
            application.Id.ToString(),
            before: null,
            after: ApplicationDocumentSnapshot.Of(document),
            cancellationToken).ConfigureAwait(false);

        return content;
    }

    /// <inheritdoc />
    public Task<ApplicationApproval> ApproveAsync(
        Guid applicationId,
        DecideApplicationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        RequireDecisionPermission();

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();
                var application = await RequireAsync(applicationId, ct).ConfigureAwait(false);
                var before = ServiceApplicationSnapshot.Of(application);

                // Straight through WP-1.2's own service: the premise-occupancy rule, the customer
                // status rule, the account number, the opening history line, its audit entry and
                // ServiceAccountOpened all live there. An approval is that act with a reviewed
                // decision recorded beside it, not a second implementation of it.
                var account = await accounts.OpenAsync(
                    new OpenServiceAccountInput(
                        application.CustomerId,
                        application.ServiceLocationId,
                        application.ServiceType,
                        $"Application {application.ApplicationNumber} approved."),
                    ct).ConfigureAwait(false);

                application.Approve(account, input.ReasonCode, input.Notes, RegistryActor.Of(currentUser), now);

                audit.Record(
                    AuditActions.ServiceApplicationApproved,
                    AuditEntityTypes.ServiceApplication,
                    application.Id.ToString(),
                    before,
                    ServiceApplicationSnapshot.Of(application));

                // AFTER the account is opened, and the order is load-bearing — WP-2.17's rule, met
                // again. A deposit is a sum over the supplies a customer takes, so re-assessing
                // before the account exists would quote a figure that leaves out the very supply
                // being approved. The re-assessment reads the change tracker for exactly this case.
                var deposit = await deposits.ReassessAsync(application.CustomerId, ct).ConfigureAwait(false);

                return new ApplicationApproval(application, account, deposit);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ServiceApplication> RejectAsync(
        Guid applicationId,
        DecideApplicationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        RequireDecisionPermission();

        return DecideAsync(
            applicationId,
            AuditActions.ServiceApplicationRejected,
            (application, actor, now) => application.Reject(input.ReasonCode, input.Notes, actor, now),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ServiceApplication> WithdrawAsync(
        Guid applicationId,
        DecideApplicationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // NOT gated on customers.approve, deliberately. A withdrawal is the applicant's own act,
        // relayed by whoever is talking to them; it opens no account, assesses nothing and refuses
        // nobody. Making the desk fetch a supervisor to record "they changed their mind" would be a
        // gate that teaches people to reject instead.
        return DecideAsync(
            applicationId,
            AuditActions.ServiceApplicationWithdrawn,
            (application, actor, now) => application.Withdraw(input.ReasonCode, input.Notes, actor, now),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ServiceApplication> ResubmitAsync(
        Guid applicationId,
        ResubmitApplicationInput? overrides = null,
        CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                var previous = await RequireAsync(applicationId, ct).ConfigureAwait(false);

                if (previous.IsOpen)
                {
                    throw new RegistryWorkflowException(
                        $"Application {previous.ApplicationNumber} is {previous.Status} and has not been decided. "
                        + "A resubmission replaces a decision; decide this one or withdraw it first.");
                }

                // What was applied for carries over; the evidence does not. A fresh application is a
                // fresh review, and re-using the documents that were just refused would let a
                // rejection be overturned by pressing a button rather than by producing anything new.
                return await SubmitAsync(
                    new SubmitApplicationInput(
                        previous.CustomerId,
                        previous.ServiceLocationId,
                        previous.ServiceType,
                        overrides?.RequestedOn,
                        overrides?.Notes ?? previous.Notes,
                        ReplacesApplicationId: previous.Id),
                    ct).ConfigureAwait(false);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<ServiceApplication?> FindAsync(Guid applicationId, CancellationToken cancellationToken = default) =>
        database.ServiceApplications
            .AsNoTracking()
            .Include(application => application.Documents)
            .FirstOrDefaultAsync(application => application.Id == applicationId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ServiceApplication>> ListAsync(
        ServiceApplicationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Include, unlike the account list: a queue row shows how far along the checklist an
        // application is, and that is computed from the documents. Bounded by MaxPageSize and by
        // the handful of documents an application carries.
        var applications = database.ServiceApplications
            .AsNoTracking()
            .Include(application => application.Documents)
            .AsQueryable();

        if (query.CustomerId is { } customerId)
        {
            applications = applications.Where(application => application.CustomerId == customerId);
        }

        if (query.ServiceLocationId is { } locationId)
        {
            applications = applications.Where(application => application.ServiceLocationId == locationId);
        }

        // Matched against a non-nullable local: the column is stored by name, and EF cannot
        // translate a nullable-to-converted-value comparison.
        if (query.Status is { } status)
        {
            applications = applications.Where(application => application.Status == status);
        }

        if (query.ServiceType is { } serviceType)
        {
            applications = applications.Where(application => application.ServiceType == serviceType);
        }

        if (query.OpenOnly is true)
        {
            // Spelled out rather than expressed through ServiceApplicationTransitions.IsOpen: the
            // state machine is a method EF cannot translate, and a filter that fell back to
            // client-side evaluation would page the whole register into memory to hide two rows.
            applications = applications.Where(application =>
                application.Status == ServiceApplicationStatus.Submitted
                || application.Status == ServiceApplicationStatus.UnderReview);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Lower-cased on both sides rather than ILIKE, so the fast tier exercises the same SQL
            // shape production runs.
            var term = query.Search.Trim().ToLowerInvariant();

            applications = applications.Where(application => application.ApplicationNumber.ToLower().Contains(term));
        }

        // Ordered by key: ids are Guid v7, so the primary-key index already orders chronologically
        // on Postgres and on the fast tier's SQLite alike.
        return await applications
            .OrderByDescending(application => application.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Where a document is filed: the feature's folder, the application, then the document's own id
    /// with the extension its media type implies.
    /// </summary>
    /// <remarks>
    /// Built from ids alone — never from the uploader's file name, which is attacker-controlled text
    /// that would let a request choose its own path in the bucket. The name is kept on the row for
    /// the reviewer to read and is used for nothing else.
    /// </remarks>
    public static string StorageKeyFor(Guid applicationId, Guid documentId, string contentType) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{StoragePrefix}/{applicationId:D}/{documentId:D}{ApplicationDocuments.ExtensionFor(contentType)}");

    /// <summary>SHA-256 of <paramref name="content"/>, lower-case hex — what a document's row records.</summary>
    public static string Checksum(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private Task<ServiceApplication> DecideAsync(
        Guid applicationId,
        string action,
        Action<ServiceApplication, RegistryActor, DateTimeOffset> decide,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();
                var application = await RequireAsync(applicationId, ct).ConfigureAwait(false);
                var before = ServiceApplicationSnapshot.Of(application);

                decide(application, RegistryActor.Of(currentUser), now);

                audit.Record(
                    action,
                    AuditEntityTypes.ServiceApplication,
                    application.Id.ToString(),
                    before,
                    ServiceApplicationSnapshot.Of(application));

                return application;
            },
            cancellationToken);

    /// <summary>
    /// The tracked application, with its documents — the checklist is computed from them, so a
    /// decision taken against an application loaded without them would approve past a checklist it
    /// could not see.
    /// </summary>
    private async Task<ServiceApplication> RequireAsync(Guid applicationId, CancellationToken cancellationToken) =>
        // The change tracker first, for the reason ServiceAccountService checks Local: an
        // application filed moments earlier in this same transaction is invisible to any query until
        // it commits, and it is already carrying the documents the Include would have fetched.
        database.ServiceApplications.Local.FirstOrDefault(candidate => candidate.Id == applicationId)
        ?? await database.ServiceApplications
            .Include(candidate => candidate.Documents)
            .FirstOrDefaultAsync(candidate => candidate.Id == applicationId, cancellationToken).ConfigureAwait(false)
        ?? throw new ServiceApplicationNotFoundException(applicationId);

    private async Task RequireSupplyIsFreeAsync(
        string locationCode,
        Guid serviceLocationId,
        ServiceType serviceType,
        CancellationToken cancellationToken)
    {
        // Two questions, both about the same premise-and-supply pair, and both refused early. The
        // account check is the same one OpenAsync makes at approval — asked here so an applicant is
        // told at the counter rather than after a review — and the application check is what stops
        // two reps taking one telephone call and both filing.
        var taken = await database.ServiceAccounts
            .Where(account => account.ServiceLocationId == serviceLocationId)
            .Where(account => account.ServiceType == serviceType)
            .Where(account => account.Status != ServiceAccountStatus.Closed)
            .Select(account => account.AccountNumber)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (taken is not null)
        {
            throw new RegistryWorkflowException(
                $"Service location {locationCode} already takes {serviceType} on account {taken}. "
                + "Close that account before applying for the same service there.");
        }

        var pending = await database.ServiceApplications
            .Where(application => application.ServiceLocationId == serviceLocationId)
            .Where(application => application.ServiceType == serviceType)
            .Where(application =>
                application.Status == ServiceApplicationStatus.Submitted
                || application.Status == ServiceApplicationStatus.UnderReview)
            .Select(application => application.ApplicationNumber)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (pending is not null)
        {
            throw new RegistryWorkflowException(
                $"Application {pending} for {serviceType} at service location {locationCode} is still open. "
                + "Decide or withdraw it before filing another.");
        }
    }

    private static string RequireStorableUpload(AttachDocumentInput input)
    {
        if (input.Content.Length is 0)
        {
            throw new RegistryValidationException("An uploaded document is empty; there is nothing to file.");
        }

        if (input.Content.Length > ApplicationDocuments.MaxSizeInBytes)
        {
            throw new RegistryValidationException(
                $"An uploaded document may be at most {ApplicationDocuments.MaxSizeInBytes / (1024 * 1024)} MB; "
                + $"this one is {input.Content.Length} bytes.");
        }

        return ApplicationDocuments.Normalise(input.ContentType) is { } contentType && ApplicationDocuments.IsAllowed(contentType)
            ? contentType
            : throw new RegistryValidationException(
                $"'{input.ContentType}' is not a document type GridCore accepts. "
                + $"Scan it as one of: {string.Join(", ", ApplicationDocuments.AllowedContentTypes.Order(StringComparer.Ordinal))}.");
    }

    private void RequireDecisionPermission()
    {
        if (currentUser.HasPermission(Permissions.Customers.Approve))
        {
            return;
        }

        throw new RegistryPermissionException(
            $"Deciding a service application requires the '{Permissions.Customers.Approve}' permission. "
            + "An approval opens a service account and assesses a deposit, which is not the same job as taking the form.");
    }
}

/// <summary>
/// The before/after shape an application is audited as. A dedicated record rather than the entity,
/// so changing the entity later cannot silently change the meaning of historic entries.
/// </summary>
/// <remarks>
/// It carries <see cref="MissingDocuments"/> rather than the documents themselves. What an auditor
/// asks of an approval is "was the checklist satisfied when this was decided", and the answer is a
/// short list that is empty or is not — where the whole document set would put a growing blob in
/// every entry and still make the reader work the answer out.
/// </remarks>
/// <param name="Id">Which application.</param>
/// <param name="ApplicationNumber">Its number.</param>
/// <param name="CustomerId">Who applied.</param>
/// <param name="ServiceLocationId">Where.</param>
/// <param name="ServiceType">Which supply.</param>
/// <param name="Type">Which checklist it was held to.</param>
/// <param name="Status">Where the application stands.</param>
/// <param name="DecisionReasonCode">The code the decision was recorded under, where it has been decided.</param>
/// <param name="DecisionNotes">What the reviewer wrote beside it.</param>
/// <param name="DocumentCount">How many documents were attached.</param>
/// <param name="MissingDocuments">Which required documents had not arrived, by name.</param>
/// <param name="ServiceAccountId">The account approval opened.</param>
public sealed record ServiceApplicationSnapshot(
    Guid Id,
    string ApplicationNumber,
    Guid CustomerId,
    Guid ServiceLocationId,
    ServiceType ServiceType,
    ServiceApplicationType Type,
    ServiceApplicationStatus Status,
    ApplicationReasonCode? DecisionReasonCode,
    string? DecisionNotes,
    int DocumentCount,
    IReadOnlyList<string> MissingDocuments,
    Guid? ServiceAccountId)
{
    /// <summary>Takes a snapshot of <paramref name="application"/> as it stands.</summary>
    public static ServiceApplicationSnapshot Of(ServiceApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        return new ServiceApplicationSnapshot(
            application.Id,
            application.ApplicationNumber,
            application.CustomerId,
            application.ServiceLocationId,
            application.ServiceType,
            application.Type,
            application.Status,
            application.DecisionReasonCode,
            application.DecisionNotes,
            application.Documents.Count,
            [.. application.MissingDocuments.Select(kind => kind.ToString())],
            application.ServiceAccountId);
    }
}

/// <summary>
/// The shape a document read is audited as. No bytes and no key — the entry records that somebody
/// produced this document, and the checksum is what identifies which bytes they got.
/// </summary>
/// <param name="Id">Which document.</param>
/// <param name="ServiceApplicationId">The application it belongs to.</param>
/// <param name="Kind">What it is.</param>
/// <param name="FileName">What it was called.</param>
/// <param name="ContentType">The media type.</param>
/// <param name="SizeInBytes">How large it is.</param>
/// <param name="Checksum">The digest recorded when it was attached.</param>
public sealed record ApplicationDocumentSnapshot(
    Guid Id,
    Guid ServiceApplicationId,
    ApplicationDocumentKind Kind,
    string FileName,
    string ContentType,
    long SizeInBytes,
    string Checksum)
{
    /// <summary>Takes a snapshot of <paramref name="document"/>.</summary>
    public static ApplicationDocumentSnapshot Of(ApplicationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new ApplicationDocumentSnapshot(
            document.Id,
            document.ServiceApplicationId,
            document.Kind,
            document.FileName,
            document.ContentType,
            document.SizeInBytes,
            document.Checksum);
    }
}
