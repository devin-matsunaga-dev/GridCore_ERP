namespace GridCore.Modules.Assets.Features.Shared;

/// <summary>
/// Base of the failures the asset register's endpoints translate into ProblemDetails responses. The
/// service throws these rather than returning result objects, so a rule can be enforced in the one
/// place that knows it and still reach the caller as the right status code.
/// </summary>
public abstract class AssetRegistryException(string message) : Exception(message);

/// <summary>No asset with that id. Surfaces as 404.</summary>
public sealed class AssetNotFoundException(Guid id)
    : AssetRegistryException($"Asset '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid AssetId { get; } = id;
}

/// <summary>
/// The register is not in a state that allows what was asked — an illegal status transition, or a
/// tag or serial number already taken. Surfaces as 409.
/// </summary>
public sealed class AssetWorkflowException(string message) : AssetRegistryException(message);

/// <summary>
/// The asset as described could not be registered. Surfaces as 400. Edge validation catches most of
/// these first; this is the aggregate's own guard, which also protects a seeder or a later module
/// calling the service directly.
/// </summary>
public sealed class AssetValidationException(string message) : AssetRegistryException(message);
