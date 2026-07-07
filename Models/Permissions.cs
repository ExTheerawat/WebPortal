namespace SingleSignOn.Models;

/// <summary>A user's direct permissions (no roles). Apps = the app keys they may see.</summary>
public class UserPermission
{
    public int UserKey { get; set; }   // one_leave.dbo.log_users.user_id (stable key)
    public string Email { get; set; } = "";
    public bool IsAdmin { get; set; }
    public bool IsActive { get; set; } = true;
    public List<string> Apps { get; set; } = new();
}

/// <summary>What the signed-in user is allowed to do (resolved from UserPermissions).</summary>
public class UserAccess
{
    public bool HasRole { get; set; }   // has an (active) permission record
    public bool IsAdmin { get; set; }
    public int UserKey { get; set; }    // one_leave.dbo.log_users.user_id (0 = unresolved)
    public HashSet<string> AppKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool CanSee(string appKey) => AppKeys.Contains(appKey);
}

/// <summary>A selectable account from one_leave.dbo.log_users (for the e-mail dropdown).</summary>
public class LogUser
{
    public int UserId { get; set; }   // log_users.user_id (PK)
    public string Email { get; set; } = "";
}

// ----- view models for the admin screen -----

public class PermissionsPageViewModel
{
    public List<UserPermission> Users { get; set; } = new();      // already-configured users
    public List<LogUser> UnassignedUsers { get; set; } = new();   // for the "add" dropdown
    public List<AppDefinition> AllApps { get; set; } = new();
    public List<string> DefaultApps { get; set; } = new();        // pre-checked for a new user
}

public class UserEditViewModel
{
    public UserPermission User { get; set; } = new();
    public List<AppDefinition> AllApps { get; set; } = new();
}
