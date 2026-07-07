namespace SingleSignOn.Models;

/// <summary>
/// Strategy used to give the browser a valid session for a target app
/// WITHOUT modifying that app.
/// </summary>
public enum SsoStrategy
{
    /// <summary>
    /// Portal logs in server-side, then re-emits the app's auth cookie as a
    /// parent-domain (.penso.co.th) cookie. Only safe when the app's auth
    /// cookie name is unique across all apps (Leave, Internal).
    /// </summary>
    Transplant,

    /// <summary>
    /// Browser submits the login form to the app itself (so the app sets its
    /// OWN host-scoped cookie). Required when two apps share a cookie name
    /// (KPI &amp; Helpdesk both use ".AspNetCore.Cookies").
    /// </summary>
    AutoLogin,

    /// <summary>
    /// The app already exposes a gateway/SSO endpoint that signs a user in by
    /// e-mail alone. Just redirect the browser there — the app sets its own
    /// session, no password and no cookie handling on our side. (Internal)
    /// </summary>
    GatewayRedirect
}

public enum LoginEngine
{
    /// <summary>ASP.NET Core / MVC5 form login with __RequestVerificationToken.</summary>
    CoreMvc,

    /// <summary>Classic WebForms login with __VIEWSTATE / __EVENTVALIDATION.</summary>
    WebForms
}

public class AppDefinition
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Color { get; set; } = "#334155";

    public SsoStrategy Strategy { get; set; } = SsoStrategy.AutoLogin;
    public LoginEngine Engine { get; set; } = LoginEngine.CoreMvc;

    public string LoginGetUrl { get; set; } = "";
    public string LoginPostUrl { get; set; } = "";
    public string HomeUrl { get; set; } = "";

    /// <summary>For GatewayRedirect: URL template signing in by e-mail. Use {email} placeholder,
    /// or {token} for a signed short-lived JWT (requires <see cref="GatewaySecret"/>).</summary>
    public string GatewayUrl { get; set; } = "";

    /// <summary>HMAC key shared with a {token} gateway app: standard base64 of 32 random bytes.</summary>
    public string GatewaySecret { get; set; } = "";

    /// <summary>Optional GET logout URL of the app — hit on portal logout to clear that app's own session.</summary>
    public string LogoutUrl { get; set; } = "";

    public string UserField { get; set; } = "Username";
    public string PassField { get; set; } = "Password";

    /// <summary>Static extra fields to post (RememberMe, btnLogin, chkKeepLogin, ...).</summary>
    public Dictionary<string, string> ExtraFields { get; set; } = new();

    /// <summary>Cookie name(s) that carry the authenticated session for Transplant apps.</summary>
    public List<string> TransplantCookies { get; set; } = new();
}

public class SsoOptions
{
    public const string SectionName = "Sso";

    /// <summary>Parent domain the planted cookies are scoped to. e.g. ".penso.co.th".</summary>
    public string CookieDomain { get; set; } = ".penso.co.th";

    /// <summary>Emit planted cookies with the Secure flag (production / HTTPS).</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>App keys used (in order) to validate the password at portal login.</summary>
    public List<string> ValidateApps { get; set; } = new();

    /// <summary>App keys pre-selected by default when granting a new user (Leave, KPI, Internal).</summary>
    public List<string> DefaultApps { get; set; } = new();

    public List<AppDefinition> Apps { get; set; } = new();

    public AppDefinition? Find(string key) =>
        Apps.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));
}
