# สรุประบบ SSO Portal (WebPortalX / SingleSignOn) — สำหรับเชื่อมแอป Next.js เข้าระบบ

> เอกสารนี้สรุปการทำงานของโค้ดระบบ SSO Portal เพื่อให้ทีม/แชทที่ดูแลโปรเจ็ค Next.js
> ใช้เป็นข้อมูลอ้างอิงในการแก้โค้ดฝั่ง Next.js ให้เชื่อมต่อระบบ login/cookie ของพอร์ทัลได้

## 1. ภาพรวม

- พอร์ทัลกลาง (ASP.NET Core 9 MVC) โฮสต์ที่ `https://portal.penso.co.th` — ผู้ใช้ล็อกอินที่นี่ครั้งเดียว
  แล้วคลิก tile เพื่อเข้าแอปลูก (Internal, Leave, KPI, Helpdesk) โดยไม่ต้องล็อกอินซ้ำ
- หัวใจของกลไก: **ทุกแอปอยู่ใต้ `*.penso.co.th`** พอร์ทัลจึงแปะคุกกี้ระดับ parent domain
  `Domain=.penso.co.th` ให้เบราว์เซอร์ถือ session ของแอปลูกได้
- **ไม่มี OAuth และ session ทั้งหมดเป็น cookie-based** — ข้อยกเว้นเดียวคือ gateway token
  (JWT HS256 อายุ 120 วิ) ที่ใช้ชั่วคราวตอนส่งผู้ใช้เข้าแอปแบบ GatewayRedirect
  (ดูข้อ 3.1 และ `docs/pms-gateway-token-spec.md`) — identity หลักของผู้ใช้คือ
  **อีเมล** (ตรงกับ `one_leave.dbo.log_users.user_name`)

## 2. การล็อกอินที่พอร์ทัล (`Controllers/AccountController.cs`)

- `POST /Account/Login` ตรวจ user/pass โดย **ล็อกอินจริงเข้าแอปปลายทางฝั่งเซิร์ฟเวอร์**
  (ตาม `Sso:ValidateApps` = `kpi` แล้วค่อย `leave`) — พอร์ทัลไม่ verify hash เอง
  ถ้าแอปที่ติดต่อได้ตอบว่ารหัสผิด ถือว่าผิดทันที
- ล็อกอินสำเร็จแล้วเกิด 2 อย่าง:
  1. **Sign-in cookie ของพอร์ทัล** — ASP.NET Cookie Authentication, claim เดียวคือ
     `ClaimTypes.Name` = username (อีเมล)
  2. **เก็บ user/pass ไว้ใช้ภายหลัง** ผ่าน `Services/CredentialStore.cs` — เข้ารหัสด้วย
     Data Protection (คีย์อยู่ในโฟลเดอร์ `keys/`, app name `PensoSingleSignOn`) เก็บใน
     server-side session; ถ้าติ๊ก Remember Me จะเขียน blob เดิมลงคุกกี้ถาวรด้วย
     เพื่อ rebuild session หลัง restart

### คุกกี้ของตัวพอร์ทัลเอง

| ชื่อคุกกี้ | หน้าที่ | อายุ |
|---|---|---|
| `.SSO.Portal` | auth cookie หลักของพอร์ทัล | 8 ชม. sliding; ถ้า Remember Me → persistent 365 วัน |
| `.SSO.Session` | session id (เก็บ credentials ที่เข้ารหัสฝั่ง server) | idle timeout 8 ชม. |
| `.SSO.Remember` | blob user/pass เข้ารหัส (เฉพาะ Remember Me) | 365 วัน, HttpOnly, SameSite=Lax |

## 3. การพาเข้าแอปลูก — `GET /go/{key}` (`Controllers/SsoController.cs`)

ลำดับการทำงาน: ต้องล็อกอินพอร์ทัลแล้ว (`[Authorize]`) → ดึง user/pass จาก `CredentialStore`
→ **เช็คสิทธิ์จาก DB** (ข้อ 4) → เลือกกลยุทธ์ตาม config ของแอปนั้นใน `appsettings.json` (`Sso:Apps[]`)

มี 3 กลยุทธ์ (`SsoStrategy` ใน `Models/SsoOptions.cs`):

### 3.1 GatewayRedirect (ใช้กับ Internal)

พอร์ทัลแค่ redirect เบราว์เซอร์ไปที่ `GatewayUrl` โดยแทน `{email}` ด้วยอีเมลผู้ใช้ (trim + lowercase)
เช่น `https://internal.penso.co.th/login.aspx?...&<param>={email}`
แอปปลายทางรับอีเมลแล้ว **สร้าง session ของตัวเอง** — พอร์ทัลไม่ยุ่งกับคุกกี้เลย

### 3.2 Transplant (ใช้กับ Leave, KPI, Helpdesk)

1. พอร์ทัลล็อกอินเข้าแอป**ฝั่งเซิร์ฟเวอร์** (`Services/AppLoginService.cs` → `ServerLoginAsync`):
   GET หน้า login → ดึง hidden input `__RequestVerificationToken` (engine CoreMvc) หรือ
   `__VIEWSTATE`/`__VIEWSTATEGENERATOR`/`__EVENTVALIDATION` (engine WebForms)
   → POST `application/x-www-form-urlencoded` ด้วยฟิลด์ชื่อตาม config (`UserField`, `PassField`, `ExtraFields`)
2. **เกณฑ์ตัดสินว่าสำเร็จ**: response เป็น 3xx และ `Location` **ไม่มีคำว่า "login"** (case-insensitive)
3. เอาคุกกี้ชื่อตาม `TransplantCookies` (เช่น `.AspNetCore.Cookies`) มาปล่อยใหม่ให้เบราว์เซอร์เป็น
   `Set-Cookie: <name>=<value>; Domain=.penso.co.th; Path=/; Secure; HttpOnly; SameSite=Lax`
   (session cookie ไม่มี Max-Age — เขียน header ตรง ๆ เพื่อไม่ให้ค่า base64 โดน re-encode)
4. redirect ไป `HomeUrl` → แอปเห็นคุกกี้ชื่อตัวเองก็ถือว่าล็อกอินอยู่แล้ว

> ข้อจำกัด: ชื่อคุกกี้ต้องไม่ซ้ำกันข้ามแอป เพราะแปะระดับ parent domain
> (KPI กับ Helpdesk ใช้ `.AspNetCore.Cookies` เหมือนกัน จึงเปิดพร้อมกันคนละแท็บไม่ได้)

### 3.3 AutoLogin

1. พอร์ทัล GET หน้า login ของแอป เอา antiforgery token + คุกกี้มา
2. แปะคุกกี้ antiforgery เป็น domain cookie อายุสั้น (Max-Age 2 นาที)
3. ส่งหน้า `Views/Sso/AutoSubmit.cshtml` (form hidden fields: user/pass/token) ให้
   **เบราว์เซอร์ POST เข้าแอปเอง** → แอป set คุกกี้ host ของตัวเอง
   (รหัสผ่านโผล่ใน HTML ชั่วคราว — ใช้เมื่อจำเป็นเท่านั้น)

## 4. ระบบสิทธิ์ (DB: SQL Server, database `Webportal`)

- `dbo.UserPermissions` (UserKey, Email, IsAdmin, IsActive) — UserKey = `one_leave.dbo.log_users.user_id`
- `dbo.UserApps` (UserKey, AppKey) — รายชื่อแอปที่ user เข้าได้ (AppKey ตรงกับ `Key` ใน `Sso:Apps`)
- ตอนเข้า `/go/{key}` จะ resolve อีเมล → `log_users.user_id` → เช็คว่า active + มี AppKey นั้น
  ไม่มีสิทธิ์ = เด้งกลับหน้า hub (ดู `Services/PermissionRepository.cs`, `Services/UserAccessor.cs`)

## 5. Logout — `GET /Account/Logout` (Single Logout)

1. ล้าง `CredentialStore` + SignOut cookie พอร์ทัล
2. ส่ง header `Clear-Site-Data: "cookies", "storage", "cache"`
3. `Set-Cookie` หมดอายุ (Expires 1970) ทุกชื่อคุกกี้ที่เคยแปะบน `.penso.co.th` — รวม
   `.AspNetCore.Cookies`, `.AspNet.ApplicationCookie` และทุกชื่อใน `TransplantCookies` ของทุกแอป
4. หน้า `Views/Account/Logout.cshtml` ยิง `fetch(url, { mode: 'no-cors', credentials: 'include' })`
   ไปที่ `LogoutUrl` (GET) ของแต่ละแอป เพื่อล้าง session ฝั่ง server ของแอปนั้น แล้ว redirect กลับ
   (`returnUrl` ถูก validate ว่า host ต้องตรงกับแอปใน config เท่านั้น — กัน open redirect)

## 6. รูปแบบรหัสผ่านในฐานข้อมูล (สำคัญถ้า Next.js จะ verify เอง)

รหัสผ่านผู้ใช้เก็บ 2 ที่ใน `one_leave` (สร้าง/reset พร้อมกันเสมอโดยฟีเจอร์ Forgot Password
ของพอร์ทัล — ดู `Services/LegacyPasswordCrypto.cs`):

- `AspNetUsers.PasswordHash` — ASP.NET Identity v2:
  `Base64( 0x00 + salt16 + PBKDF2(pw, salt, 1000 iterations, HMAC-SHA1, 32 bytes) )`
- `log_users.user_password` — "OneSecurity" (ถอดกลับได้):
  `Base64( TripleDES-ECB/PKCS7( UTF8(pw), key = MD5("GBHPOS") 16 bytes, 2-key EDE ) )`

## 7. สิ่งที่ฝั่ง Next.js ต้องทำเพื่อเชื่อมเข้าระบบนี้

**เงื่อนไขบังคับทุกกรณี:** แอป Next.js ต้องโฮสต์ใต้ `*.penso.co.th` (เช่น `newapp.penso.co.th`)
และใช้ HTTPS ใน production ไม่งั้นกลไกคุกกี้ร่วมโดเมนใช้ไม่ได้

### ทางเลือก A — GatewayRedirect (แนะนำ เพราะแก้โค้ด Next.js ได้เอง)

1. สร้าง route เช่น `GET /api/sso/gateway?token=...` ที่: verify JWT (ตามสเปคใน
   `docs/pms-gateway-token-spec.md`) → ดึงอีเมลจาก claim `sub` → ตรวจว่า user มีจริง →
   สร้าง session/cookie ของแอปเอง (เช่น cookie `HttpOnly; Secure; SameSite=Lax` scope host ตัวเอง)
   → redirect เข้าหน้าแรก
2. **ใช้ token ที่เซ็นแล้วแทนอีเมลเปล่า ๆ** — พอร์ทัลรองรับ placeholder `{token}` ใน
   `GatewayUrl` แล้ว (JWT HS256 อายุ 120 วิ เซ็นด้วย shared secret ต่อแอป)
   สเปคเต็ม + โค้ดตัวอย่าง Next.js + วิธีแลก shared secret อยู่ใน
   `docs/pms-gateway-token-spec.md` — แบบ `{email}` เปล่า ๆ ยังใช้ได้เฉพาะ legacy (Internal)
3. ทำ `GET /api/sso/logout` ที่ล้าง session ของแอป — endpoint นี้จะถูกยิงด้วย
   `fetch no-cors credentials:include` จาก `portal.penso.co.th`
   (same-site กัน จึงส่งคุกกี้ SameSite=Lax มาด้วยได้)
4. ฝั่งพอร์ทัลเพิ่ม entry ใน `appsettings.json` → `Sso:Apps[]`:

```json
{
  "Key": "newapp",
  "Name": "NewApp",
  "Url": "https://newapp.penso.co.th",
  "Strategy": "GatewayRedirect",
  "GatewayUrl": "https://newapp.penso.co.th/api/sso/gateway?token={token}",
  "GatewaySecret": "<base64 ของ key 32 ไบต์ — ใส่เฉพาะบนเซิร์ฟเวอร์ ห้าม commit>",
  "HomeUrl": "https://newapp.penso.co.th/",
  "LogoutUrl": "https://newapp.penso.co.th/api/sso/logout"
}
```

5. เพิ่มสิทธิ์ผู้ใช้: insert `dbo.UserApps (UserKey, AppKey='newapp')`
   (หรือผ่านหน้า Permissions ของพอร์ทัล)

### ทางเลือก B — Transplant (ถ้าอยากให้รหัสผ่านไม่ออกไปเบราว์เซอร์ และแอปตรวจรหัสเอง)

Next.js ต้องมี login แบบที่พอร์ทัล "เลียนแบบเบราว์เซอร์" ได้ คือ:

1. `GET` หน้า login คืน HTML; ถ้ามี CSRF ต้องเป็น hidden input ชื่อ `__RequestVerificationToken`
   เท่านั้น (regex ของพอร์ทัลดึงชื่อนี้ชื่อเดียวสำหรับ engine CoreMvc)
2. `POST` รับ `application/x-www-form-urlencoded` ฟิลด์ชื่อตามที่ config ไว้
   (`UserField`/`PassField` ตั้งได้อิสระ) และเมื่อสำเร็จต้อง **ตอบ 302/3xx ที่ `Location`
   ไม่มีคำว่า "login"** (ล้มเหลว = ตอบ 200 หน้าเดิม หรือ redirect กลับหน้า login)
3. Set cookie session **ชื่อไม่ซ้ำกับแอปอื่น** (ห้าม `.AspNetCore.Cookies`,
   `.AspNet.ApplicationCookie`; ห้ามใช้ prefix `__Host-`/`__Secure-` เพราะคุกกี้จะถูก re-emit
   เป็น `Domain=.penso.co.th`) และค่าคุกกี้ต้อง self-contained (ตรวจจากค่าในคุกกี้
   ไม่ผูกกับ IP/host เดิม) — เช่นใช้ JWT/iron-session ในคุกกี้ชื่อ `newapp_session`
4. แอปต้อง verify รหัสผ่านกับฐานเดียวกับระบบเดิม (ข้อ 6 — อ่าน `log_users.user_password`
   ถอด TripleDES หรือ verify `AspNetUsers.PasswordHash` แบบ Identity v2)
5. Config ฝั่งพอร์ทัล: `"Strategy": "Transplant"`, `LoginGetUrl`, `LoginPostUrl`,
   `UserField`, `PassField`, `TransplantCookies: ["newapp_session"]` —
   คุกกี้ชื่อนี้จะถูกลบให้อัตโนมัติตอน portal logout ด้วย

### หมายเหตุ middleware ฝั่ง Next.js

ไม่ว่าทางไหน หน้า protected ของ Next.js ควรมี middleware ตรวจ session cookie ของตัวเอง
แล้วถ้าไม่มี ให้ redirect ไป `https://portal.penso.co.th/go/newapp` (ไม่ใช่หน้า login ของตัวเอง)
เพื่อให้วนกลับเข้า flow SSO อัตโนมัติ — และหน้า logout ในแอปควรพาไป
`https://portal.penso.co.th/Account/Logout?returnUrl=https://newapp.penso.co.th/...`
เพื่อ Single Logout (returnUrl จะผ่าน validation ก็ต่อเมื่อ host ของแอปถูกเพิ่มใน `Sso:Apps` แล้ว)

## ไฟล์อ้างอิงหลักในโปรเจ็ค SSO Portal

| ไฟล์ | เนื้อหา |
|---|---|
| `Program.cs` | config cookie auth, session, data protection, DI |
| `appsettings.json` | `Sso:Apps[]` นิยามแอปทั้งหมด (URL, ฟิลด์, กลยุทธ์, ชื่อคุกกี้) |
| `Controllers/SsoController.cs` | `/go/{key}` — เลือกกลยุทธ์ + แปะคุกกี้ + ส่งต่อ |
| `Services/AppLoginService.cs` | server-side login (CoreMvc + WebForms) |
| `Services/CredentialStore.cs` | เก็บ credential เข้ารหัสใน session/cookie |
| `Controllers/AccountController.cs` | Login / Logout / Forgot-Reset password |
| `Services/LegacyPasswordCrypto.cs` | รูปแบบรหัสผ่าน 2 แบบของ one_leave |
| `Services/PermissionRepository.cs` | สิทธิ์ผู้ใช้ต่อแอป (Webportal DB) |
