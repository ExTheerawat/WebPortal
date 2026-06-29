using SingleSignOn.Models;

namespace SingleSignOn.Services;

/// <summary>Resolves (and caches per request) the current user's role-based access.</summary>
public class UserAccessor
{
    private readonly IPermissionRepository _repo;
    private readonly IHttpContextAccessor _http;
    private UserAccess? _cached;

    public UserAccessor(IPermissionRepository repo, IHttpContextAccessor http)
    {
        _repo = repo;
        _http = http;
    }

    public async Task<UserAccess> GetAsync()
    {
        if (_cached is not null) return _cached;
        var email = _http.HttpContext?.User?.Identity?.Name;
        _cached = string.IsNullOrEmpty(email) ? new UserAccess() : await _repo.GetAccessAsync(email);
        return _cached;
    }
}
