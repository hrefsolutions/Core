using Href.Core.Api.Modules.Notifications.Email;

namespace Href.Core.Tests.Integration;

/// <summary>
/// Thread-safe scriptable <see cref="IEmailSender"/>: records every attempt, fails a configured number of times per
/// recipient and runs an optional callback during the send.
/// </summary>
public sealed class FakeEmailSender : IEmailSender
{
    private readonly Lock _gate = new();
    private readonly List<EmailMessage> _attempts = [];
    private readonly List<EmailMessage> _sent = [];
    private readonly Dictionary<string, int> _remainingFailures = new(StringComparer.OrdinalIgnoreCase);

    public string FailureMessage { get; init; } = "SMTP server unavailable";

    /// <summary>
    /// Runs inside <see cref="SendAsync"/> after the attempt is recorded and before the scripted outcome.
    /// </summary>
    public Func<EmailMessage, CancellationToken, Task>? OnSend { get; set; }

    public IReadOnlyList<EmailMessage> Attempts
    {
        get
        {
            lock (_gate)
            {
                return _attempts.ToList();
            }
        }
    }

    public IReadOnlyList<EmailMessage> Sent
    {
        get
        {
            lock (_gate)
            {
                return _sent.ToList();
            }
        }
    }

    public void FailNext(string toEmail, int times)
    {
        lock (_gate)
        {
            _remainingFailures[toEmail] = times;
        }
    }

    public void AlwaysFail(string toEmail) => FailNext(toEmail, int.MaxValue);

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        bool fail;

        lock (_gate)
        {
            _attempts.Add(message);
            fail = _remainingFailures.TryGetValue(message.ToEmail, out var remaining) && remaining > 0;

            if (fail && remaining != int.MaxValue)
            {
                _remainingFailures[message.ToEmail] = remaining - 1;
            }
        }

        if (OnSend is not null)
        {
            await OnSend(message, cancellationToken);
        }

        if (fail)
        {
            throw new InvalidOperationException(FailureMessage);
        }

        lock (_gate)
        {
            _sent.Add(message);
        }
    }
}
