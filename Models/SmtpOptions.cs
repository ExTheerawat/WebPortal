namespace SingleSignOn.Models;

/// <summary>SMTP relay settings for outbound mail (password-reset links).</summary>
public class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public bool EnableSsl { get; set; } = false;          // internal relays are often plain
    public string User { get; set; } = "";                // empty = connect without auth
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "no-reply@penso.co.th";
    public string FromName { get; set; } = "CCP Business Group Portal";
}
