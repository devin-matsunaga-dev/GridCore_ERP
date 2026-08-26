using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.Features.Applications;

/// <summary>Body of a request to file an application.</summary>
/// <param name="CustomerId">Who is applying.</param>
/// <param name="ServiceLocationId">Where they want to be served.</param>
/// <param name="ServiceType">
/// Which supply. <b>Required, with no default</b> — deliberately unlike WP-2.8's intake body and
/// WP-2.15's move-in, both of which default to <see cref="ServiceType.Electricity"/> so a wizard
/// need not ask a question with one real answer. An application is the form the utility reviews and
/// the account is opened from: what was applied for has to be what somebody wrote down, not what
/// the API assumed. Those older defaults are left alone; this is a considered difference, not drift.
/// </param>
/// <param name="RequestedOn">The day supply is wanted from. Today when the caller does not say.</param>
/// <param name="Notes">What the applicant or the rep wrote.</param>
public sealed record SubmitApplicationRequest(
    Guid CustomerId,
    Guid ServiceLocationId,
    ServiceType ServiceType,
    DateOnly? RequestedOn = null,
    string? Notes = null);

/// <summary>Body of a request to decide an application — approve, reject or withdraw.</summary>
/// <param name="ReasonCode">Why, from the fixed list for that decision.</param>
/// <param name="Notes">What the reviewer wants to add. Required with the codes that escape the list.</param>
public sealed record DecideApplicationRequest(ApplicationReasonCode ReasonCode, string? Notes = null);

/// <summary>Body of a request to file a fresh application replacing a decided one.</summary>
/// <param name="RequestedOn">A new date for supply, where the applicant has given one.</param>
/// <param name="Notes">What to record on the new application.</param>
public sealed record ResubmitApplicationRequest(DateOnly? RequestedOn = null, string? Notes = null);

/// <summary>One checklist line as the API returns it.</summary>
/// <param name="Kind">The document asked for.</param>
/// <param name="IsSatisfied">Whether it has arrived.</param>
/// <param name="DocumentId">The newest document answering it.</param>
/// <param name="UploadedAt">When that document arrived.</param>
public sealed record ApplicationChecklistResponse(string Kind, bool IsSatisfied, Guid? DocumentId, DateTimeOffset? UploadedAt)
{
    /// <summary>Projects a checklist line for the wire.</summary>
    public static ApplicationChecklistResponse From(ApplicationChecklistLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new ApplicationChecklistResponse(line.Kind.ToString(), line.IsSatisfied, line.DocumentId, line.UploadedAt);
    }
}

/// <summary>One attached document as the API returns it. Never the bytes — those are their own route.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="Kind">What it is.</param>
/// <param name="FileName">What the uploader called it.</param>
/// <param name="ContentType">The media type it was filed under.</param>
/// <param name="SizeInBytes">How large it is.</param>
/// <param name="Checksum">SHA-256 of the bytes, lower-case hex.</param>
/// <param name="UploadedAt">When it arrived.</param>
/// <param name="ActorId">Subject id of whoever uploaded it.</param>
/// <param name="ActorName">Their name at the time.</param>
public sealed record ApplicationDocumentResponse(
    Guid Id,
    string Kind,
    string FileName,
    string ContentType,
    long SizeInBytes,
    string Checksum,
    DateTimeOffset UploadedAt,
    string ActorId,
    string? ActorName)
{
    /// <summary>Projects a document for the wire.</summary>
    public static ApplicationDocumentResponse From(ApplicationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new ApplicationDocumentResponse(
            document.Id,
            document.Kind.ToString(),
            document.FileName,
            document.ContentType,
            document.SizeInBytes,
            document.Checksum,
            document.UploadedAt,
            document.ActorId,
            document.ActorName);
    }
}

/// <summary>One application as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="ApplicationNumber">The number quoted to the applicant.</param>
/// <param name="CustomerId">Who applied.</param>
/// <param name="ServiceLocationId">Where.</param>
/// <param name="ServiceType">Which supply.</param>
/// <param name="Type">Which checklist it is held to.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="AllowedTransitions">The statuses it may move to, for rendering buttons.</param>
/// <param name="IsOpen">Whether it is still on the desk.</param>
/// <param name="RequestedOn">The day supply is wanted from.</param>
/// <param name="Notes">What was written when it was filed.</param>
/// <param name="Checklist">What it must carry, against what has arrived.</param>
/// <param name="MissingDocuments">The required documents still outstanding — empty is what approval needs.</param>
/// <param name="IsDocumentationComplete">Whether the checklist is satisfied.</param>
/// <param name="Documents">Everything attached, oldest first.</param>
/// <param name="SubmittedAt">When it was filed.</param>
/// <param name="SubmittedById">Subject id of whoever filed it.</param>
/// <param name="SubmittedByName">Their name at the time.</param>
/// <param name="ReviewStartedAt">When a reviewer picked it up.</param>
/// <param name="ReviewerId">Subject id of that reviewer.</param>
/// <param name="ReviewerName">Their name at the time.</param>
/// <param name="DecidedAt">When it was decided.</param>
/// <param name="DecidedById">Subject id of whoever decided it.</param>
/// <param name="DecidedByName">Their name at the time.</param>
/// <param name="DecisionReasonCode">The code the decision was recorded under.</param>
/// <param name="DecisionNotes">What the reviewer wrote beside it.</param>
/// <param name="ServiceAccountId">The account approval opened.</param>
/// <param name="ReplacesApplicationId">The decided application this one replaced.</param>
public sealed record ServiceApplicationResponse(
    Guid Id,
    string ApplicationNumber,
    Guid CustomerId,
    Guid ServiceLocationId,
    string ServiceType,
    string Type,
    string Status,
    IReadOnlyList<string> AllowedTransitions,
    bool IsOpen,
    DateOnly RequestedOn,
    string? Notes,
    IReadOnlyList<ApplicationChecklistResponse> Checklist,
    IReadOnlyList<string> MissingDocuments,
    bool IsDocumentationComplete,
    IReadOnlyList<ApplicationDocumentResponse> Documents,
    DateTimeOffset SubmittedAt,
    string SubmittedById,
    string? SubmittedByName,
    DateTimeOffset? ReviewStartedAt,
    string? ReviewerId,
    string? ReviewerName,
    DateTimeOffset? DecidedAt,
    string? DecidedById,
    string? DecidedByName,
    string? DecisionReasonCode,
    string? DecisionNotes,
    Guid? ServiceAccountId,
    Guid? ReplacesApplicationId)
{
    /// <summary>Projects an application for the wire.</summary>
    public static ServiceApplicationResponse From(ServiceApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        return new ServiceApplicationResponse(
            application.Id,
            application.ApplicationNumber,
            application.CustomerId,
            application.ServiceLocationId,
            application.ServiceType.ToString(),
            application.Type.ToString(),
            application.Status.ToString(),

            // By name, so a UI renders buttons from what the state machine actually allows rather
            // than from a list it keeps in step by hand — WP-1.5's shape.
            [.. application.AllowedTransitions.Select(status => status.ToString())],
            application.IsOpen,
            application.RequestedOn,
            application.Notes,
            [.. application.Checklist.Select(ApplicationChecklistResponse.From)],
            [.. application.MissingDocuments.Select(kind => kind.ToString())],
            application.IsDocumentationComplete,
            [.. application.Documents.Select(ApplicationDocumentResponse.From)],
            application.SubmittedAt,
            application.SubmittedById,
            application.SubmittedByName,
            application.ReviewStartedAt,
            application.ReviewerId,
            application.ReviewerName,
            application.DecidedAt,
            application.DecidedById,
            application.DecidedByName,
            application.DecisionReasonCode?.ToString(),
            application.DecisionNotes,
            application.ServiceAccountId,
            application.ReplacesApplicationId);
    }
}

/// <summary>What an approval produced, as the API returns it.</summary>
/// <param name="Application">The application, now approved.</param>
/// <param name="Account">The service account it opened — Pending until somebody connects it.</param>
/// <param name="Deposit">What is held against what is now required, the new account included.</param>
public sealed record ApplicationApprovalResponse(
    ServiceApplicationResponse Application,
    ServiceAccountResponse Account,
    DepositRequirementResponse Deposit)
{
    /// <summary>Projects an approval for the wire.</summary>
    public static ApplicationApprovalResponse From(ApplicationApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);

        return new ApplicationApprovalResponse(
            ServiceApplicationResponse.From(approval.Application),
            ServiceAccountResponse.From(approval.Account),
            DepositRequirementResponse.From(approval.Deposit));
    }
}

/// <summary>The reference data an application form and a review dialog need, in one read.</summary>
/// <param name="Types">Each application type with the documents it requires.</param>
/// <param name="DocumentKinds">Every document kind that may be attached, required or not.</param>
/// <param name="AllowedContentTypes">The media types an upload may declare.</param>
/// <param name="MaxSizeInBytes">The largest upload accepted.</param>
/// <param name="ReasonCodes">The reason codes legal against each terminal status.</param>
/// <param name="ReasonCodesRequiringNotes">The codes that oblige the reviewer to write something too.</param>
public sealed record ApplicationReferenceResponse(
    IReadOnlyList<ApplicationTypeResponse> Types,
    IReadOnlyList<string> DocumentKinds,
    IReadOnlyList<string> AllowedContentTypes,
    long MaxSizeInBytes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ReasonCodes,
    IReadOnlyList<string> ReasonCodesRequiringNotes)
{
    /// <summary>Reads the static policy out of the domain, so a browser never keeps a second copy.</summary>
    public static ApplicationReferenceResponse Current()
    {
        ServiceApplicationStatus[] decisions =
        [
            ServiceApplicationStatus.Approved,
            ServiceApplicationStatus.Rejected,
            ServiceApplicationStatus.Withdrawn,
        ];

        return new ApplicationReferenceResponse(
            [
                .. Enum.GetValues<ServiceApplicationType>().Select(type => new ApplicationTypeResponse(
                    type.ToString(),
                    [.. ServiceApplicationTypes.RequiredDocuments(type).Select(kind => kind.ToString())])),
            ],
            [.. Enum.GetValues<ApplicationDocumentKind>().Select(kind => kind.ToString())],
            [.. ApplicationDocuments.AllowedContentTypes.Order(StringComparer.Ordinal)],
            ApplicationDocuments.MaxSizeInBytes,
            decisions.ToDictionary(
                status => status.ToString(),
                status => (IReadOnlyList<string>)[.. ApplicationReasons.For(status).Select(code => code.ToString())],
                StringComparer.Ordinal),
            [.. Enum.GetValues<ApplicationReasonCode>().Where(ApplicationReasons.RequiresNotes).Select(code => code.ToString())]);
    }
}

/// <summary>One application type and the documents it requires.</summary>
/// <param name="Type">The type, by name.</param>
/// <param name="RequiredDocuments">The document kinds it must carry.</param>
public sealed record ApplicationTypeResponse(string Type, IReadOnlyList<string> RequiredDocuments);

/// <summary>
/// The service application register's HTTP surface (WP-2.18).
/// </summary>
/// <remarks>
/// <para>
/// <b>A resource of its own, not a sub-route of the customer.</b> An application belongs to a
/// customer, but the screen it exists for is a <i>queue</i> across every customer — "what is waiting
/// to be reviewed" — and hanging it off <c>/api/customers/{id}</c> would name it after the one thing
/// the review desk does not filter by. The 360 reads the same collection with
/// <c>?customerId=</c>, which is the shape WP-2.16's charges already take.
/// </para>
/// <para>
/// <b>Three gates across the group.</b> Reads and the checklist are
/// <see cref="Permissions.Customers.Read"/> — a clerk who may not decide anything still has to be
/// able to tell an applicant what is outstanding. Filing, picking up, uploading and withdrawing are
/// <see cref="Permissions.Customers.Write"/>: clerical work. Approving and rejecting are
/// <see cref="Permissions.Customers.Approve"/> on the route <i>and</i> in the service, which is the
/// shape WP-2.15's transitions take — every route carrying it genuinely is a decision, and the
/// service demands it again because WP-3.6's connection order will reach it in process. Reading a
/// document's bytes back is <see cref="Permissions.Customers.Documents"/>, demanded in the service
/// alone: the route serves a scanned identity page, and that is the same act as producing a
/// statement (WP-2.14).
/// </para>
/// </remarks>
public static class ApplicationEndpoints
{
    /// <summary>Route prefix of the application register.</summary>
    public const string RoutePrefix = "/api/service-applications";

    /// <summary>Route of the reference data an application form needs.</summary>
    public const string ReferenceRoute = "/api/service-application-reference";

    /// <summary>Default page size for the register list.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Maps the application endpoints.</summary>
    public static IEndpointRouteBuilder MapApplicationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).WithTags("Service applications");

        group
            .MapGet("/", async (
                    string? search,
                    Guid? customerId,
                    Guid? serviceLocationId,
                    ServiceApplicationStatus? status,
                    ServiceType? serviceType,
                    bool? openOnly,
                    int? limit,
                    [FromServices] IServiceApplicationService applications,
                    CancellationToken cancellationToken) =>
                Results.Ok((await applications.ListAsync(
                        new ServiceApplicationQuery(
                            search,
                            customerId,
                            serviceLocationId,
                            status,
                            serviceType,
                            openOnly,
                            limit ?? DefaultPageSize),
                        cancellationToken))
                    .Select(ServiceApplicationResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Customers.Read)
            .WithName("ListServiceApplications");

        group
            .MapGet("/{id:guid}", async (
                    [FromRoute] Guid id,
                    [FromServices] IServiceApplicationService applications,
                    CancellationToken cancellationToken) =>
                await applications.FindAsync(id, cancellationToken) is { } application
                    ? Results.Ok(ServiceApplicationResponse.From(application))
                    : RegistryProblems.ServiceApplicationNotFound(id))
            .RequirePermission(Permissions.Customers.Read)
            .WithName("GetServiceApplication");

        group
            .MapPost("/", (
                    SubmitApplicationRequest body,
                    [FromServices] IServiceApplicationService applications,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var application = await applications.SubmitAsync(
                        new SubmitApplicationInput(
                            body.CustomerId,
                            body.ServiceLocationId,
                            body.ServiceType,
                            body.RequestedOn,
                            body.Notes),
                        cancellationToken);

                    return Results.Created($"{RoutePrefix}/{application.Id}", ServiceApplicationResponse.From(application));
                }))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<SubmitApplicationRequest>()
            .WithName("SubmitServiceApplication");

        group
            .MapPost("/{id:guid}/review", (
                    [FromRoute] Guid id,
                    [FromServices] IServiceApplicationService applications,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(ServiceApplicationResponse.From(await applications.StartReviewAsync(id, cancellationToken)))))
            .RequirePermission(Permissions.Customers.Write)
            .WithName("StartServiceApplicationReview");

        // Multipart, and the only endpoint in GridCore that is. A file is not JSON, and base64 in a
        // body would inflate every scan by a third and hide the size from the server until it had
        // already been buffered. FluentValidation is not applied here for the same reason: the rules
        // are about bytes rather than about fields, so the service owns them — see
        // ServiceApplicationService.AttachDocumentAsync, which answers 400 exactly as a validator
        // would have.
        group
            .MapPost("/{id:guid}/documents", (
                    [FromRoute] Guid id,
                    [FromForm] ApplicationDocumentKind kind,
                    IFormFile file,
                    [FromServices] IServiceApplicationService applications,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    if (file.Length > ApplicationDocuments.MaxSizeInBytes)
                    {
                        // Refused before the stream is read, so an oversized upload costs one header
                        // rather than ten megabytes of buffering. The service checks again, because
                        // a Length a client can lie about is not a limit.
                        throw new RegistryValidationException(
                            $"An uploaded document may be at most {ApplicationDocuments.MaxSizeInBytes / (1024 * 1024)} MB; "
                            + $"this one declares {file.Length} bytes.");
                    }

                    using var buffer = new MemoryStream();
                    await file.CopyToAsync(buffer, cancellationToken);

                    var document = await applications.AttachDocumentAsync(
                        id,
                        new AttachDocumentInput(kind, file.FileName, file.ContentType, buffer.ToArray()),
                        cancellationToken);

                    return Results.Created(
                        $"{RoutePrefix}/{id}/documents/{document.Id}",
                        ApplicationDocumentResponse.From(document));
                }))
            .RequirePermission(Permissions.Customers.Write)
            .DisableAntiforgery()
            .WithName("AttachServiceApplicationDocument");

        // The bytes. Gated in the SERVICE on customers.documents rather than on the route, because
        // it is the same act as producing a statement and should carry the same grant — while the
        // route around it is a read of an application like its neighbours.
        group
            .MapGet("/{id:guid}/documents/{documentId:guid}/content", (
                    [FromRoute] Guid id,
                    [FromRoute] Guid documentId,
                    [FromServices] IServiceApplicationService applications,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var content = await applications.ReadDocumentAsync(id, documentId, cancellationToken);

                    // Inline rather than an attachment: a reviewer looks at a scan on screen, and a
                    // browser that downloads it instead turns a review into a folder of files.
                    return Results.File(content.Content.ToArray(), content.ContentType);
                }))
            .RequirePermission(Permissions.Customers.Read)
            .WithName("GetServiceApplicationDocument");

        group
            .MapPost("/{id:guid}/approve", (
                    [FromRoute] Guid id,
                    DecideApplicationRequest body,
                    [FromServices] IServiceApplicationService applications,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(ApplicationApprovalResponse.From(await applications.ApproveAsync(
                        id,
                        new DecideApplicationInput(body.ReasonCode, body.Notes),
                        cancellationToken)))))
            .RequirePermission(Permissions.Customers.Approve)
            .WithValidation<DecideApplicationRequest>()
            .WithName("ApproveServiceApplication");

        group
            .MapPost("/{id:guid}/reject", (
                    [FromRoute] Guid id,
                    DecideApplicationRequest body,
                    [FromServices] IServiceApplicationService applications,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(ServiceApplicationResponse.From(await applications.RejectAsync(
                        id,
                        new DecideApplicationInput(body.ReasonCode, body.Notes),
                        cancellationToken)))))
            .RequirePermission(Permissions.Customers.Approve)
            .WithValidation<DecideApplicationRequest>()
            .WithName("RejectServiceApplication");

        // customers.write, not customers.approve. A withdrawal is the applicant's own act relayed by
        // the desk; see ServiceApplicationService.WithdrawAsync for why gating it would teach a desk
        // to reject instead.
        group
            .MapPost("/{id:guid}/withdraw", (
                    [FromRoute] Guid id,
                    DecideApplicationRequest body,
                    [FromServices] IServiceApplicationService applications,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(ServiceApplicationResponse.From(await applications.WithdrawAsync(
                        id,
                        new DecideApplicationInput(body.ReasonCode, body.Notes),
                        cancellationToken)))))
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<DecideApplicationRequest>()
            .WithName("WithdrawServiceApplication");

        // A sub-resource of the application it replaces, so the provenance is in the URL: this is
        // "the resubmission of AP-000004", not a second POST to the collection that happens to
        // mention one.
        group
            .MapPost("/{id:guid}/resubmissions", (
                    [FromRoute] Guid id,
                    ResubmitApplicationRequest? body,
                    [FromServices] IServiceApplicationService applications,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var application = await applications.ResubmitAsync(
                        id,
                        body is null ? null : new ResubmitApplicationInput(body.RequestedOn, body.Notes),
                        cancellationToken);

                    return Results.Created($"{RoutePrefix}/{application.Id}", ServiceApplicationResponse.From(application));
                }))
            .RequirePermission(Permissions.Customers.Write)
            .WithName("ResubmitServiceApplication");

        // Reference data for the form and the decision dialogs. Read-only and static — it is the
        // domain's own policy, projected, so a browser never keeps a second copy of the checklist to
        // fall out of step with (the call WP-1.5 made about allowed transitions).
        endpoints
            .MapGet(ReferenceRoute, () => Results.Ok(ApplicationReferenceResponse.Current()))
            .RequirePermission(Permissions.Customers.Read)
            .WithTags("Service applications")
            .WithName("GetServiceApplicationReference");

        return endpoints;
    }
}
