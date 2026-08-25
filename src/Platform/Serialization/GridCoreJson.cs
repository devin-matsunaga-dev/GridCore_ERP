using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Platform.Serialization;

/// <summary>
/// How GridCore reads and writes JSON on the wire.
/// </summary>
/// <remarks>
/// <para>
/// One rule, and it is about enums: <b>an enum crosses the wire as its name, in both directions.</b>
/// Every response DTO in the codebase already hand-stringifies its enums (<c>Status.ToString()</c>,
/// <c>Class.ToString()</c>) so that a client renders a word rather than an ordinal — but nothing
/// configured the reading half, so a request body naming a <c>CustomerClass</c> was refused with a
/// 400 while the response for the same record came back naming one. An API that will not accept
/// what it emits is a bug in the API, not in its callers.
/// </para>
/// <para>
/// <b>Names are not the only thing accepted.</b> <see cref="JsonStringEnumConverter"/> reads integers
/// too, so any caller that was posting ordinals — which is what the host demanded until now — keeps
/// working. And nothing that is currently *written* changes shape: no response record on any module's
/// HTTP surface exposes a raw enum, precisely because they all stringify by hand, so this converter
/// has no output to alter. That is what makes the fix safe to apply host-wide rather than DTO by DTO.
/// </para>
/// <para>
/// Deliberately NOT the audit trail's serializer. <see cref="Audit.AuditEntry.Options"/> is its own
/// instance with its own converter, because a snapshot's shape is a stored record that must not
/// change when the wire format does.
/// </para>
/// </remarks>
public static class GridCoreJson
{
    /// <summary>
    /// Applies GridCore's wire conventions to <paramref name="options"/>.
    /// </summary>
    /// <remarks>
    /// Separate from the registration below so a test can assert on the conventions without a host,
    /// and so anything that has to build its own options gets the same answer.
    /// </remarks>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Guarded so calling this twice — a test that configures its own options and a host that
        // also registers them — cannot end up with two converters racing for the same types.
        if (!options.Converters.Any(converter => converter is JsonStringEnumConverter))
        {
            options.Converters.Add(new JsonStringEnumConverter());
        }

        return options;
    }

    /// <summary>A fresh options instance carrying those conventions, over the web defaults.</summary>
    public static JsonSerializerOptions Options() => Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>
    /// Applies them to the minimal-API pipeline, so every endpoint in every module reads and writes
    /// the same way.
    /// </summary>
    public static IServiceCollection AddGridCoreJson(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ConfigureHttpJsonOptions, not AddJsonOptions: the latter configures MVC, and GridCore's
        // HTTP surface is minimal APIs throughout.
        services.ConfigureHttpJsonOptions(options => Configure(options.SerializerOptions));

        return services;
    }
}
