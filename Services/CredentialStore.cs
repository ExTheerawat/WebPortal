using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace SingleSignOn.Services;

/// <summary>
/// Holds the signed-in user's credentials so the portal can establish a session in each
/// downstream app on demand.
///
/// SECURITY: the password is encrypted with Data Protection (keys only this server holds,
/// persisted under keys/). It lives server-side in session state; the client holds only an
/// opaque session id. With "remember me" the same sealed blob is ALSO written to a durable,
/// HttpOnly cookie so the SSO session can be rebuilt after the browser or server restarts —
/// the cookie is unreadable to the client and is wiped on Logout.
/// </summary>
public class CredentialStore
{
    private const string Key = "sso_creds";
    private const string RememberCookie = ".SSO.Remember";
    private static readonly TimeSpan RememberFor = TimeSpan.FromDays(365);

    private readonly IHttpContextAccessor _http;
    private readonly IDataProtector _protector;

    public CredentialStore(IHttpContextAccessor http, IDataProtectionProvider dp)
    {
        _http = http;
        _protector = dp.CreateProtector("SingleSignOn.Credentials.v1");
    }

    private record Creds(string User, string Pass);

    public void Save(string user, string pass, bool persist = false)
    {
        var ctx = _http.HttpContext!;
        var blob = _protector.Protect(JsonSerializer.Serialize(new Creds(user, pass)));
        ctx.Session.SetString(Key, blob);

        if (persist)
            ctx.Response.Cookies.Append(RememberCookie, blob, new CookieOptions
            {
                HttpOnly = true,
                Secure = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/",
                MaxAge = RememberFor,
                Expires = DateTimeOffset.UtcNow.Add(RememberFor),
            });
        else
            DeleteRememberCookie(ctx); // un-ticking "remember me" forgets a previous one
    }

    public (string User, string Pass)? Get()
    {
        var ctx = _http.HttpContext;
        if (ctx is null) return null;

        // Session is the fast path; fall back to the durable cookie after a restart wiped it.
        var blob = ctx.Session.GetString(Key);
        var fromCookie = string.IsNullOrEmpty(blob);
        if (fromCookie) blob = ctx.Request.Cookies[RememberCookie];
        if (string.IsNullOrEmpty(blob)) return null;

        try
        {
            var creds = JsonSerializer.Deserialize<Creds>(_protector.Unprotect(blob));
            if (creds is null) return null;
            if (fromCookie) ctx.Session.SetString(Key, blob); // re-cache into session
            return (creds.User, creds.Pass);
        }
        catch
        {
            return null;
        }
    }

    public void Clear()
    {
        var ctx = _http.HttpContext;
        ctx?.Session.Remove(Key);
        if (ctx is not null) DeleteRememberCookie(ctx);
    }

    private static void DeleteRememberCookie(HttpContext ctx)
    {
        if (ctx.Request.Cookies.ContainsKey(RememberCookie))
            ctx.Response.Cookies.Delete(RememberCookie, new CookieOptions { Path = "/" });
    }
}
