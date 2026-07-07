# Penso Single Sign-On Portal

พอร์ทัล **ล็อกอินครั้งเดียว เข้าได้ทุกระบบ** สำหรับแอปของ Penso 4 ระบบ — สร้างด้วย **ASP.NET Core 9 MVC**
และทำงานได้โดย **ไม่ต้องแก้โค้ดของแอปปลายทางเลย**

| ระบบ | URL | กลไก SSO |
|------|-----|----------|
| Internal | https://internal.penso.co.th | **GatewayRedirect** (ใช้ gateway ในตัวแอป ล็อกอินด้วยอีเมล) |
| Leave | https://leave.penso.co.th | Transplant (`.AspNet.ApplicationCookie`) |
| KPI | https://kpi.penso.co.th | Transplant (`.AspNetCore.Cookies`) |
| Helpdesk | https://support.penso.co.th | Transplant (`.AspNetCore.Cookies`) |
| PMS | https://pms.penso.co.th | **GatewayRedirect + signed JWT** (`{token}` — ดู [docs/pms-gateway-token-spec.md](docs/pms-gateway-token-spec.md)) |

> **ค่าเริ่มต้น = Transplant ทั้ง 4 ระบบ** (รหัสผ่านไม่ออกไปที่เบราว์เซอร์เลย) — พิสูจน์แล้วว่าเข้าได้ทุกระบบโดยไม่ล็อกอินซ้ำ
> ข้อจำกัดเดียว: KPI กับ Helpdesk ใช้ชื่อคุกกี้เดียวกัน (`.AspNetCore.Cookies`) จึง **เปิดพร้อมกันคนละแท็บไม่ได้** (สลับผ่านพอร์ทัลได้ปกติ)
> ถ้าต้องการเปิด KPI+Helpdesk พร้อมกัน → เปลี่ยน 2 ตัวนี้เป็น `"Strategy": "AutoLogin"` (ทำงานเฉพาะ production HTTPS)

---

## หลักการทำงาน (How it works)

ทุกแอปอยู่ใต้โดเมนเดียวกัน `*.penso.co.th` ทำให้ใช้คุกกี้ร่วมโดเมน `.penso.co.th` ได้
ผู้ใช้ล็อกอินที่พอร์ทัลครั้งเดียว จากนั้นพอร์ทัล "สวมรอย" ล็อกอินเข้าแต่ละแอปแทนผู้ใช้
แล้วทำให้เบราว์เซอร์มีเซสชันของแต่ละแอป — โดยแอปปลายทางไม่รู้ตัวและไม่ต้องแก้อะไร

มี 2 กลยุทธ์ (ตั้งค่าได้ต่อแอปใน `appsettings.json` → `Strategy`):

### 1. Transplant — ใช้กับ Leave, Internal
แอปทั้งสองมีชื่อคุกกี้ auth ที่ **ไม่ซ้ำใคร** จึงย้ายคุกกี้มาแปะระดับโดเมนได้ตรง ๆ
1. พอร์ทัลล็อกอินเข้าแอป **ฝั่งเซิร์ฟเวอร์** (จัดการ antiforgery token / WebForms `__VIEWSTATE` ให้เอง)
2. ดึงคุกกี้ที่ได้ (`.AspNet.ApplicationCookie` / `ASP.NET_SessionId`) มา set ใหม่เป็น `Domain=.penso.co.th`
3. redirect เบราว์เซอร์ไปหน้าแอป → แอปเห็นคุกกี้ของตัวเอง → ล็อกอินอยู่แล้ว
> ✅ ข้อดี: **รหัสผ่านไม่เคยออกไปที่เบราว์เซอร์**

### 2. Auto-login — ใช้กับ KPI, Helpdesk
สองแอปนี้ใช้ชื่อคุกกี้เดียวกัน (`.AspNetCore.Cookies`) — แปะระดับโดเมนพร้อมกันไม่ได้ (ทับกัน)
จึงให้ **เบราว์เซอร์ล็อกอินเข้าแอปเอง** เพื่อให้แต่ละแอป set คุกกี้ระดับ host ของตัวเอง (ไม่ชนกัน)
1. พอร์ทัล GET หน้า login ของแอป → ได้ antiforgery token + คุกกี้
2. แปะคุกกี้ antiforgery เป็น `Domain=.penso.co.th` อายุสั้น (2 นาที)
3. ส่งหน้า auto-submit form (มี user/pass/token) ให้เบราว์เซอร์ POST เข้าแอปเอง
4. แอป set คุกกี้ auth ระดับ host ของตัวเอง → ล็อกอินอยู่
> ⚠️ รหัสผ่านจะอยู่ในฟอร์ม HTML ชั่วคราว (ผ่าน HTTPS) — ดูหัวข้อความปลอดภัย

### การยืนยันตัวตนที่พอร์ทัล
การ**เข้าสู่ระบบปกติ**ของพอร์ทัล **ยืนยันตัวตนด้วยการล็อกอินจริงเข้าแอป one_leave** (KPI → Leave)
— ไม่ต้องถอดรหัสรหัสผ่านเอง

### ลืมรหัสผ่าน (Forgot password)
รหัสผ่านของ one_leave เก็บใน 2 ที่ ซึ่งจำลองได้แบบเป๊ะ ๆ ใน [Services/LegacyPasswordCrypto.cs](Services/LegacyPasswordCrypto.cs):
- `AspNetUsers.PasswordHash` — ASP.NET Identity v2: `Base64(0x00 + salt16 + PBKDF2-HMACSHA1(pw,salt,1000,32))`
- `log_users.user_password` — OneSecurity: `Base64(TripleDES-ECB/PKCS7(UTF8(pw), key=MD5("GBHPOS")))`

ฟีเจอร์ลืมรหัสผ่านส่งลิงก์ (token แบบ single-use เก็บเฉพาะ SHA-256) ไปทางอีเมล แล้วเขียนรหัสใหม่กลับเข้า
**ทั้งสองตารางในทรานแซกชันเดียว** (+`SecurityStamp` ใหม่) ผ่าน connection `Webportal`/`sa` เดิม
เพราะ `one_leave` อยู่บน SQL instance เดียวกัน (172.16.10.8) — ดู [Services/PasswordResetRepository.cs](Services/PasswordResetRepository.cs)
และตาราง [db/PasswordResets.sql](db/PasswordResets.sql)

---

## โครงสร้างโค้ด

```
Program.cs                      ตั้งค่า auth cookie, session, data protection, DI
appsettings.json                Sso:Apps[] นิยามแต่ละแอป (URL, ฟิลด์, กลยุทธ์, คุกกี้)
Models/SsoOptions.cs            AppDefinition, SsoOptions, enum กลยุทธ์/engine
Models/ViewModels.cs            LoginViewModel, CookieToPlant, TransplantResult, AutoLoginPrep
Services/AppLoginService.cs     ★ หัวใจ: ด-แดนซ์ login ฝั่งเซิร์ฟเวอร์ (CoreMvc + WebForms)
Services/CredentialStore.cs     เก็บ credential ใน session (เข้ารหัส Data Protection)
Controllers/AccountController   Login / Logout
Controllers/HomeController      หน้า hub รวม 4 ระบบ
Controllers/SsoController       /go/{key} — เลือกกลยุทธ์ + แปะคุกกี้ + ส่งต่อ
Views/...                       Login, Index(hub), Sso/AutoSubmit, Sso/SsoError
```

---

## รันแบบ Local แล้ว SSO ทำงานจริง (ใช้ hosts)

SSO ต้องการให้พอร์ทัลรันใต้โดเมน `penso.co.th` (เบราว์เซอร์ถึงจะรับ/ส่งคุกกี้ `.penso.co.th`)
เปิดจาก `http://localhost` ตรง ๆ จะ **ยังเด้งหน้า login** เพราะเบราว์เซอร์ทิ้งคุกกี้ — วิธีทดสอบบนเครื่อง:

1. เพิ่ม hosts (รัน PowerShell **as Administrator**):
   ```powershell
   Add-Content -Path "$env:windir\System32\drivers\etc\hosts" -Value "`n127.0.0.1 sso.penso.co.th"
   ```
2. รันพอร์ทัล: `dotnet run`  (โปรไฟล์ Development จะ bind `localhost:5259`)
3. เปิดเบราว์เซอร์ที่ **http://sso.penso.co.th:5259** → ล็อกอิน → คลิก tile → เข้าได้ทันทีไม่ต้องล็อกอินซ้ำ

> หมายเหตุ: local ใช้ HTTP จึงตั้ง KPI/Helpdesk เป็น Transplant (ค่าเริ่มต้น) ซึ่งทำงานได้ทั้ง HTTP และ HTTPS

---

## Deploy (production — จำเป็นต่อการใช้งานจริง)

1. โฮสต์พอร์ทัลที่ **https://portal.penso.co.th** (host จริงใน production — DNS A record + ใบรับรอง HTTPS)
   - IIS: ติดตั้ง ASP.NET Core Hosting Bundle → publish → ผูก binding `portal.penso.co.th:443`
   - หมายเหตุ: หัวข้อทดสอบ local ด้านบนใช้ชื่อ `sso.penso.co.th` **โดยตั้งใจ** —
     เป็น alias ใน hosts ที่ไม่ทับ host จริง เพื่อให้เครื่อง dev ยังเข้า portal จริงได้
   - `dotnet publish -c Release` แล้ว deploy โฟลเดอร์ผลลัพธ์
2. `appsettings.json`:
   - `Sso:CookieDomain` = `.penso.co.th`
   - `Sso:RequireHttps` = `true`
3. ให้เซิร์ฟเวอร์พอร์ทัล **เข้าถึงเครือข่ายของทั้ง 4 แอป** ได้ (ใช้ตอนล็อกอินฝั่งเซิร์ฟเวอร์)
4. โฟลเดอร์ `keys/` (Data Protection) ต้องเขียนได้และสำรอง/คงอยู่ข้ามการ restart

### ทดสอบ SSO เต็มรูปแบบบนเครื่อง dev (ถ้าต้องการ ก่อน deploy จริง)
- เพิ่มใน hosts: `127.0.0.1  sso.penso.co.th`
- สร้าง+ไว้ใจใบรับรอง HTTPS สำหรับ `sso.penso.co.th` แล้วรันพอร์ทัลบน `https://sso.penso.co.th`
- เปิดเบราว์เซอร์ → login → คลิกแต่ละ tile → ต้องเข้าแอปได้โดยไม่เจอหน้า login อีก
  (แอป kpi/leave/internal/support ยังชี้ไปเซิร์ฟเวอร์จริงตาม DNS)

---

## ความปลอดภัย (อ่านก่อนใช้งานจริง)

- **ต้องใช้ HTTPS เท่านั้น** — คุกกี้ทั้งหมดตั้ง `Secure`
- พอร์ทัลถือ credential ของผู้ใช้ไว้ใน session (เข้ารหัสด้วย Data Protection) เพื่อล็อกอินแต่ละแอปแทน
  ⇒ ต้องป้องกันตัวพอร์ทัลให้ดี (เป็นจุดรวมความเสี่ยง)
- กลยุทธ์ **Auto-login (KPI/Helpdesk)** จะฝังรหัสผ่านในฟอร์ม HTML ชั่วคราวฝั่งเบราว์เซอร์
  หากรับไม่ได้ ให้เปลี่ยนเป็น `"Strategy": "Transplant"` (แต่ KPI กับ Helpdesk จะเปิดพร้อมกัน
  คนละแท็บไม่ได้ เพราะใช้ชื่อคุกกี้เดียวกัน)
- ไม่มีการเก็บรหัส SQL `sa` ในพอร์ทัล (ยืนยันตัวตนผ่านการล็อกอินแอป)
- **Logout** ล้างเฉพาะ session ของพอร์ทัล เซสชันของแต่ละแอปจะหมดอายุตามค่าของแอปนั้น ๆ

---

## สถานะการทดสอบ (ณ ตอนส่งมอบ)

✅ ตรวจแล้วกับแอปจริง (ฝั่งเซิร์ฟเวอร์):
- ยืนยันตัวตนที่พอร์ทัลผ่านการล็อกอิน KPI จริง
- `/go/leave`, `/go/internal` → ล็อกอินจริงสำเร็จ + แปะคุกกี้โดเมนถูกต้อง
- `/go/kpi`, `/go/helpdesk` → ได้ token/คุกกี้ antiforgery + สร้างฟอร์ม auto-submit ถูกต้อง

⏳ ต้องทดสอบหลัง deploy ใต้ `portal.penso.co.th`:
- เบราว์เซอร์รับคุกกี้ `.penso.co.th` แล้วเข้าแต่ละแอปโดยไม่ล็อกอินซ้ำ (พฤติกรรมคุกกี้มาตรฐาน)
