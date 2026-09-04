using System.Collections.Concurrent;
using System.Net.Mail;
using AwesomeAssertions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Common.Translations;
using NewHeap.Platform.Mapping;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhEmailNotificationDispatcherTests
{
    [Fact]
    public async Task DispatchFailureIncludesExceptionDetailsInTheLogMessage()
    {
        var exception = new InvalidOperationException("SMTP diagnostic failure");
        var logger = new ListLogger<NhEmailNotificationDispatcher>();
        var dispatcher = new NhEmailNotificationDispatcher(
            Options.Create(new NhEmailNotificationSettings()),
            Substitute.For<IRepository<NhNotification>>(),
            CreateLocalizer(),
            Substitute.For<INhDbLogService>(),
            new LogHelperService(
                Substitute.For<IStringLocalizer<SharedDataAnnotationRecources>>(),
                NullLogger<LogHelperService>.Instance),
            new ValidationService(Substitute.For<IServiceProvider>()),
            Substitute.For<IMapper>(),
            logger,
            new FailingMailService(exception));

        var result = await dispatcher.DispatchAsync(new NhEmailDeliveryData
        {
            FromEmail = "sender@example.com",
            FromDisplayName = "Sample sender",
            Subject = "Notification test",
            Body = "Notification body",
            To = ["recipient@example.com"]
        });

        result.Success.Should().BeFalse();
        result.AllErrorMessages.Should().ContainSingle(message =>
            message.ToString().Contains("Failed to dispatch email notification.", StringComparison.Ordinal));

        var logEntry = logger.Entries.Should().ContainSingle().Subject;
        logEntry.Level.Should().Be(LogLevel.Error);
        logEntry.Exception.Should().BeSameAs(exception);
        logEntry.Message.Should().Contain(typeof(InvalidOperationException).FullName);
        logEntry.Message.Should().Contain(exception.Message);
        logEntry.Properties["ExceptionType"].Should().Be(typeof(InvalidOperationException).FullName);
        logEntry.Properties["ExceptionMessage"].Should().Be(exception.Message);
    }

    private static IStringLocalizer<NhEmailNotificationDispatcher> CreateLocalizer()
    {
        var localizer = Substitute.For<IStringLocalizer<NhEmailNotificationDispatcher>>();
        const string message = "Failed to dispatch email notification.";
        localizer[message].Returns(new LocalizedString(message, message));
        return localizer;
    }

    private sealed class FailingMailService(Exception exception)
        : NhMailService(
            Options.Create(new MailServiceSettings()),
            NullLogger<NhMailService>.Instance)
    {
        public override Task SendAsync(
            MailMessage mailMessage,
            MailAddress? fromMailAddress = null,
            string? formDisplayName = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException(exception);
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state as IEnumerable<KeyValuePair<string, object?>>;
            var propertyDictionary = properties?.ToDictionary(x => x.Key, x => x.Value)
                ?? new Dictionary<string, object?>();

            _entries.Enqueue(new LogEntry(
                logLevel,
                formatter(state, exception),
                propertyDictionary,
                exception));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Properties,
        Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
