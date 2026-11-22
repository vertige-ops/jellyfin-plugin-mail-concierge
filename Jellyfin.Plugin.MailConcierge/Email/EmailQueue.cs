using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MailConcierge.Email;

/// <summary>
/// Sends e-mails in the background so that HTTP requests never wait for the SMTP server.
/// One worker, bounded retries, and no secret in the logs.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Name fixed by the design: it is a queue.")]
public sealed partial class EmailQueue : BackgroundService
{
    private const int MaxAttempts = 3;

    /// <summary>The template of the notice sent to the administrator when a daily limit is reached.</summary>
    internal const string QuotaNoticeTemplate = "quota-reached";

    private static readonly TimeSpan NoticeInterval = TimeSpan.FromHours(EmailQuota.WindowHours);
    private static readonly TimeSpan AttemptLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan[] DefaultRetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(25) };

    private readonly Channel<EmailMessage> _channel;
    private readonly int _capacity;
    private readonly Func<PluginConfiguration> _config;
    private readonly IEmailTransport _transport;
    private readonly Dictionary<string, DateTimeOffset> _noticeSentAt = new();
    private readonly object _noticeLock = new();
    private readonly ILogger<EmailQueue> _logger;
    private readonly TimeProvider _time;
    private readonly EmailStats? _stats;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailQueue"/> class.
    /// </summary>
    /// <param name="config">Accessor of the current configuration.</param>
    /// <param name="transport">The transport.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="time">The time source, used for back-off delays.</param>
    /// <param name="retryDelays">The back-off before each retry; defaults to 1, 5 and 25 seconds.</param>
    /// <param name="capacity">The number of messages that can wait for the worker; a full queue refuses new messages.</param>
    /// <param name="stats">The e-mail counters, fed with each delivered or abandoned message (not with test e-mails).</param>
    public EmailQueue(
        Func<PluginConfiguration> config,
        IEmailTransport transport,
        ILogger<EmailQueue> logger,
        TimeProvider time,
        IReadOnlyList<TimeSpan>? retryDelays = null,
        int capacity = 100,
        EmailStats? stats = null)
    {
        _capacity = capacity;
        _channel = Channel.CreateBounded<EmailMessage>(new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _config = config;
        _transport = transport;
        Quota = new EmailQuota(time);
        _logger = logger;
        _time = time;
        _retryDelays = retryDelays ?? DefaultRetryDelays;
        _stats = stats;
    }

    /// <summary>
    /// Gets the daily e-mail limits: what is counted in the last 24 hours, for the administration page.
    /// </summary>
    public EmailQuota Quota { get; }

    /// <summary>
    /// Queues a message. The message is dropped, with a warning in the log, when e-mail is not configured, when the queue is full
    /// (checked before anything is counted), or when a daily limit is reached: the limit for its recipient, or the limit for the whole server.
    /// The notices of an e-mail change and the notice to the administrator are never limited. When a limit is reached the administrator is told, at most once a day for each limit.
    /// </summary>
    /// <param name="m">The message.</param>
    /// <returns>What happened to the message.</returns>
    public EnqueueResult Enqueue(EmailMessage m)
    {
        var config = _config();
        if (!config.CanSendEmail())
        {
            _logger.LogWarning("Dropped {Template} e-mail for user {UserId}: e-mail sending is not configured", m.Template, m.UserId);
            return EnqueueResult.NotConfigured;
        }

        if (m.Template is "email-changed" or "email-change-cancelled" or QuotaNoticeTemplate)
        {
            // Security notices to the previous address never use a shared limit: otherwise someone who filled it could keep the
            // owner from being warned. They are bounded by the per-user limits of the e-mail change form. The notice to the administrator
            // must get through when the limit is reached, and is itself sent at most once a day for each limit.
            return TryQueue(m);
        }

        if (_channel.Reader.Count >= _capacity)
        {
            return RefuseFull(m);
        }

        var perRecipient = EmailQuota.Effective(config.MaxEmailsPerRecipientPerDay, EmailQuota.MaxPerRecipient, EmailQuota.DefaultPerRecipient);
        var perDay = EmailQuota.Effective(config.MaxEmailsPerDay, EmailQuota.MaxPerDay, EmailQuota.DefaultPerDay);
        var refusal = Quota.TryAcquire(m.To, perRecipient, perDay);
        if (refusal != EmailQuota.Refusal.None)
        {
            var global = refusal == EmailQuota.Refusal.Global;
            _logger.LogWarning(
                "Dropped {Template} e-mail for user {UserId}: the daily limit {Kind} ({Limit}) is reached",
                m.Template,
                m.UserId,
                global ? "for the server" : "for one recipient",
                global ? perDay : perRecipient);
            NotifyAdministrator(config, global ? "global" : "recipient", perRecipient, perDay);
            return EnqueueResult.LimitReached;
        }

        return TryQueue(m);
    }

    // The notice never holds an address of a recipient, only counts. It goes straight to the queue, so a refusal cannot loop back here.
    private void NotifyAdministrator(PluginConfiguration config, string kind, int perRecipient, int perDay)
    {
        if (!EmailService.TryNormalize(config.AdminNotificationAddress, out var to))
        {
            return;
        }

        var now = _time.GetUtcNow();
        lock (_noticeLock)
        {
            if (_noticeSentAt.TryGetValue(kind, out var last) && now - last < NoticeInterval)
            {
                return;
            }

            _noticeSentAt[kind] = now;
        }

        try
        {
            var lang = config.DefaultLanguage;
            var detail = EmailTemplates.GetString(lang, kind == "global" ? "email.quota.global" : "email.quota.recipient")
                .Replace("{limit}", (kind == "global" ? perDay : perRecipient).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var values = new Dictionary<string, string>
            {
                ["detail"] = detail,
            };
            var (subject, html, text) = EmailTemplates.Render(QuotaNoticeTemplate, lang, values, config.TemplateOverrides, config.EmailLogo);
            if (!_channel.Writer.TryWrite(new EmailMessage(to, subject, html, text, QuotaNoticeTemplate, null)))
            {
                lock (_noticeLock)
                {
                    _noticeSentAt.Remove(kind);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("The notice about the daily e-mail limit could not be prepared: {ExceptionType}", ex.GetType().FullName);
        }
    }

    private EnqueueResult TryQueue(EmailMessage m)
        => _channel.Writer.TryWrite(m) ? EnqueueResult.Queued : RefuseFull(m);

    private EnqueueResult RefuseFull(EmailMessage m)
    {
        _logger.LogWarning("Dropped {Template} e-mail for user {UserId}: the sending queue is full", m.Template, m.UserId);
        return EnqueueResult.QueueFull;
    }

    /// <summary>
    /// Sends a test e-mail immediately (not queued, not counted in the daily limits).
    /// </summary>
    /// <param name="to">The recipient.</param>
    /// <returns>Null on success, otherwise the error text (without secrets).</returns>
    public async Task<string?> SendTestAsync(string to)
    {
        var config = _config();
        if (!config.CanSendEmail())
        {
            return "E-mail sending is not configured: the public URL, the SMTP host and the sender address are required.";
        }

        var lang = config.DefaultLanguage;
        var message = new EmailMessage(
            to,
            EmailTemplates.GetString(lang, "email.test.subject"),
            TestHtml(config, EmailTemplates.GetString(lang, "email.test.body")),
            EmailTemplates.GetString(lang, "email.test.body"),
            "test",
            null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60), _time);
            await _transport.SendAsync(message, cts.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return Describe(ex, config);
        }
    }

    // The HTML of the test e-mail: the logo row of the configured mode above the text, as in the templates.
    private static string TestHtml(PluginConfiguration config, string body)
        => "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\">"
            + EmailTemplates.LogoRowFor(config.EmailLogo, EmailTemplates.LogoUrl(config.PublicUrl))
            + "<tr><td style=\"padding:16px 32px\"><p>" + System.Net.WebUtility.HtmlEncode(body).ReplaceLineEndings("<br>") + "</p></td></tr></table>";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var m in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await DeliverAsync(m, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task DeliverAsync(EmailMessage m, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                // One attempt lasts at most AttemptLimit: a server that accepts the connection and then stalls cannot hold the queue.
                // An attempt cancelled after the server already accepted the message is retried, so it can be delivered twice;
                // the two messages carry the same token, so this creates no new right.
                using var limit = new CancellationTokenSource(AttemptLimit, _time);
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
                await _transport.SendAsync(m, attemptCts.Token).WaitAsync(attemptCts.Token).ConfigureAwait(false);
                if (m.Template != QuotaNoticeTemplate)
                {
                    _stats?.RecordSent(m.Template, m.UserId);
                }

                _logger.LogInformation("Sent {Template} e-mail to user {UserId}", m.Template, m.UserId);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var error = Describe(ex, _config());
                if (attempt == MaxAttempts)
                {
                    if (m.Template != QuotaNoticeTemplate)
                    {
                        _stats?.RecordFailed(m.Template, m.UserId);
                    }

                    _logger.LogError("Failed to send {Template} e-mail to user {UserId} after {Attempts} attempts, dropped: {Error}", m.Template, m.UserId, attempt, error);
                    return;
                }

                _logger.LogWarning("Failed to send {Template} e-mail to user {UserId} (attempt {Attempt} of {Max}): {Error}", m.Template, m.UserId, attempt, MaxAttempts, error);
                var delay = _retryDelays.Count == 0 ? TimeSpan.Zero : _retryDelays[Math.Min(attempt - 1, _retryDelays.Count - 1)];
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Describes an exception by type and message only, with the SMTP password removed.
    /// </summary>
    private static string Describe(Exception ex, PluginConfiguration config)
    {
        var message = ex.Message;
        if (!string.IsNullOrEmpty(config.SmtpPassword))
        {
            message = message.Replace(config.SmtpPassword, "***", StringComparison.Ordinal);
        }

        // SMTP servers often repeat the recipient in their refusal ("550 <x@y>: Recipient address rejected"): addresses stay out of the log.
        message = AddressInText().Replace(message, "<address>");
        return $"{ex.GetType().Name}: {message}";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[^\s<>""',;()]+@[^\s<>""',;()]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex AddressInText();
}
