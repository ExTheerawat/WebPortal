using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SingleSignOn.Models;
using SingleSignOn.Services;

namespace SingleSignOn.Controllers;

public class HomeController : Controller
{
    private readonly SsoOptions _opt;
    private readonly UserAccessor _access;

    public HomeController(IOptions<SsoOptions> opt, UserAccessor access)
    {
        _opt = opt.Value;
        _access = access;
    }

    [Authorize]
    public async Task<IActionResult> Index()
    {
        var access = await _access.GetAsync();
        // Show only the apps this user's role permits (no role ⇒ no apps).
        var visible = _opt.Apps.Where(a => access.CanSee(a.Key)).ToList();
        ViewBag.HasRole = access.HasRole;
        return View(visible);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
