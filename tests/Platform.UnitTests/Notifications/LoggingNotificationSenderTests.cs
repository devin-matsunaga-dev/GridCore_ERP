using GridCore.Platform.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GridCore.Platform.UnitTests.Notifications;

public class LoggingNotificationSenderTests
{
    private sealed class CapturingLogger : ILogger<LoggingNotificationSender>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task The_stub_records_the_channel_recipient_and_message()
    {
        var logger = new CapturingLogger();
        var sender = new LoggingNotificationSender(logger);

        await sender.SendAsync(new Notification(
            NotificationChannels.Email,
            "manager@gridcore.test",
            "Approval needed: billing.adjustment",
            "Bill B-1001 would go from 120.50 to 95.00."));

        var message = Assert.Single(logger.Messages);

        Assert.Contains(NotificationChannels.Email, message, StringComparison.Ordinal);
        Assert.Contains("manager@gridcore.test", message, StringComparison.Ordinal);
        Assert.Contains("Approval needed: billing.adjustment", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sending_nothing_is_a_programming_error_not_a_silent_no_op()
    {
        var sender = new LoggingNotificationSender(NullLogger<LoggingNotificationSender>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sender.SendAsync(null!));
    }
}
