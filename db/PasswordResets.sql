/* ============================================================
   Webportal — password-reset tokens (forgot-password feature)
   Server: 172.16.10.8   DB: Webportal
   Run:    sqlcmd -S 172.16.10.8 -U sa -P *** -d Webportal -i PasswordResets.sql

   Only a SHA-256 HASH of each token is stored; the raw token lives only in the
   e-mailed link, so a DB leak cannot replay a live link. Tokens are single-use
   (UsedAt) and optionally time-limited (ExpiresUtc; NULL = never expires).
   ============================================================ */

USE Webportal;
GO

IF OBJECT_ID('dbo.PasswordResets') IS NULL
CREATE TABLE dbo.PasswordResets (
    Id         INT IDENTITY(1,1) PRIMARY KEY,
    TokenHash  CHAR(64)      NOT NULL,   -- SHA-256(raw token), lowercase hex
    Email      NVARCHAR(256) NOT NULL,   -- one_leave.dbo.AspNetUsers.UserName / log_users.user_name
    ExpiresUtc DATETIME2     NULL,       -- NULL = never expires
    UsedAt     DATETIME2     NULL,       -- set once the link is consumed (single-use)
    CreatedAt  DATETIME2     NOT NULL CONSTRAINT DF_PasswordResets_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_PasswordResets_TokenHash UNIQUE (TokenHash)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PasswordResets_Email' AND object_id = OBJECT_ID('dbo.PasswordResets'))
    CREATE INDEX IX_PasswordResets_Email ON dbo.PasswordResets (Email);
GO
