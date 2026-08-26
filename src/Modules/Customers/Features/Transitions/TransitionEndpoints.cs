using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.Features.Transitions;

/// <summary>
/// The fields every transition request carries, over which the shared rules are written.
/// </summary>
/// <remarks>
/// An interface rather than a base record, the shape <c>ICustomerDetails</c> already takes: the five
/// bodies are genuinely different — one names a class, one a premise, one an account — and making
/// four of them inherit a fifth would be a hierarchy in place of three shared fields.
/// </remarks>
public interface ITransitionRequest
{
    /// <summary>Why, from the fixed list.</summary>
    TransitionReasonCode ReasonCode { get; }

    /// <summary>The day the change applies from. Today when the caller does not say.</summary>
    DateOnly? EffectiveOn { get; }

    /// <summary>What the operator wants to add. Required with <see cref="TransitionReasonCode.Other"/>.</summary>
    string? Notes { get; }
}

/// <summary>Body of a request to move a customer between classes.</summary>
/// <param name="Class">What they are to become.</param>
/// <param name="ReasonCode">Why, from the fixed list.</param>
/// <param name="EffectiveOn">The day the new class applies from.</param>
/// <param name="Notes">What the operator wants to add.</param>
public sealed record ChangeCustomerClassRequest(
    CustomerClass Class,
    TransitionReasonCode ReasonCode,
    DateOnly? EffectiveOn = null,
    string? Notes = null) : ITransitionRequest;

/// <summary>Body of a request to move a customer between statuses.</summary>
/// <param name="Status">Where they should end up.</param>
/// <param name="ReasonCode">Why, from the fixed list.</param>
/// <param name="EffectiveOn">The day the new status applies from.</param>
/// <param name="Notes">What the operator wants to add.</param>
public sealed record ChangeCustomerStatusRequest(
    CustomerStatus Status,
    TransitionReasonCode ReasonCode,
    DateOnly? EffectiveOn = null,
    string? Notes = null) : ITransitionRequest;

/// <summary>Body of a request to move a customer in at a premise.</summary>
/// <param name="ServiceLocationId">Where they are moving in.</param>
/// <param name="ReasonCode">Why, from the fixed list.</param>
/// <param name="EffectiveOn">The day service is taken up.</param>
/// <param name="Notes">What the operator wants to add.</param>
public sealed record MoveInRequest(
    Guid ServiceLocationId,
    TransitionReasonCode ReasonCode,
    DateOnly? EffectiveOn = null,
    string? Notes = null) : ITransitionRequest;

/// <summary>Body of a request to end a customer's service at a premise.</summary>
/// <param name="ServiceAccountId">The account to close.</param>
/// <param name="ReasonCode">Why, from the fixed list.</param>
/// <param name="EffectiveOn">The day service ended.</param>
/// <param name="Notes">What the operator wants to add.</param>
public sealed record MoveOutRequest(
    Guid ServiceAccountId,
    TransitionReasonCode ReasonCode,
    DateOnly? EffectiveOn = null,
    string? Notes = null) : ITransitionRequest;

/// <summary>Body of a request to move a customer's service between premises.</summary>
/// <param name="FromServiceAccountId">The account to close at the premise being left.</param>
/// <param name="ToServiceLocationId">The premise being taken up.</param>
/// <param name="ReasonCode">Why, from the fixed list.</param>
/// <param name="EffectiveOn">The day service moved.</param>
/// <param name="Notes">What the operator wants to add.</param>
public sealed record TransferServiceRequest(
    Guid FromServiceAccountId,
    Guid ToServiceLocationId,
    TransitionReasonCode ReasonCode,
    DateOnly? EffectiveOn = null,
    string? Notes = null) : ITransitionRequest;

/// <summary>One recorded transition as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="CustomerId">Who it happened to.</param>
/// <param name="Kind">What kind of move it was.</param>
/// <param name="ReasonCode">The fixed-list code it was recorded under.</param>
/// <param name="Notes">What the operator wrote beside it.</param>
/// <param name="EffectiveOn">The day it applies from.</param>
/// <param name="FromValue">What it was — a class, a status, or the account released.</param>
/// <param name="ToValue">What it became.</param>
/// <param name="FromServiceAccountId">The account closed, where one was.</param>
/// <param name="ToServiceAccountId">The account opened, where one was.</param>
/// <param name="DepositCarried">How much held deposit rode along. Zero on everything but a transfer.</param>
/// <param name="Currency">ISO 4217 code that figure is in, where there is one.</param>
/// <param name="DepositEntryId">The ledger entry that carried it, where there was anything to carry.</param>
/// <param name="ActorId">Subject id of whoever made it.</param>
/// <param name="ActorName">Their name at the time.</param>
/// <param name="RecordedAt">When it was recorded — not when it applies from.</param>
public sealed record AccountTransitionResponse(
    Guid Id,
    Guid CustomerId,
    string Kind,
    string ReasonCode,
    string? Notes,
    DateOnly EffectiveOn,
    string? FromValue,
    string? ToValue,
    Guid? FromServiceAccountId,
    Guid? ToServiceAccountId,
    decimal DepositCarried,
    string? Currency,
    Guid? DepositEntryId,
    string ActorId,
    string? ActorName,
    DateTimeOffset RecordedAt)
{
    /// <summary>Projects a transition for the wire.</summary>
    public static AccountTransitionResponse From(AccountTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        return new AccountTransitionResponse(
            transition.Id,
            transition.CustomerId,
            transition.Kind.ToString(),
            transition.ReasonCode.ToString(),
            transition.Notes,
            transition.EffectiveOn,
            transition.FromValue,
            transition.ToValue,
            transition.FromServiceAccountId,
            transition.ToServiceAccountId,
            transition.DepositCarried,
            transition.Currency,
            transition.DepositEntryId,
            transition.ActorId,
            transition.ActorName,
            transition.RecordedAt);
    }
}

/// <summary>The account transitions' HTTP surface (WP-2.15).</summary>
/// <remarks>
/// <para>
/// <b>One group under the customer, because every one of the five is that customer's.</b> A transfer
/// is defined by both accounts belonging to the same customer, and a move-in has no account to hang
/// off until it has happened — so <c>/api/customers/{id}/transitions</c> is the only prefix all five
/// fit under.
/// </para>
/// <para>
/// <b>Each is a POST sub-resource named for the act</b>, per CONVENTIONS.md — a transition is not a
/// field edit, and the verb being the URL is what stops an operator mistyping "move-out" as
/// "transfer" in a body. The register itself is a GET on the group.
/// </para>
/// <para>
/// <b>The writes carry <see cref="Permissions.Customers.Transition"/> on the route <i>and</i> the
/// service demands it too, which is not belt-and-braces.</b> It is the shape WP-2.12's deposit
/// routes take, for the reason they take it: every route in this group <i>is</i> a transition, so
/// the route can honestly say so — unlike WP-2.11's <c>customers.authorise</c>, where only part of
/// a request performed the gated act and routing could not see which. The service demands it as well
/// because it is reachable in process, and CONVENTIONS.md asks a service to enforce its own rules
/// rather than trust the endpoint that called it. <c>The_service_refuses_a_transition_without_the_permission</c>
/// is what keeps the second check honest when the first would have hidden its absence.
/// </para>
/// <para>
/// The read is <see cref="Permissions.Customers.Read"/>: a clerk who may not move a customer still
/// has to be able to say what has happened to them, which is the call WP-2.12 made about the deposit
/// ledger for the same reason.
/// </para>
/// </remarks>
public static class TransitionEndpoints
{
    /// <summary>Route prefix of one customer's transition register.</summary>
    public const string RoutePrefix = "/api/customers/{customerId:guid}/transitions";

    /// <summary>Maps the transition endpoints.</summary>
    public static IEndpointRouteBuilder MapTransitionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).WithTags("Account transitions");

        group
            .MapGet("/", (
                    [FromRoute] Guid customerId,
                    AccountTransitionKind? kind,
                    Guid? serviceAccountId,
                    int? limit,
                    [FromServices] ICustomerTransitionService transitions,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok((await transitions.ListAsync(
                            customerId,
                            new TransitionQuery(kind, serviceAccountId, limit ?? 100),
                            cancellationToken))
                        .Select(AccountTransitionResponse.From)
                        .ToList())))
            .RequirePermission(Permissions.Customers.Read)
            .WithName("ListAccountTransitions");

        group
            .MapPost("/class", (
                    [FromRoute] Guid customerId,
                    ChangeCustomerClassRequest body,
                    [FromServices] ICustomerTransitionService transitions,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Created(customerId, await transitions.ChangeClassAsync(
                        customerId,
                        new ChangeCustomerClassInput(body.Class, body.ReasonCode, body.EffectiveOn, body.Notes),
                        cancellationToken))))
            .RequirePermission(Permissions.Customers.Transition)
            .WithValidation<ChangeCustomerClassRequest>()
            .WithName("ChangeCustomerClass");

        group
            .MapPost("/status", (
                    [FromRoute] Guid customerId,
                    ChangeCustomerStatusRequest body,
                    [FromServices] ICustomerTransitionService transitions,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Created(customerId, await transitions.ChangeStatusAsync(
                        customerId,
                        new ChangeCustomerStatusInput(body.Status, body.ReasonCode, body.EffectiveOn, body.Notes),
                        cancellationToken))))
            .RequirePermission(Permissions.Customers.Transition)
            .WithValidation<ChangeCustomerStatusRequest>()
            .WithName("ChangeCustomerStatus");

        group
            .MapPost("/move-in", (
                    [FromRoute] Guid customerId,
                    MoveInRequest body,
                    [FromServices] ICustomerTransitionService transitions,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Created(customerId, await transitions.MoveInAsync(
                        customerId,
                        new MoveInInput(body.ServiceLocationId, body.ReasonCode, body.EffectiveOn, body.Notes),
                        cancellationToken))))
            .RequirePermission(Permissions.Customers.Transition)
            .WithValidation<MoveInRequest>()
            .WithName("MoveCustomerIn");

        group
            .MapPost("/move-out", (
                    [FromRoute] Guid customerId,
                    MoveOutRequest body,
                    [FromServices] ICustomerTransitionService transitions,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Created(customerId, await transitions.MoveOutAsync(
                        customerId,
                        new MoveOutInput(body.ServiceAccountId, body.ReasonCode, body.EffectiveOn, body.Notes),
                        cancellationToken))))
            .RequirePermission(Permissions.Customers.Transition)
            .WithValidation<MoveOutRequest>()
            .WithName("MoveCustomerOut");

        group
            .MapPost("/transfer", (
                    [FromRoute] Guid customerId,
                    TransferServiceRequest body,
                    [FromServices] ICustomerTransitionService transitions,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Created(customerId, await transitions.TransferAsync(
                        customerId,
                        new TransferServiceInput(
                            body.FromServiceAccountId,
                            body.ToServiceLocationId,
                            body.ReasonCode,
                            body.EffectiveOn,
                            body.Notes),
                        cancellationToken))))
            .RequirePermission(Permissions.Customers.Transition)
            .WithValidation<TransferServiceRequest>()
            .WithName("TransferCustomerService");

        return endpoints;
    }

    /// <summary>
    /// A 201 pointing at the register the new row joined.
    /// </summary>
    /// <remarks>
    /// Created rather than Ok, unlike WP-1.2's transitions, because a transition here <i>is</i> a
    /// row: the register is append-only and the response body is the entry that was written. The
    /// location is the collection rather than the entry — there is no endpoint that serves one
    /// transition on its own, and a Location header pointing at a 404 is worse than none.
    /// </remarks>
    private static IResult Created(Guid customerId, AccountTransition transition) =>
        Results.Created(
            $"/api/customers/{customerId}/transitions",
            AccountTransitionResponse.From(transition));
}
