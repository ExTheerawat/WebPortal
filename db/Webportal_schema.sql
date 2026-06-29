/* ============================================================
   Webportal — SSO permission database (role-based access)
   Server: CCTH-EX\SQL2019
   Run:    sqlcmd -S "CCTH-EX\SQL2019" -U sa -P *** -i Webportal_schema.sql
   ============================================================ */

IF DB_ID('Webportal') IS NULL CREATE DATABASE Webportal;
GO
USE Webportal;
GO

/* Roles: a named permission group. IsAdmin = can open the "manage permissions" pages. */
IF OBJECT_ID('dbo.Roles') IS NULL
CREATE TABLE dbo.Roles (
    RoleId      INT IDENTITY(1,1) PRIMARY KEY,
    RoleName    NVARCHAR(100) NOT NULL,
    Description NVARCHAR(255) NULL,
    IsAdmin     BIT NOT NULL CONSTRAINT DF_Roles_IsAdmin  DEFAULT(0),
    IsActive    BIT NOT NULL CONSTRAINT DF_Roles_IsActive DEFAULT(1),
    CreatedAt   DATETIME NOT NULL CONSTRAINT DF_Roles_CreatedAt DEFAULT(GETDATE())
);
GO

/* RoleApps: which app keys (internal/leave/kpi/helpdesk) a role can see. */
IF OBJECT_ID('dbo.RoleApps') IS NULL
CREATE TABLE dbo.RoleApps (
    RoleId INT NOT NULL,
    AppKey NVARCHAR(50) NOT NULL,
    CONSTRAINT PK_RoleApps PRIMARY KEY (RoleId, AppKey)
);
GO

/* UserRoles: map a user to exactly one role.
   Keyed by UserKey = one_leave.dbo.log_users.user_id (stable PK) so a changed e-mail
   on the same account keeps its role. Email is stored for display. */
IF OBJECT_ID('dbo.UserRoles') IS NULL
CREATE TABLE dbo.UserRoles (
    UserKey   INT NOT NULL PRIMARY KEY,
    Email     NVARCHAR(256) NOT NULL,
    RoleId    INT NOT NULL,
    UpdatedAt DATETIME NOT NULL CONSTRAINT DF_UserRoles_UpdatedAt DEFAULT(GETDATE())
);
GO

/* Seed: Administrator role (all apps) + the first admin user. */
IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleName = 'Administrator')
BEGIN
    INSERT dbo.Roles (RoleName, Description, IsAdmin)
    VALUES ('Administrator', N'ผู้ดูแลระบบ — เห็นทุกระบบและจัดการสิทธิ์ได้', 1);

    DECLARE @rid INT = SCOPE_IDENTITY();
    INSERT dbo.RoleApps (RoleId, AppKey) VALUES (@rid,'internal'),(@rid,'leave'),(@rid,'kpi'),(@rid,'helpdesk');
    INSERT dbo.UserRoles (UserKey, Email, RoleId)
    SELECT lu.user_id, lu.user_name, @rid FROM one_leave.dbo.log_users lu
    WHERE lu.user_name = 'theerawat@ccthailand.co.th';
END
GO
