using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SingleSignOn.Models;
using SingleSignOn.Services;

namespace SingleSignOn.Controllers;

public class AccountController : Controller
{
    private readonly IAppLoginService _login;
    private readonly CredentialStore _creds;
    private readonly SsoOptions _opt;
    private readonly IPasswordResetRepository _reset;
    private readonly IEmailSender _email;
    private readonly IConfiguration _config;
    private readonly ILogger<AccountController> _log;

    public AccountController(IAppLoginService login, CredentialStore creds, IOptions<SsoOptions> opt,
        IPasswordResetRepository reset, IEmailSender email, IConfiguration config,
        ILogger<AccountController> log)
    {
        _login = login;
        _creds = creds;
        _opt = opt.Value;
        _reset = reset;
        _email = email;
        _config = config;
        _log = log;
    }

    [HttpGet]
    public IActionResult Login()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");
        return View(new LoginViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return View(model);

        var ok = await _login.ValidateAsync(model.Username.Trim(), model.Password, ct);
        if (!ok)
        {
            model.Error = "ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง (หรือระบบปลายทางไม่ตอบสนอง)";
            model.Password = "";
            return View(model);
        }

        // Remember the credentials so each app can be entered on demand. When "remember me" is on
        // they are sealed into a durable cookie too, so the SSO session survives a restart.
        _creds.Save(model.Username.Trim(), model.Password, model.RememberMe);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, model.Username.Trim()),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                // Remember me → a persistent 1-year cookie that the browser keeps after it closes
                // and that slides forward on every visit, so it never lapses for an active user.
                // Otherwise a session cookie that dies when the browser closes.
                IsPersistent = model.RememberMe,
                ExpiresUtc = model.RememberMe ? DateTimeOffset.UtcNow.AddDays(365) : null,
                AllowRefresh = true,
            });

        return RedirectToAction("Index", "Home");
    }

    // ----- Forgot / Reset password -------------------------------------------------

    [HttpGet]
    [AllowAnonymous]
    public IActionResult ForgotPassword() => View(new ForgotPasswordViewModel());

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(model);

        var enteredEmail = model.Email.Trim();
        var user = await _reset.FindUserByEmailAsync(enteredEmail);
        if (user is null)
        {
            model.Error = "ไม่พบอีเมลนี้ในระบบ"; // explicit per requirement
            return View(model);
        }

        // Key the reset on the e-mail the user logs in with (= log_users.user_name), so the
        // log_users row is matched reliably even if AspNetUsers.UserName differs from it.
        var rawToken = NewToken();
        var minutes = _config.GetValue("PasswordReset:TokenLifetimeMinutes", 0);
        DateTime? expires = minutes > 0 ? DateTime.UtcNow.AddMinutes(minutes) : null;
        await _reset.CreateTokenAsync(enteredEmail, Sha256Hex(rawToken), expires);

        var link = Url.Action("ResetPassword", "Account",
            new { token = rawToken }, Request.Scheme, Request.Host.Value)!;

        try
        {
            await _email.SendAsync(user.Email,
                "ตั้งรหัสผ่านใหม่ — CCP Business Group Portal",
                BuildResetEmailHtml(link), ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to send password-reset e-mail to {Email}.", user.Email);
            model.Error = "ส่งอีเมลไม่สำเร็จ กรุณาลองใหม่อีกครั้ง หรือติดต่อผู้ดูแลระบบ";
            return View(model);
        }

        return View("ForgotPasswordSent", model);
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword(string? token)
    {
        if (string.IsNullOrEmpty(token)) return View("ResetInvalid");
        var row = await _reset.GetValidTokenAsync(Sha256Hex(token));
        if (row is null) return View("ResetInvalid");
        return View(new ResetPasswordViewModel { Token = token });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        // Re-check the token (it may have been used/expired between GET and POST).
        var row = await _reset.GetValidTokenAsync(Sha256Hex(model.Token));
        if (row is null) return View("ResetInvalid");

        // Reproduce BOTH of the destination app's password representations from the new plaintext.
        var identityHash = LegacyPasswordCrypto.HashIdentityV2(model.NewPassword);
        var stamp = Guid.NewGuid().ToString();
        var cipher = LegacyPasswordCrypto.EncryptOneSecurity(model.NewPassword);

        bool ok;
        try
        {
            ok = await _reset.UpdatePasswordAsync(row.Email, identityHash, stamp, cipher);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Password reset write failed for {Email}.", row.Email);
            model.Error = "เปลี่ยนรหัสผ่านไม่สำเร็จ กรุณาลองใหม่ หรือติดต่อผู้ดูแลระบบ";
            return View(model);
        }
        if (!ok)
        {
            model.Error = "ไม่พบบัญชีผู้ใช้สำหรับตั้งรหัสผ่าน กรุณาติดต่อผู้ดูแลระบบ";
            return View(model);
        }

        await _reset.MarkTokenUsedAsync(row.Id); // burn the token only after the write committed
        return View("ResetSuccess");
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); // url-safe
    }

    private static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string BuildResetEmailHtml(string link) => $@"
<div style=""background-color:#f4f4f5;padding:30px 12px;font-family:'Segoe UI',Tahoma,Arial,sans-serif;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""max-width:540px;margin:0 auto;border-collapse:separate;"">
    <tr><td>
      <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background-color:#ffffff;border-radius:16px;overflow:hidden;box-shadow:0 12px 34px rgba(0,0,0,0.16);border:1px solid #e7e2d4;"">

        <!-- header: black + gold wordmark -->
        <tr>
          <td style=""background-color:#15110b;background-image:linear-gradient(135deg,#241b0d 0%,#15110b 62%,#0b0906 100%);padding:32px 30px 28px;border-bottom:3px solid #caa12f;text-align:center;"">
            <div style=""color:#e2c25c;font-size:23px;letter-spacing:4px;font-weight:800;"">CCP BUSINESS GROUP</div>
            <div style=""color:#9a8c5f;font-size:11px;letter-spacing:3px;font-weight:600;margin-top:7px;"">WEB PORTAL</div>
          </td>
        </tr>

        <!-- gold title ribbon -->
        <tr>
          <td style=""background-color:#caa12f;background-image:linear-gradient(90deg,#e7c969 0%,#caa12f 100%);padding:13px 30px;text-align:center;"">
            <span style=""color:#15110b;font-size:17px;font-weight:800;letter-spacing:.4px;"">🔐 ตั้งรหัสผ่านใหม่</span>
          </td>
        </tr>

        <!-- body -->
        <tr>
          <td style=""padding:30px 34px 10px;"">
            <p style=""margin:0 0 8px;font-size:15px;line-height:1.75;color:#4b5563;"">
              เราได้รับคำขอตั้งรหัสผ่านใหม่สำหรับบัญชีของคุณในระบบ
              <b style=""color:#15110b;"">CCP Business Group Portal</b><br>กดปุ่มด้านล่างเพื่อตั้งรหัสผ่านใหม่
            </p>

            <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" align=""center"" style=""margin:24px auto;"">
              <tr><td style=""background-color:#caa12f;background-image:linear-gradient(135deg,#f3dd86 0%,#caa12f 55%,#a9781a 100%);border-radius:12px;box-shadow:0 7px 18px rgba(169,120,26,0.42);"">
                <a href=""{link}"" style=""display:inline-block;padding:15px 42px;color:#1a1205;font-size:16px;font-weight:800;text-decoration:none;letter-spacing:.3px;"">ตั้งรหัสผ่านใหม่ →</a>
              </td></tr>
            </table>

            <p style=""margin:16px 0 6px;color:#9aa0a6;font-size:12.5px;"">หรือคัดลอกลิงก์นี้ไปวางในเบราว์เซอร์:</p>
            <p style=""margin:0;word-break:break-all;font-size:12.5px;""><a href=""{link}"" style=""color:#a9781a;"">{link}</a></p>

            <div style=""border-top:1px solid #eeeae0;margin:24px 0 0;""></div>
            <p style=""margin:16px 0 6px;color:#9ca3af;font-size:12px;line-height:1.65;"">
              ถ้าคุณไม่ได้เป็นผู้ร้องขอ โปรดเพิกเฉยอีเมลฉบับนี้ — รหัสผ่านของคุณจะไม่ถูกเปลี่ยน<br>อีเมลฉบับนี้ส่งโดยอัตโนมัติ กรุณาอย่าตอบกลับ
            </p>
          </td>
        </tr>

        <!-- footer: black + gold -->
        <tr>
          <td style=""background-color:#15110b;padding:15px 30px;text-align:center;"">
            <span style=""color:#caa12f;font-size:11px;letter-spacing:1.5px;"">© CCP BUSINESS GROUP · WEB PORTAL</span>
          </td>
        </tr>

      </table>
    </td></tr>
  </table>
</div>";

    /// <summary>
    /// Single Logout: clears the portal session, deletes every cookie the portal planted
    /// on .penso.co.th (logs out the cookie-based apps), then the rendered page pings each
    /// app's own logout URL (clears server-side sessions like Internal) and returns to login.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Logout(string? returnUrl = null)
    {
        _creds.Clear();
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // Stop the browser caching anything and ask it to wipe cookies/cache/storage for the
        // whole penso.co.th site (Clear-Site-Data clears the registrable domain on modern browsers).
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "-1";
        Response.Headers["Clear-Site-Data"] = "\"cookies\", \"storage\", \"cache\"";

        // Expire the planted parent-domain auth cookies → KPI / Helpdesk / Leave are signed out.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".AspNetCore.Cookies", ".AspNet.ApplicationCookie"
        };
        foreach (var app in _opt.Apps)
            foreach (var c in app.TransplantCookies)
                names.Add(c);

        var secure = _opt.RequireHttps || Request.IsHttps;
        foreach (var n in names)
            Response.Headers.Append("Set-Cookie",
                $"{n}=; Domain={_opt.CookieDomain}; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT;{(secure ? " Secure;" : "")} HttpOnly; SameSite=Lax");

        // Apps that keep their own server-side session/host cookie (e.g. Internal) need a real
        // logout request from the browser — the view pings these, then redirects to /Account/Login.
        var logoutUrls = _opt.Apps
            .Where(a => !string.IsNullOrWhiteSpace(a.LogoutUrl))
            .Select(a => a.LogoutUrl)
            .ToList();

        // Land back on the originating app's OWN login page when asked. Validated against the
        // configured app hosts so it can't be abused as an open redirect; else the SSO login.
        var dest = Url.Action("Login", "Account")!;
        if (!string.IsNullOrWhiteSpace(returnUrl)
            && Uri.TryCreate(returnUrl, UriKind.Absolute, out var ru)
            && _opt.Apps.Any(a => Uri.TryCreate(a.Url, UriKind.Absolute, out var au)
                                  && au.Host.Equals(ru.Host, StringComparison.OrdinalIgnoreCase)))
        {
            dest = returnUrl;
        }
        ViewBag.ReturnUrl = dest;

        return View("Logout", logoutUrls);
    }
}
