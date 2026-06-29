using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using SingleSignOn.Models;

namespace SingleSignOn.Services;

public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct);
}

/// <summary>Sends mail through the configured SMTP relay via MailKit (auth optional).</summary>
public class EmailService : IEmailSender
{
    private readonly SmtpOptions _opt;
    private readonly ILogger<EmailService> _log;

    public EmailService(IOptions<SmtpOptions> opt, ILogger<EmailService> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(_opt.FromName, _opt.FromAddress));
        msg.To.Add(MailboxAddress.Parse(toEmail));
        msg.Subject = subject;
        msg.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 15000 }; // don't hang on a dead relay
        // Plain for internal relays; Auto negotiates STARTTLS/SSL when EnableSsl is on.
        var security = _opt.EnableSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None;
        await client.ConnectAsync(_opt.Host, _opt.Port, security, ct);
        if (!string.IsNullOrWhiteSpace(_opt.User))
            await client.AuthenticateAsync(_opt.User, _opt.Password, ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
        _log.LogInformation("Reset e-mail sent to {To} via {Host}:{Port}.", toEmail, _opt.Host, _opt.Port);
    }
}
