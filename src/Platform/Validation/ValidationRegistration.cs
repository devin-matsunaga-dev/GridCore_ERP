using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Platform.Validation;

/// <summary>
/// Marks an endpoint as validating its request body, and says which type it validates. Endpoint
/// filters leave no metadata of their own, so without this there is no way to tell a write endpoint
/// that validates from one that forgot to — and the difference only shows up as a 500 the first
/// time somebody posts a bad body.
/// </summary>
/// <param name="RequestType">The request body the endpoint validates.</param>
public sealed record ValidatedRequest(Type RequestType);

/// <summary>
/// How a module registers edge validation. CONVENTIONS.md puts FluentValidation at the edge; these
/// two calls are the whole convention — one registers the validator, the other attaches it to the
/// endpoint that takes the request.
/// </summary>
/// <remarks>
/// Validators are registered one by one rather than by assembly scanning, for the same reason
/// modules are listed explicitly in <c>Program.cs</c>: the composition stays greppable, and a
/// validator that is written but never wired is a compile-time-visible omission rather than a rule
/// that silently never ran.
/// </remarks>
public static class ValidationRegistration
{
    /// <summary>
    /// Registers the validator for a request DTO. Singleton: a validator states rules and holds no
    /// state, and the filter resolves it per request anyway, so a later validator that needs a
    /// scoped service can be registered scoped without changing anything here.
    /// </summary>
    /// <typeparam name="TRequest">The request body being validated.</typeparam>
    /// <typeparam name="TValidator">The rules for it.</typeparam>
    public static IServiceCollection AddGridCoreValidator<TRequest, TValidator>(this IServiceCollection services)
        where TRequest : class
        where TValidator : class, IValidator<TRequest>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IValidator<TRequest>, TValidator>();

        return services;
    }

    /// <summary>
    /// Validates the endpoint's <typeparamref name="TRequest"/> body before the handler runs,
    /// answering 400 with an RFC 7807 validation problem if it does not hold up.
    /// </summary>
    /// <typeparam name="TRequest">The request body being validated.</typeparam>
    public static RouteHandlerBuilder WithValidation<TRequest>(this RouteHandlerBuilder builder)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .AddEndpointFilter<ValidationEndpointFilter<TRequest>>()
            .WithMetadata(new ValidatedRequest(typeof(TRequest)));
    }
}
