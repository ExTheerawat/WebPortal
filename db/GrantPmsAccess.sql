-- Grant the "pms" app to one test user (idempotent). Run against the Webportal DB.
-- The user must already have a dbo.UserPermissions row (i.e. appears on the portal's
-- Permissions page). If not, add them there first — it resolves the account from
-- one_leave.dbo.log_users and creates the row. Then run this (or just tick PMS in the UI).

DECLARE @Email nvarchar(256) = N'arnon@p9.co.th';  -- ผู้ทดสอบ UAT รอบแรก

INSERT INTO dbo.UserApps (UserKey, AppKey)
SELECT up.UserKey, 'pms'
FROM dbo.UserPermissions up
WHERE LOWER(up.Email) = LOWER(@Email)
  AND up.IsActive = 1
  AND NOT EXISTS (SELECT 1
                  FROM dbo.UserApps ua
                  WHERE ua.UserKey = up.UserKey AND ua.AppKey = 'pms');

-- ตรวจผล: ควรเห็นแถว AppKey = 'pms' ของอีเมลนั้น
SELECT up.Email, up.IsActive, ua.AppKey
FROM dbo.UserPermissions up
JOIN dbo.UserApps ua ON ua.UserKey = up.UserKey
WHERE LOWER(up.Email) = LOWER(@Email)
ORDER BY ua.AppKey;
