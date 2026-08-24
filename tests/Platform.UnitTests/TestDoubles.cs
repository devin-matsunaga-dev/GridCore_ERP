using GridCore.Platform.Notifications;
using GridCore.Platform.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace GridCore.Platform.UnitTests;

/// <summary>A caller with an explicit identity and permission set, so tests never build a token.</summary>
public sealed class FakeCurrentUser(string userId, string? userName = null, params string[] permissions) : ICurrentUser
{
    private readonly HashSet<string> _permissions = [.. permissions];

    public string UserId { get; } = userId;

    public string? UserName { get; } = userName ?? userId;

    public bool HasPermission(string permission) => _permissions.Contains(permission);
}

/// <summary>Captures what was sent instead of sending it.</summary>
public sealed class RecordingNotificationSender : INotificationSender
{
    public List<Notification> Sent { get; } = [];

    public Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        Sent.Add(notification);

        return Task.CompletedTask;
    }
}

/// <summary>A clock the test moves by hand, so nothing waits on wall time.</summary>
public sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>
/// A host environment a test names, so the Development-only rules (migrations at startup, demo
/// seeding) can be exercised from both sides without a host.
/// </summary>
public sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;

    public string ApplicationName { get; set; } = "GridCore.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
