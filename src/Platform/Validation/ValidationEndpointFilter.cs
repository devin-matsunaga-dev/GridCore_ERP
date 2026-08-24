using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Platform.Validation;

/// <summary>
/// Runs the FluentValidation validator registered for <typeparamref name="TRequest"/> before the
/// endpoint's handler, and turns a failure into the RFC 7807 validation problem CONVENTIONS.md
/// prescribes. Validation happens at the edge; a service is then free to assume its input is
/// well-formed and to spend its own guards on the rules only it can see.
/// </summary>
/// <remarks>
/// The validator is resolved from the <i>request</i> services rather than injected into the filter.
/// <c>AddEndpointFilter&lt;T&gt;</c> constructs the filter once, from the application's root
/// provider, so a constructor dependency would fix every validator's lifetime as a singleton and
/// fail loudly the day one legitimately needs a scoped service.
/// </remarks>
/// <typeparam name="TRequest">The request body being validated.</typeparam>
public sealed class ValidationEndpointFilter<TRequest> : IEndpointFilter
    where TRequest : class
{
    /// <summary>Title carried by every validation failure, so a client can match on one string.</summary>
    public const string ValidationProblemTitle = "The request is not valid";

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();

        if (request is null)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["body"] = ["A request body is required."] },
                title: ValidationProblemTitle);
        }

        // Resolved rather than injected: see the remarks above. Missing registration is a
        // composition mistake, so it throws rather than quietly letting the request through.
        var validator = context.HttpContext.RequestServices.GetService<IValidator<TRequest>>()
            ?? throw new InvalidOperationException(
                $"No IValidator<{typeof(TRequest).Name}> is registered. Register it with "
                + $"services.AddGridCoreValidator<{typeof(TRequest).Name}, {typeof(TRequest).Name}Validator>().");

        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted).ConfigureAwait(false);

        if (result.IsValid)
        {
            return await next(context).ConfigureAwait(false);
        }

        return Results.ValidationProblem(ErrorsOf(result.Errors), title: ValidationProblemTitle);
    }

    /// <summary>
    /// Groups the failures by field, naming each field the way the caller wrote it. FluentValidation
    /// reports the CLR property name (<c>AccountNumber</c>); the caller sent JSON
    /// (<c>accountNumber</c>), and an error it cannot attach to its own form field is an error it
    /// can only show as a sentence.
    /// </summary>
    private static Dictionary<string, string[]> ErrorsOf(IEnumerable<FluentValidation.Results.ValidationFailure> failures) =>
        failures
            .GroupBy(failure => JsonNameOf(failure.PropertyName), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(failure => failure.ErrorMessage).ToArray(),
                StringComparer.Ordinal);

    /// <summary>
    /// The JSON name of a validated property path — <c>Address.Line1</c> becomes
    /// <c>address.line1</c>. Only the first letter of each segment moves, matching
    /// <c>JsonNamingPolicy.CamelCase</c> for the names these DTOs actually use.
    /// </summary>
    private static string JsonNameOf(string propertyName) =>
        string.IsNullOrEmpty(propertyName)
            ? "body"
            : string.Join('.', propertyName.Split('.').Select(CamelCase));

    private static string CamelCase(string segment) =>
        segment.Length is 0 ? segment : char.ToLowerInvariant(segment[0]) + segment[1..];
}
