using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading.Channels;

public sealed class EmailOptions
{
    public string SmtpHost { get; init; } = string.Empty;
    public int SmtpPort { get; init; } = 587;
    public bool UseStartTls { get; init; } = true;
    public string SmtpUsername { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string ManagerAddress { get; init; } = string.Empty;
    public string ManagerPageUrl { get; init; } = string.Empty;
    public string TimeZone { get; init; } = "Australia/Hobart";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SmtpHost)
        && !string.IsNullOrWhiteSpace(From)
        && !string.IsNullOrWhiteSpace(ManagerAddress);

    public bool IsPartlyConfigured =>
        !IsConfigured
        && !(string.IsNullOrWhiteSpace(SmtpHost)
            && string.IsNullOrWhiteSpace(From)
            && string.IsNullOrWhiteSpace(ManagerAddress));
}

public sealed record EnquiryNotification(
    long Id,
    string GuestName,
    string GuestEmail,
    string? GuestNote,
    string GameChoice,
    DateTimeOffset ReceivedAtUtc);

/// <summary>
/// Emails the manager about each new enquiry. Sending happens on a background queue so a slow
/// or unavailable mail server never delays or fails the guest's enquiry, which is already saved.
/// </summary>
public sealed class EnquiryNotifier : BackgroundService
{
    private const int MaxAttempts = 3;

    private readonly Channel<EnquiryNotification> queue = Channel.CreateBounded<EnquiryNotification>(
        new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly EmailOptions options;
    private readonly string? smtpPassword;
    private readonly TimeZoneInfo timeZone;
    private readonly ILogger<EnquiryNotifier> logger;

    public EnquiryNotifier(EmailOptions options, string? smtpPassword, ILogger<EnquiryNotifier> logger)
    {
        this.options = options;
        this.smtpPassword = smtpPassword;
        this.logger = logger;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning("Email: time zone {TimeZone} not found, using UTC in manager emails.", options.TimeZone);
            timeZone = TimeZoneInfo.Utc;
        }
    }

    public bool IsEnabled => options.IsConfigured;

    public void Enqueue(EnquiryNotification notification)
    {
        if (IsEnabled && !queue.Writer.TryWrite(notification))
        {
            logger.LogWarning("Email: notification queue is full, enquiry {Id} was saved but not emailed.", notification.Id);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var notification in queue.Reader.ReadAllAsync(stoppingToken))
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    await SendAsync(notification, stoppingToken);
                    logger.LogInformation("Email: manager notified about enquiry {Id}.", notification.Id);
                    break;
                }
                // Any failure here must not escape: an unhandled exception in a BackgroundService stops the API.
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Guest details stay out of the log; the enquiry id is enough to find it in the manager page.
                    logger.LogWarning(
                        "Email: attempt {Attempt} of {MaxAttempts} to notify about enquiry {Id} failed: {Error}",
                        attempt,
                        MaxAttempts,
                        notification.Id,
                        exception.Message);
                    if (attempt < MaxAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10 * attempt), stoppingToken);
                    }
                }
            }
        }
    }

    private async Task SendAsync(EnquiryNotification notification, CancellationToken cancellationToken)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(options.From, "Boxwood enquiries"),
            // Guest text never goes into a header except the validated reply-to address.
            Subject = $"New game-night enquiry #{notification.Id}",
            Body = BuildBody(notification),
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        foreach (var address in options.ManagerAddress.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            message.To.Add(new MailAddress(address));
        }

        message.ReplyToList.Add(new MailAddress(notification.GuestEmail));

        using var client = new SmtpClient(options.SmtpHost, options.SmtpPort)
        {
            EnableSsl = options.UseStartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 30_000,
        };
        if (!string.IsNullOrEmpty(options.SmtpUsername))
        {
            client.Credentials = new NetworkCredential(options.SmtpUsername, smtpPassword);
        }

        await client.SendMailAsync(message, cancellationToken);
    }

    private string BuildBody(EnquiryNotification notification)
    {
        var received = TimeZoneInfo.ConvertTime(notification.ReceivedAtUtc, timeZone);
        var match = notification.GameChoice switch
        {
            "future" => "Another home game (guest will choose)",
            "next" => "Next home game",
            _ => notification.GameChoice,
        };

        var body = new StringBuilder()
            .AppendLine($"New Stay & Play game-night enquiry #{notification.Id}")
            .AppendLine()
            .AppendLine($"Name:      {notification.GuestName}")
            .AppendLine($"Email:     {notification.GuestEmail}")
            .AppendLine($"Match:     {match}")
            .AppendLine($"Received:  {received.ToString("ddd d MMM yyyy, h:mm tt", CultureInfo.GetCultureInfo("en-AU"))} ({timeZone.Id})")
            .AppendLine()
            .AppendLine("Match / stay preferences:")
            .AppendLine(string.IsNullOrWhiteSpace(notification.GuestNote) ? "(none given)" : notification.GuestNote)
            .AppendLine()
            .AppendLine("Reply to this email to answer the guest directly.");

        if (!string.IsNullOrWhiteSpace(options.ManagerPageUrl))
        {
            body.AppendLine($"All enquiries: {options.ManagerPageUrl}");
        }

        return body.ToString();
    }
}
