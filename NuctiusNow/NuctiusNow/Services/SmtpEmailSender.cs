using System.Net;
using System.Net.Mail;
using System.Text;

namespace NuctiusNow.Services;

public interface IEmailSender
{
    Task SendHtmlAsync(string recipient, string subject, string htmlBody, CancellationToken cancellationToken = default);
}

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailSender> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendHtmlAsync(string recipient, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        var enabled = _configuration.GetValue<bool?>("Smtp:Enabled") ?? false;
        if (!enabled)
            throw new InvalidOperationException("Email delivery is not configured. Set Smtp:Enabled to true.");

        var host = _configuration["Smtp:Host"];
        var port = _configuration.GetValue<int?>("Smtp:Port") ?? 587;
        var username = _configuration["Smtp:Username"];
        var password = _configuration["Smtp:Password"];
        var fromEmail = _configuration["Smtp:FromEmail"];
        var fromName = _configuration["Smtp:FromName"] ?? "NuctiusNow";
        var enableSsl = _configuration.GetValue<bool?>("Smtp:EnableSsl") ?? true;

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fromEmail))
            throw new InvalidOperationException("SMTP configuration is incomplete.");

        using var message = new MailMessage
        {
            From = new MailAddress(fromEmail, fromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };
        message.To.Add(new MailAddress(recipient));

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = enableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        if (!string.IsNullOrWhiteSpace(username))
            client.Credentials = new NetworkCredential(username, password);

        await client.SendMailAsync(message, cancellationToken);
        _logger.LogInformation("Activation email sent to {Recipient}.", recipient);
    }
}
