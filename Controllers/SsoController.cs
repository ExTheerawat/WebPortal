using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SingleSignOn.Models;
using SingleSignOn.Services;

namespace SingleSignOn.Controllers;

[Authorize]
public class SsoController : Controller
{
    private readonly SsoOptions _opt;
    private readonly IAppLoginService _login;
    private readonly CredentialStore _creds;
    private readonly UserAccessor _access;
    private readonly ILogger<SsoController> _log;

    public SsoController(IOptions<SsoOptions> opt, IAppLoginService login, CredentialStore creds, UserAccessor access, ILogger<SsoController> log)
    {
        _opt = opt.Value;
        _login = login;
        _creds = creds;
        _access = access;
        _log = log;
    }

    /// <summary>
    /// Establish a session in the chosen app (without it ever showing its own login page)
    /// then hand the browser off to that app.
    /// </summary>
    [HttpGet("/go/{key}")]
    public async Task<IActionResult> Go(string key, CancellationToken ct)
    {
        var app = _opt.Find(key);
        if (app is null) return NotFound();

        var creds = _creds.Get();
        if (creds is null)
            return RedirectToAction("Login", "Account");

        // Role check: the user may only enter apps their role permits.
        var access = await _access.GetAsync();
        if (!access.CanSee(key))
            return RedirectToAction("Index", "Home");

        var (user, pass) = creds.Value;
        var secure = _opt.RequireHttps || Request.IsHttps;

        try
        {
            if (app.Strategy == SsoStrategy.GatewayRedirect)
            {
                // The app signs the user in by e-mail via its own gateway endpoint and
                // sets its own session cookie. Nothing to plant on our side.
                //var url = app.GatewayUrl.Replace("{email}", Uri.EscapeDataString(user));
                var url = app.GatewayUrl.Replace("{email}", Uri.EscapeDataString(user.Trim().ToLowerInvariant()));
                return Redirect(url);
            }

            if (app.Strategy == SsoStrategy.Transplant)
            {
                var result = await _login.TransplantAsync(app, user, pass, ct);
                if (!result.Success)
                    return SsoFailed(app);

                // Re-emit the app's auth cookie scoped to the parent domain so the app
                // recognises the session when the browser navigates to it. Session-lifetime.
                foreach (var c in result.Cookies)
                    PlantCookie(c.Name, c.Value, secure, maxAge: null);

                return Redirect(result.HomeUrl);
            }
            else // AutoLogin: let the browser log in to the app so it sets its own host cookie.
            {
                var prep = await _login.PrepareAutoLoginAsync(app, user, pass, ct);

                // Antiforgery cookie only needs to outlive the browser's immediate POST;
                // a short life avoids lingering next to the app's own later cookie.
                foreach (var c in prep.PlantCookies)
                    PlantCookie(c.Name, c.Value, secure, maxAge: TimeSpan.FromMinutes(2));

                return View("AutoSubmit", prep);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SSO into {App} failed.", app.Key);
            return SsoFailed(app);
        }
    }

    /// <summary>Write a Set-Cookie header manually so the raw (base64) value is never re-encoded.</summary>
    private void PlantCookie(string name, string value, bool secure, TimeSpan? maxAge)
    {
        var sb = new StringBuilder();
        sb.Append(name).Append('=').Append(value);
        sb.Append("; Domain=").Append(_opt.CookieDomain);
        sb.Append("; Path=/");
        if (maxAge.HasValue) sb.Append("; Max-Age=").Append((int)maxAge.Value.TotalSeconds);
        if (secure) sb.Append("; Secure");
        sb.Append("; HttpOnly");
        sb.Append("; SameSite=Lax");
        Response.Headers.Append("Set-Cookie", sb.ToString());
    }

    private IActionResult SsoFailed(AppDefinition app)
    {
        ViewBag.App = app;
        Response.StatusCode = 502;
        return View("SsoError");
    }
}
