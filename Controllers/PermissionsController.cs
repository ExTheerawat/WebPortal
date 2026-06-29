using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SingleSignOn.Models;
using SingleSignOn.Services;

namespace SingleSignOn.Controllers;

[Authorize]
public class PermissionsController : Controller
{
    private readonly IPermissionRepository _repo;
    private readonly UserAccessor _access;
    private readonly SsoOptions _opt;

    public PermissionsController(IPermissionRepository repo, UserAccessor access, IOptions<SsoOptions> opt)
    {
        _repo = repo;
        _access = access;
        _opt = opt.Value;
    }

    private async Task<bool> IsAdminAsync() => (await _access.GetAsync()).IsAdmin;

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (!await IsAdminAsync()) return RedirectToAction("Index", "Home");
        return View(new PermissionsPageViewModel
        {
            Users = await _repo.GetUsersAsync(),
            UnassignedUsers = await _repo.GetUnassignedUsersAsync(),
            AllApps = _opt.Apps,
            DefaultApps = _opt.DefaultApps
        });
    }

    // Save a user's permissions — used by both the "add" form and the edit page (upsert by key).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int userKey, bool isAdmin, bool isActive, string[]? apps)
    {
        if (!await IsAdminAsync()) return RedirectToAction("Index", "Home");
        if (userKey > 0)
            await _repo.SaveUserAsync(new UserPermission
            {
                UserKey = userKey,
                IsAdmin = isAdmin,
                IsActive = isActive,
                Apps = (apps ?? Array.Empty<string>()).ToList()
            });
        return RedirectToAction("Index");
    }

    [HttpGet]
    public async Task<IActionResult> UserEdit(int id)
    {
        if (!await IsAdminAsync()) return RedirectToAction("Index", "Home");
        var u = await _repo.GetUserAsync(id);
        if (u is null) return RedirectToAction("Index");
        return View(new UserEditViewModel { User = u, AllApps = _opt.Apps });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int userKey)
    {
        if (!await IsAdminAsync()) return RedirectToAction("Index", "Home");
        if (userKey > 0) await _repo.DeleteUserAsync(userKey);
        return RedirectToAction("Index");
    }
}
