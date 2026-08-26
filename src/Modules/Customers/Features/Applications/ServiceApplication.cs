using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.Features.Applications;

/// <summary>
/// One line of an application's required-document checklist: what is asked for, and whether it has
/// arrived.
/// </summary>
/// <param name="Kind">The document asked for.</param>
/// <param name="IsSatisfied">Whether at least one document of that kind has been attached.</param>
/// <param name="DocumentId">The newest document that answers it, or <see langword="null"/>.</param>
/// <param name="UploadedAt">When that document arrived.</param>
public sealed record ApplicationChecklistLine(
    ApplicationDocumentKind Kind,
    bool IsSatisfied,
    Guid? DocumentId,
    DateTimeOffset? UploadedAt);

/// <summary>
/// A request for service, reviewed before it becomes an account. The thing WORK_PACKAGES.md
/// WP-2.18 says CUC has and GridCore did not: WP-2.8's wizard opened an account the moment somebody
/// finished a form, and there was nowhere to hold "we have the form, we are still waiting on the
/// lease".
/// </summary>
/// <remarks>
/// <para>
/// <b>Approval is what opens the account, and the account is the only thing approval creates.</b>
/// The customer and the premise both exist before an application is filed — an applicant is a
/// customer record in <see cref="CustomerStatus.Prospect"/>, and a premise is registered by
/// whoever surveyed it — so the reviewed decision is about the <i>supply</i>, which is exactly what
/// a <see cref="ServiceAccount"/> is. <see cref="ServiceAccountId"/> is the link, written once at
/// approval and never afterwards.
/// </para>
/// <para>
/// <b>The checklist is a property of the type, not a set of rows somebody ticks.</b>
/// <see cref="Checklist"/> is computed from <see cref="ServiceApplicationTypes.RequiredDocuments"/>
/// against the documents actually attached, so it cannot say an application is complete when it is
/// not — there is no state to fall out of step. <see cref="MissingDocuments"/> is what
/// <see cref="Approve"/> refuses on.
/// </para>
/// <para>
/// <b>Terminal is terminal.</b> A rejected or withdrawn application is never reopened; the way
/// forward is a fresh application naming it in <see cref="ReplacesApplicationId"/>. That is WP-2.4's
/// rule about corrections, one module over: what was decided keeps meaning what it said, and the
/// evidence behind the second decision is the second application's own.
/// </para>
/// </remarks>
public sealed class ServiceApplication
{
    /// <summary>Longest stored form of a status, type or reason-code name.</summary>
    public const int EnumNameLength = 64;

    /// <summary>Longest free text recorded on a submission or a decision.</summary>
    public const int NotesLength = 1024;

    private readonly List<ApplicationDocument> _documents = [];

    private ServiceApplication()
    {
        // EF materialisation.
        ApplicationNumber = string.Empty;
        SubmittedById = string.Empty;
    }

    /// <summary>Identifier of this application. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>The number quoted to the applicant, e.g. <c>AP-000001</c>. Unique across applications.</summary>
    public string ApplicationNumber { get; private init; }

    /// <summary>Who is applying. A customer record already exists — an applicant is a prospect.</summary>
    public Guid CustomerId { get; private init; }

    /// <summary>Where they want to be served.</summary>
    public Guid ServiceLocationId { get; private init; }

    /// <summary>
    /// Which supply they are applying for. One application is one supply: a household wanting
    /// electricity and water files two, because they are two accounts, two deposits and — the day
    /// the utility says no to one of them — two decisions.
    /// </summary>
    public ServiceType ServiceType { get; private init; }

    /// <summary>
    /// Which checklist this application was held to, stamped from the customer's class at
    /// submission. See <see cref="ServiceApplicationTypes.For"/> for why it is derived rather than
    /// asked for.
    /// </summary>
    public ServiceApplicationType Type { get; private init; }

    /// <summary>Where the application stands.</summary>
    public ServiceApplicationStatus Status { get; private set; }

    /// <summary>The day the applicant would like supply from. A wish, not a promise — the connection is a work order.</summary>
    public DateOnly RequestedOn { get; private init; }

    /// <summary>What the applicant or the rep wrote when it was filed.</summary>
    public string? Notes { get; private init; }

    /// <summary>When it was filed.</summary>
    public DateTimeOffset SubmittedAt { get; private init; }

    /// <summary>Subject id of whoever filed it.</summary>
    public string SubmittedById { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? SubmittedByName { get; private init; }

    /// <summary>When a reviewer picked it up, or <see langword="null"/> while it is still in the queue.</summary>
    public DateTimeOffset? ReviewStartedAt { get; private set; }

    /// <summary>Subject id of the reviewer who picked it up.</summary>
    public string? ReviewerId { get; private set; }

    /// <summary>Their display name at the time.</summary>
    public string? ReviewerName { get; private set; }

    /// <summary>When it was decided, or <see langword="null"/> while it is open.</summary>
    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>Subject id of whoever decided it.</summary>
    public string? DecidedById { get; private set; }

    /// <summary>Their display name at the time.</summary>
    public string? DecidedByName { get; private set; }

    /// <summary>The fixed-list code the decision was recorded under. Set on every terminal move and only there.</summary>
    public ApplicationReasonCode? DecisionReasonCode { get; private set; }

    /// <summary>What the reviewer wrote beside the code.</summary>
    public string? DecisionNotes { get; private set; }

    /// <summary>The service account approval opened, or <see langword="null"/> on anything not approved.</summary>
    public Guid? ServiceAccountId { get; private set; }

    /// <summary>
    /// The rejected or withdrawn application this one was filed to replace, where it was. The
    /// provenance that makes "a rejected application cannot be approved without a fresh submission"
    /// visible in the data rather than only enforced in code.
    /// </summary>
    public Guid? ReplacesApplicationId { get; private init; }

    /// <summary>Every document attached, oldest first.</summary>
    public IReadOnlyList<ApplicationDocument> Documents => _documents;

    /// <summary>The statuses this application may move to, for rendering decision buttons.</summary>
    public IReadOnlyList<ServiceApplicationStatus> AllowedTransitions => ServiceApplicationTransitions.AllowedFrom(Status);

    /// <summary>Whether it is still on the desk.</summary>
    public bool IsOpen => ServiceApplicationTransitions.IsOpen(Status);

    /// <summary>
    /// What this type of application must carry, against what has arrived — in the order the
    /// checklist declares, so a screen renders the same rows every time.
    /// </summary>
    public IReadOnlyList<ApplicationChecklistLine> Checklist =>
    [
        .. ServiceApplicationTypes.RequiredDocuments(Type).Select(kind =>
        {
            // Newest wins. The register is append-only, so a re-scan of an unreadable page is a
            // second row of the same kind, and it is the later one the reviewer looked at.
            var newest = _documents
                .Where(document => document.Kind == kind)
                .OrderByDescending(document => document.UploadedAt)
                .ThenByDescending(document => document.Id)
                .FirstOrDefault();

            return new ApplicationChecklistLine(kind, newest is not null, newest?.Id, newest?.UploadedAt);
        }),
    ];

    /// <summary>The required documents that have not arrived. Empty is what <see cref="Approve"/> demands.</summary>
    public IReadOnlyList<ApplicationDocumentKind> MissingDocuments =>
        [.. Checklist.Where(line => !line.IsSatisfied).Select(line => line.Kind)];

    /// <summary>Whether every required document has arrived.</summary>
    public bool IsDocumentationComplete => MissingDocuments.Count is 0;

    /// <summary>
    /// Files an application under a number the caller has already reserved — see
    /// <see cref="IRegistryNumberGenerator"/>. It starts <see cref="ServiceApplicationStatus.Submitted"/>:
    /// filing a form and having somebody read it are two different acts.
    /// </summary>
    /// <exception cref="RegistryValidationException">
    /// The number is missing, an id is empty, the service is not one GridCore declares, or the
    /// customer's class maps to no checklist.
    /// </exception>
    public static ServiceApplication Submit(
        string applicationNumber,
        Customer customer,
        Guid serviceLocationId,
        ServiceType serviceType,
        DateOnly requestedOn,
        string? notes,
        Guid? replacesApplicationId,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(actor);

        var number = RegistryText.Clean(applicationNumber, RegistryNumbers.MaxLength)
            ?? throw new RegistryValidationException("An application must be filed under a number.");

        if (serviceLocationId == Guid.Empty)
        {
            throw new RegistryValidationException("An application must name the premise service is wanted at.");
        }

        if (!ServiceTypes.IsDeclared(serviceType))
        {
            throw new RegistryValidationException($"'{serviceType}' is not a {nameof(ServiceType)} GridCore declares.");
        }

        return new ServiceApplication
        {
            Id = Guid.CreateVersion7(now),
            ApplicationNumber = number,
            CustomerId = customer.Id,
            ServiceLocationId = serviceLocationId,
            ServiceType = serviceType,

            // Stamped, not asked for. See ServiceApplicationTypes — a commercial customer cannot be
            // held to the household's checklist by ticking a box on the form.
            Type = ServiceApplicationTypes.For(customer.Class),
            Status = ServiceApplicationStatus.Submitted,
            RequestedOn = requestedOn,
            Notes = RegistryText.Clean(notes, NotesLength),
            SubmittedAt = now,
            SubmittedById = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new RegistryValidationException("An application must name who filed it."),
            SubmittedByName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
            ReplacesApplicationId = replacesApplicationId,
        };
    }

    /// <summary>
    /// A reviewer picks the application up. The move that makes a decision possible, and the reason
    /// the queue can say who is dealing with what.
    /// </summary>
    /// <exception cref="RegistryWorkflowException">It is not in the queue any more.</exception>
    public void StartReview(RegistryActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        RequireTransitionTo(ServiceApplicationStatus.UnderReview);

        Status = ServiceApplicationStatus.UnderReview;
        ReviewStartedAt = now;
        ReviewerId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength);
        ReviewerName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength);
    }

    /// <summary>
    /// Records a document that has already been put in the object store.
    /// </summary>
    /// <remarks>
    /// Attachable while the application is open and not after it: a decided application's evidence
    /// is what the decision was taken on, and letting a document arrive afterwards would let the
    /// record be improved in hindsight.
    /// </remarks>
    /// <exception cref="RegistryWorkflowException">The application has already been decided.</exception>
    /// <exception cref="RegistryValidationException">The document is not one GridCore can record.</exception>
    public ApplicationDocument Attach(
        Guid documentId,
        ApplicationDocumentKind kind,
        string fileName,
        string contentType,
        long sizeInBytes,
        string checksum,
        string storageKey,
        RegistryActor actor,
        DateTimeOffset now)
    {
        if (!IsOpen)
        {
            throw new RegistryWorkflowException(
                $"Application {ApplicationNumber} is {Status} and takes no further documents. "
                + "What it was decided on is what it was decided on; a later document belongs to a fresh application.");
        }

        var document = ApplicationDocument.For(
            Id,
            documentId,
            kind,
            fileName,
            contentType,
            sizeInBytes,
            checksum,
            storageKey,
            actor,
            now);

        _documents.Add(document);

        return document;
    }

    /// <summary>
    /// Approves the application against the account it opened.
    /// </summary>
    /// <remarks>
    /// The account is passed in rather than created here: opening one issues a number, checks the
    /// premise is free for this supply and publishes <c>ServiceAccountOpened</c>, all of which is
    /// <c>IServiceAccountService</c>'s and none of which an aggregate should reimplement. What this
    /// method owns is the two rules that make approval mean something — the checklist and the state
    /// machine.
    /// </remarks>
    /// <exception cref="RegistryWorkflowException">
    /// It is not under review, or a required document is still missing.
    /// </exception>
    /// <exception cref="RegistryValidationException">The reason code does not fit an approval, or needs notes it was not given.</exception>
    public void Approve(
        ServiceAccount account,
        ApplicationReasonCode reasonCode,
        string? notes,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);

        RequireTransitionTo(ServiceApplicationStatus.Approved);

        if (!IsDocumentationComplete)
        {
            throw new RegistryWorkflowException(
                $"Application {ApplicationNumber} is still missing {string.Join(", ", MissingDocuments)}. "
                + "A checklist that can be approved past is not a checklist; attach the document or record the "
                + $"approval under '{ApplicationReasonCode.ApprovedByException}', which has to say why.");
        }

        Decide(ServiceApplicationStatus.Approved, reasonCode, notes, actor, now);

        ServiceAccountId = account.Id;
    }

    /// <summary>Refuses the application, with a reason code from the fixed list.</summary>
    /// <exception cref="RegistryWorkflowException">It is not under review.</exception>
    /// <exception cref="RegistryValidationException">The reason code does not fit a rejection, or needs notes it was not given.</exception>
    public void Reject(ApplicationReasonCode reasonCode, string? notes, RegistryActor actor, DateTimeOffset now)
    {
        RequireTransitionTo(ServiceApplicationStatus.Rejected);

        Decide(ServiceApplicationStatus.Rejected, reasonCode, notes, actor, now);
    }

    /// <summary>Takes the application back, with a reason code from the fixed list.</summary>
    /// <exception cref="RegistryWorkflowException">It has already been decided.</exception>
    /// <exception cref="RegistryValidationException">The reason code does not fit a withdrawal, or needs notes it was not given.</exception>
    public void Withdraw(ApplicationReasonCode reasonCode, string? notes, RegistryActor actor, DateTimeOffset now)
    {
        RequireTransitionTo(ServiceApplicationStatus.Withdrawn);

        Decide(ServiceApplicationStatus.Withdrawn, reasonCode, notes, actor, now);
    }

    private void RequireTransitionTo(ServiceApplicationStatus next)
    {
        if (ServiceApplicationTransitions.IsAllowed(Status, next))
        {
            return;
        }

        // Named, so the caller is told what to do rather than only what they may not. The
        // resubmission sentence is the single rule of this package a client is most likely to meet
        // by trying it — see RegistryProblems.ApplicationIsDecided for the same words on a read path.
        var remedy = ServiceApplicationTransitions.IsTerminal(Status)
            ? " A decided application never moves again; file a fresh application naming this one."
            : $" Allowed from {Status}: {string.Join(", ", AllowedTransitions)}.";

        throw new RegistryWorkflowException($"Application {ApplicationNumber} is {Status} and cannot become {next}.{remedy}");
    }

    private void Decide(
        ServiceApplicationStatus outcome,
        ApplicationReasonCode reasonCode,
        string? notes,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!Enum.IsDefined(reasonCode))
        {
            throw new RegistryValidationException($"'{reasonCode}' is not an {nameof(ApplicationReasonCode)} GridCore declares.");
        }

        // The fixed list, enforced in the aggregate rather than only at the edge — so a seeder, a
        // later module and the service all meet it. WP-2.15's rule, unchanged.
        if (!ApplicationReasons.IsAllowed(outcome, reasonCode))
        {
            throw new RegistryValidationException(
                $"'{reasonCode}' is not a reason an application may be {outcome} under. "
                + $"Allowed: {string.Join(", ", ApplicationReasons.For(outcome))}.");
        }

        var written = RegistryText.Clean(notes, NotesLength);

        if (ApplicationReasons.RequiresNotes(reasonCode) && written is null)
        {
            throw new RegistryValidationException(
                $"An application recorded as '{reasonCode}' has to say what actually happened. "
                + "The fixed list is only fixed if the codes that escape it explain themselves.");
        }

        Status = outcome;
        DecisionReasonCode = reasonCode;
        DecisionNotes = written;
        DecidedAt = now;
        DecidedById = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
            ?? throw new RegistryValidationException("A decision must name who made it.");
        DecidedByName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength);
    }
}
