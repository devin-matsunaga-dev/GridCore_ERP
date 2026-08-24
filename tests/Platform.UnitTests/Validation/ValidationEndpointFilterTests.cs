using FluentValidation;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Platform.UnitTests.Validation;

/// <summary>
/// The edge-validation filter on its own: no server, no routing. It is the one piece every module's
/// write endpoints share, so what it answers with — and when it lets a request through — is worth
/// pinning down here rather than once per module.
/// </summary>
public class ValidationEndpointFilterTests
{
    /// <summary>A request body standing in for a module's DTO.</summary>
    /// <param name="Name">The field under test.</param>
    public sealed record ThingRequest(string Name);

    private sealed class ThingRequestValidator : AbstractValidator<ThingRequest>
    {
        public ThingRequestValidator() => RuleFor(request => request.Name).NotEmpty().MaximumLength(8);
    }

    private static EndpointFilterInvocationContext ContextFor(object? body, bool registerValidator = true)
    {
        var services = new ServiceCollection();

        if (registerValidator)
        {
            services.AddGridCoreValidator<ThingRequest, ThingRequestValidator>();
        }

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

        return EndpointFilterInvocationContext.Create(httpContext, body!);
    }

    private static async Task<(object? Result, bool HandlerRan)> InvokeAsync(EndpointFilterInvocationContext context)
    {
        var handlerRan = false;

        var result = await new ValidationEndpointFilter<ThingRequest>().InvokeAsync(
            context,
            _ =>
            {
                handlerRan = true;

                return ValueTask.FromResult<object?>(Results.Ok());
            });

        return (result, handlerRan);
    }

    [Fact]
    public async Task A_valid_body_reaches_the_handler()
    {
        var (_, handlerRan) = await InvokeAsync(ContextFor(new ThingRequest("ok")));

        Assert.True(handlerRan);
    }

    [Fact]
    public async Task An_invalid_body_is_refused_before_the_handler_runs()
    {
        // The point of validating at the edge: the handler never sees a body it would have to
        // re-check, and the caller gets a 400 rather than whatever the service would have thrown.
        var (result, handlerRan) = await InvokeAsync(ContextFor(new ThingRequest("")));

        Assert.False(handlerRan);

        var problem = Assert.IsType<ProblemHttpResult>(result);

        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task Errors_are_named_the_way_the_caller_wrote_them()
    {
        // FluentValidation reports the CLR property (Name); the caller sent JSON (name). An error
        // the client cannot attach to its own field is one it can only show as a sentence.
        var (result, _) = await InvokeAsync(ContextFor(new ThingRequest(new string('x', 9))));

        var problem = Assert.IsType<ProblemHttpResult>(result);
        var errors = Assert.IsAssignableFrom<HttpValidationProblemDetails>(problem.ProblemDetails).Errors;

        Assert.True(errors.ContainsKey("name"));
        Assert.DoesNotContain("Name", errors.Keys);
    }

    [Fact]
    public async Task A_missing_body_is_a_validation_failure_rather_than_a_crash()
    {
        // Failure path: nothing of the expected type among the arguments. Dereferencing it would be
        // a 500 for what is plainly a bad request.
        var (result, handlerRan) = await InvokeAsync(ContextFor("not the expected type"));

        Assert.False(handlerRan);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ProblemHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task A_missing_validator_registration_fails_loudly()
    {
        // Failure path: a validator written but never registered would otherwise let every body
        // through unchecked, which is worse than an exception naming the missing registration.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeAsync(ContextFor(new ThingRequest("ok"), registerValidator: false)));

        Assert.Contains("AddGridCoreValidator", thrown.Message, StringComparison.Ordinal);
    }
}
