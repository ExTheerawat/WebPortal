using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SingleSignOn.Models;

namespace SingleSignOn.Services;

public interface IAppLoginService
{
    /// <summary>Validate credentials by performing a real server-side login to a one_leave app.</summary>
    Task<bool> ValidateAsync(string user, string pass, CancellationToken ct);

    /// <summary>Server-side login; returns the app's auth cookie(s) to re-emit on the parent domain.</summary>
    Task<TransplantResult> TransplantAsync(AppDefinition app, string user, string pass, CancellationToken ct);

    /// <summary>Fetch antiforgery token+cookie so the browser can post the login form to the app itself.</summary>
    Task<AutoLoginPrep> PrepareAutoLoginAsync(AppDefinition app, string user, string pass, CancellationToken ct);
}

public class AppLoginService : IAppLoginService
{
    private readonly SsoOptions _opt;
    private readonly ILogger<AppLoginService> _log;

    public AppLoginService(IOptions<SsoOptions> opt, ILogger<AppLoginService> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    public async Task<bool> ValidateAsync(string user, string pass, CancellationToken ct)
    {
        foreach (var key in _opt.ValidateApps)
        {
            var app = _opt.Find(key);
            if (app is null) continue;
            try
            {
                var (success, _, _) = await ServerLoginAsync(app, user, pass, ct);
                if (success) return true;
                // A reachable app that rejected the credentials is authoritative — stop.
                return false;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Validation via {App} failed to connect; trying next.", app.Key);
            }
        }
        return false;
    }

    public async Task<TransplantResult> TransplantAsync(AppDefinition app, string user, string pass, CancellationToken ct)
    {
        var (success, container, _) = await ServerLoginAsync(app, user, pass, ct);
        var cookies = new List<CookieToPlant>();
        if (success)
        {
            var appCookies = container.GetCookies(new Uri(app.Url));
            foreach (var name in app.TransplantCookies)
            {
                var c = appCookies.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (c is not null && !string.IsNullOrEmpty(c.Value))
                    cookies.Add(new CookieToPlant(c.Name, c.Value));
            }
        }
        return new TransplantResult(success && cookies.Count > 0, cookies, app.HomeUrl);
    }

    public async Task<AutoLoginPrep> PrepareAutoLoginAsync(AppDefinition app, string user, string pass, CancellationToken ct)
    {
        var container = new CookieContainer();
        using var http = CreateClient(container);

        var html = await http.GetStringAsync(app.LoginGetUrl, ct);
        var token = ExtractInputValue(html, "__RequestVerificationToken");

        var prep = new AutoLoginPrep { AppName = app.Name, PostUrl = app.LoginPostUrl };
        prep.Fields[app.UserField] = user;
        prep.Fields[app.PassField] = pass;
        if (!string.IsNullOrEmpty(token)) prep.Fields["__RequestVerificationToken"] = token!;
        foreach (var kv in app.ExtraFields) prep.Fields[kv.Key] = kv.Value;

        // The antiforgery cookie set during the GET must accompany the browser's POST.
        foreach (Cookie c in container.GetCookies(new Uri(app.Url)))
            prep.PlantCookies.Add(new CookieToPlant(c.Name, c.Value));

        return prep;
    }

    // ----- internal helpers -----

    /// <summary>Performs a full server-side login (handles CoreMvc token or WebForms viewstate).</summary>
    private async Task<(bool Success, CookieContainer Container, string? Location)> ServerLoginAsync(
        AppDefinition app, string user, string pass, CancellationToken ct)
    {
        var container = new CookieContainer();
        using var http = CreateClient(container);

        var html = await http.GetStringAsync(app.LoginGetUrl, ct);

        var form = new Dictionary<string, string>
        {
            [app.UserField] = user,
            [app.PassField] = pass,
        };
        foreach (var kv in app.ExtraFields) form[kv.Key] = kv.Value;

        if (app.Engine == LoginEngine.CoreMvc)
        {
            var token = ExtractInputValue(html, "__RequestVerificationToken");
            if (!string.IsNullOrEmpty(token)) form["__RequestVerificationToken"] = token!;
        }
        else // WebForms
        {
            foreach (var f in new[] { "__VIEWSTATE", "__VIEWSTATEGENERATOR", "__EVENTVALIDATION" })
            {
                var v = ExtractInputValue(html, f);
                if (v is not null) form[f] = v;
            }
        }

        using var resp = await http.PostAsync(app.LoginPostUrl, new FormUrlEncodedContent(form), ct);
        var location = resp.Headers.Location?.ToString();
        var success = IsLoginSuccess(resp, location);
        _log.LogInformation("Server login {App}: status={Status} location={Loc} success={Ok}",
            app.Key, (int)resp.StatusCode, location, success);
        return (success, container, location);
    }

    /// <summary>A login succeeded if it redirected somewhere other than back to a login page.</summary>
    private static bool IsLoginSuccess(HttpResponseMessage resp, string? location)
    {
        if ((int)resp.StatusCode is >= 300 and < 400 && !string.IsNullOrEmpty(location))
            return location.IndexOf("login", StringComparison.OrdinalIgnoreCase) < 0
                && location.IndexOf("Account/Login", StringComparison.OrdinalIgnoreCase) < 0;
        return false;
    }

    private static HttpClient CreateClient(CookieContainer container)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = container,
            UseCookies = true,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        var http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        // Look like a normal browser so apps don't serve a different page.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return http;
    }

    /// <summary>Extract the value attribute of a hidden &lt;input&gt; by name (attribute order independent).</summary>
    private static string? ExtractInputValue(string html, string fieldName)
    {
        var tag = Regex.Match(html,
            $"<input\\b[^>]*\\bname=[\"']{Regex.Escape(fieldName)}[\"'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!tag.Success) return null;
        var val = Regex.Match(tag.Value, "\\bvalue=[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase);
        return val.Success ? WebUtility.HtmlDecode(val.Groups[1].Value) : "";
    }
}
