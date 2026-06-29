using Dapper;
using Microsoft.Data.SqlClient;

namespace SingleSignOn.Services;

/// <summary>An account eligible for a password reset (resolved from one_leave.dbo.AspNetUsers).</summary>
public record ResetUser(string AspNetUserId, string Email);

/// <summary>A still-valid reset token row.</summary>
public record ResetTokenRow(int Id, string Email);

public interface IPasswordResetRepository
{
    Task<ResetUser?> FindUserByEmailAsync(string email);
    Task CreateTokenAsync(string email, string tokenHash, DateTime? expiresUtc);
    Task<ResetTokenRow?> GetValidTokenAsync(string tokenHash);
    Task MarkTokenUsedAsync(int id);
    Task<bool> UpdatePasswordAsync(string email, string identityHash, string securityStamp, string oneSecurityCipher);
}

/// <summary>
/// Reset-token bookkeeping (in Webportal) plus the actual password write into the two
/// one_leave tables. Reuses the same "Webportal" connection as <see cref="PermissionRepository"/>;
/// one_leave is on the same SQL instance, so cross-database writes work over this connection.
/// </summary>
public class PasswordResetRepository : IPasswordResetRepository
{
    private readonly string _cs;
    private readonly ILogger<PasswordResetRepository> _log;

    public PasswordResetRepository(IConfiguration config, ILogger<PasswordResetRepository> log)
    {
        _cs = config.GetConnectionString("Webportal")
              ?? throw new InvalidOperationException("Missing ConnectionStrings:Webportal");
        _log = log;
    }

    private SqlConnection Conn() => new(_cs);
    private static string Norm(string email) => (email ?? "").Trim().ToLowerInvariant();

    /// <summary>Confirm the e-mail belongs to a real account; returns its canonical UserName.</summary>
    public async Task<ResetUser?> FindUserByEmailAsync(string email)
    {
        using var db = Conn();
        return await db.QueryFirstOrDefaultAsync<ResetUser>(
            @"SELECT TOP 1 CONVERT(nvarchar(128), u.Id) AS AspNetUserId, u.UserName AS Email
              FROM one_leave.dbo.AspNetUsers u
              WHERE LOWER(u.UserName) = @e OR LOWER(u.Email) = @e",
            new { e = Norm(email) });
    }

    /// <summary>Invalidate any earlier unused tokens for this e-mail, then store the new one (hashed).</summary>
    public async Task CreateTokenAsync(string email, string tokenHash, DateTime? expiresUtc)
    {
        using var db = Conn();
        db.Open();
        using var tx = db.BeginTransaction();
        await db.ExecuteAsync(
            "UPDATE dbo.PasswordResets SET UsedAt = SYSUTCDATETIME() WHERE Email = @e AND UsedAt IS NULL",
            new { e = email }, tx);
        await db.ExecuteAsync(
            "INSERT dbo.PasswordResets (TokenHash, Email, ExpiresUtc) VALUES (@h, @e, @x)",
            new { h = tokenHash, e = email, x = expiresUtc }, tx);
        tx.Commit();
    }

    /// <summary>Fetch a token row only if it is unused and unexpired.</summary>
    public async Task<ResetTokenRow?> GetValidTokenAsync(string tokenHash)
    {
        using var db = Conn();
        return await db.QueryFirstOrDefaultAsync<ResetTokenRow>(
            @"SELECT TOP 1 Id, Email FROM dbo.PasswordResets
              WHERE TokenHash = @h AND UsedAt IS NULL AND (ExpiresUtc IS NULL OR ExpiresUtc > SYSUTCDATETIME())",
            new { h = tokenHash });
    }

    public async Task MarkTokenUsedAsync(int id)
    {
        using var db = Conn();
        await db.ExecuteAsync(
            "UPDATE dbo.PasswordResets SET UsedAt = SYSUTCDATETIME() WHERE Id = @id AND UsedAt IS NULL",
            new { id });
    }

    /// <summary>
    /// Write the new password into BOTH one_leave tables in a single transaction so the two
    /// representations can never diverge. Mirrors the destination app's own ChangePassword:
    /// AspNetUsers.PasswordHash (+ a fresh SecurityStamp) and the legacy log_users upsert.
    /// </summary>
    public async Task<bool> UpdatePasswordAsync(string email, string identityHash, string securityStamp, string oneSecurityCipher)
    {
        using var db = Conn();
        db.Open();
        using var tx = db.BeginTransaction();

        var aspNet = await db.ExecuteAsync(
            @"UPDATE one_leave.dbo.AspNetUsers
              SET PasswordHash = @hash, SecurityStamp = @stamp
              WHERE LOWER(UserName) = @e OR LOWER(Email) = @e",
            new { hash = identityHash, stamp = securityStamp, e = Norm(email) }, tx);

        var legacy = await db.ExecuteAsync(
            @"UPDATE one_leave.dbo.log_users SET user_password = @pwd, active = 1
              WHERE LOWER(user_name) = @e",
            new { pwd = oneSecurityCipher, e = Norm(email) }, tx);

        if (legacy == 0) // mirror the legacy loguser(): insert if the row is missing
            await db.ExecuteAsync(
                @"INSERT one_leave.dbo.log_users (user_name, user_password, active)
                  VALUES (@e, @pwd, 1)",
                new { e = email.Trim(), pwd = oneSecurityCipher }, tx);

        tx.Commit();

        if (aspNet == 0)
            _log.LogWarning("Password reset: no AspNetUsers row updated for {Email}.", email);
        return aspNet > 0;
    }
}
