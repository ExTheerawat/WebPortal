using Dapper;
using Microsoft.Data.SqlClient;
using SingleSignOn.Models;

namespace SingleSignOn.Services;

public interface IPermissionRepository
{
    Task<UserAccess> GetAccessAsync(string email);
    Task<List<UserPermission>> GetUsersAsync();
    Task<UserPermission?> GetUserAsync(int userKey);
    Task<List<LogUser>> GetUnassignedUsersAsync();
    Task SaveUserAsync(UserPermission user);
    Task DeleteUserAsync(int userKey);
}

public class PermissionRepository : IPermissionRepository
{
    private readonly string _cs;
    private readonly ILogger<PermissionRepository> _log;

    public PermissionRepository(IConfiguration config, ILogger<PermissionRepository> log)
    {
        _cs = config.GetConnectionString("Webportal")
              ?? throw new InvalidOperationException("Missing ConnectionStrings:Webportal");
        _log = log;
    }

    private SqlConnection Conn() => new(_cs);
    private static string Norm(string email) => (email ?? "").Trim().ToLowerInvariant();

    /// <summary>Resolve the login e-mail → log_users.user_id → that user's permissions.</summary>
    public async Task<UserAccess> GetAccessAsync(string email)
    {
        var access = new UserAccess();
        try
        {
            using var db = Conn();
            var rec = await db.QueryFirstOrDefaultAsync<(int UserKey, bool IsAdmin)>(
                @"SELECT TOP 1 lu.user_id AS UserKey, up.IsAdmin
                  FROM one_leave.dbo.log_users lu
                  JOIN dbo.UserPermissions up ON up.UserKey = lu.user_id
                  WHERE LOWER(lu.user_name) = @e AND lu.active = 1 AND up.IsActive = 1",
                new { e = Norm(email) });
            if (rec.UserKey == 0) return access; // no active permission → sees nothing

            access.HasRole = true;
            access.IsAdmin = rec.IsAdmin;
            var keys = await db.QueryAsync<string>(
                "SELECT AppKey FROM dbo.UserApps WHERE UserKey = @k", new { k = rec.UserKey });
            foreach (var k in keys) access.AppKeys.Add(k);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GetAccessAsync failed for {Email} — denying access.", email);
        }
        return access;
    }

    public async Task<List<UserPermission>> GetUsersAsync()
    {
        using var db = Conn();
        var users = (await db.QueryAsync<UserPermission>(
            "SELECT UserKey, Email, IsAdmin, IsActive FROM dbo.UserPermissions ORDER BY Email")).ToList();
        var apps = await db.QueryAsync<(int UserKey, string AppKey)>("SELECT UserKey, AppKey FROM dbo.UserApps");
        var byUser = apps.GroupBy(a => a.UserKey).ToDictionary(g => g.Key, g => g.Select(x => x.AppKey).ToList());
        foreach (var u in users)
            if (byUser.TryGetValue(u.UserKey, out var list)) u.Apps = list;
        return users;
    }

    public async Task<UserPermission?> GetUserAsync(int userKey)
    {
        using var db = Conn();
        var u = await db.QueryFirstOrDefaultAsync<UserPermission>(
            "SELECT UserKey, Email, IsAdmin, IsActive FROM dbo.UserPermissions WHERE UserKey = @k", new { k = userKey });
        if (u is null) return null;
        u.Apps = (await db.QueryAsync<string>(
            "SELECT AppKey FROM dbo.UserApps WHERE UserKey = @k", new { k = userKey })).ToList();
        return u;
    }

    /// <summary>Active accounts in log_users not yet granted any permission (for the add dropdown).</summary>
    public async Task<List<LogUser>> GetUnassignedUsersAsync()
    {
        using var db = Conn();
        return (await db.QueryAsync<LogUser>(
            @"SELECT lu.user_id AS UserId, lu.user_name AS Email
              FROM one_leave.dbo.log_users lu
              LEFT JOIN one_leave.employee.one_employee emp on lu.emp_id = emp.emp_id
              WHERE lu.active = 1
                    AND emp.emp_status_id = 1
                    AND lu.user_id NOT IN (SELECT UserKey FROM dbo.UserPermissions)
                    AND lu.user_name IS NOT NULL AND LTRIM(RTRIM(lu.user_name)) <> ''
              ORDER BY lu.user_name")).ToList();
    }

    /// <summary>Upsert a user's permission row (e-mail resolved from log_users by key) and replace their apps.</summary>
    public async Task SaveUserAsync(UserPermission user)
    {
        using var db = Conn();
        db.Open();
        using var tx = db.BeginTransaction();
        await db.ExecuteAsync(
            @"MERGE dbo.UserPermissions AS t
              USING (SELECT @key AS UserKey,
                            (SELECT user_name FROM one_leave.dbo.log_users WHERE user_id = @key) AS Email) AS s
              ON t.UserKey = s.UserKey
              WHEN MATCHED THEN UPDATE SET Email=s.Email, IsAdmin=@admin, IsActive=@active, UpdatedAt=GETDATE()
              WHEN NOT MATCHED THEN INSERT (UserKey, Email, IsAdmin, IsActive) VALUES (s.UserKey, s.Email, @admin, @active);",
            new { key = user.UserKey, admin = user.IsAdmin, active = user.IsActive }, tx);

        await db.ExecuteAsync("DELETE FROM dbo.UserApps WHERE UserKey = @k", new { k = user.UserKey }, tx);
        foreach (var key in user.Apps.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase))
            await db.ExecuteAsync("INSERT dbo.UserApps (UserKey, AppKey) VALUES (@k, @a)",
                new { k = user.UserKey, a = key }, tx);
        tx.Commit();
    }

    public async Task DeleteUserAsync(int userKey)
    {
        using var db = Conn();
        db.Open();
        using var tx = db.BeginTransaction();
        await db.ExecuteAsync("DELETE FROM dbo.UserApps WHERE UserKey = @k", new { k = userKey }, tx);
        await db.ExecuteAsync("DELETE FROM dbo.UserPermissions WHERE UserKey = @k", new { k = userKey }, tx);
        tx.Commit();
    }
}
