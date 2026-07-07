# สเปค Gateway Token: SSO Portal → PMS (v1)

> เอกสารนี้ตอบ 2 คำถามของฝั่ง PMS (Next.js): **สเปค token** ที่พอร์ทัลส่งมา และ
> **วิธีแลก/เก็บ shared secret** ระหว่างสองระบบ
> ฝั่งพอร์ทัล implement เสร็จแล้ว (ดูหัวข้อ 8) — ฝั่ง PMS implement ตามเอกสารนี้ได้เลย

## 1. ภาพรวม flow

```
ผู้ใช้คลิก tile PMS บนพอร์ทัล
  → GET https://portal.penso.co.th/go/pms   (ต้องล็อกอินพอร์ทัล + มีสิทธิ์ app "pms")
  → พอร์ทัลเซ็น JWT อายุ 120 วินาที ด้วย shared secret
  → 302 Redirect: https://pms.penso.co.th/api/sso/gateway?token=<JWT>
  → PMS verify token → สร้าง session cookie ของตัวเอง → redirect เข้าหน้าแรก
```

Token ใช้แทนการส่งอีเมลเปล่า ๆ ใน query (แบบ legacy ของแอป Internal) เพื่อให้ endpoint
ปฏิเสธลิงก์ปลอม/ลิงก์ที่ถูกนำมาใช้ซ้ำได้

## 2. สเปค Token

**รูปแบบ: JWT มาตรฐาน (RFC 7519), ลายเซ็น HS256 (HMAC-SHA256), encoding base64url ไม่มี padding**

### Header

```json
{ "alg": "HS256", "typ": "JWT" }
```

### Claims (payload)

| Claim | ชนิด | ค่า | ตรวจฝั่ง PMS |
|---|---|---|---|
| `iss` | string | `https://portal.penso.co.th` (ตายตัว, **ไม่มี `/` ท้าย** — เทียบทั้งสตริงเป๊ะ ๆ) | **MUST** ตรงเป๊ะ |
| `aud` | string | `pms` (= App Key ใน config พอร์ทัล) | **MUST** ตรงเป๊ะ |
| `sub` | string | อีเมลผู้ใช้ (trim + lowercase แล้ว) เช่น `somchai.j@ccthailand.co.th` | ใช้หา user ใน PMS |
| `iat` | number | unix seconds ตอนออก token | — |
| `exp` | number | `iat + 120` (อายุ 120 วินาที) | **MUST** ยังไม่หมดอายุ (เผื่อ clock skew ±60 วิ) |
| `jti` | string | nonce สุ่ม 16 ไบต์ base64url (ยาว 22 ตัวอักษร) | **SHOULD** ใช้ครั้งเดียว (กัน replay) |
| `uid` | number | `one_leave.dbo.log_users.user_id` ของผู้ใช้ — id ที่นิ่งกว่าอีเมล (อีเมลพนักงานเปลี่ยนได้ระหว่าง `@p9.co.th`/`@ccthailand.co.th`) | **OPTIONAL** — ถ้ามีให้ใช้เป็น mapping หลัก, ไม่มีต้องไม่ fail |

### ลายเซ็น

```
signature = base64url( HMAC-SHA256( key = secretBytes,
                                    data = base64url(header) + "." + base64url(payload) ) )
```

**`secretBytes` = base64-decode ของ shared secret** (ดูหัวข้อ 5 — secret ในไฟล์ config
เป็นสตริง base64 มาตรฐานของ key ดิบ 32 ไบต์ ต้อง decode ก่อนใช้ ไม่ใช่เอาสตริงไป HMAC ตรง ๆ)

### Test vector (ใช้เขียน unit test ฝั่ง PMS ได้)

```
secret (base64) : oR+1mCI/uV/GVF5A9Qx7fSNjM3NsxbphqdVlvP5RBVA=
token           : eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJodHRwczovL3BvcnRhbC5wZW5zby5jby50aCIsImF1ZCI6InBtcyIsInN1YiI6InNvbWNoYWkuakBjY3RoYWlsYW5kLmNvLnRoIiwiaWF0IjoxNzgzMzk1ODQxLCJleHAiOjE3ODMzOTU5NjEsImp0aSI6InZTN01IdERZM2V4ZTJ6QjBPMGpMclEiLCJ1aWQiOjQwMjF9.mJHhWX_wZGOtYlWc0N36LFqy1YXteoq6mlsxh_G98PA
payload ที่ถอดได้ : { "iss":"https://portal.penso.co.th","aud":"pms","sub":"somchai.j@ccthailand.co.th",
                    "iat":1783395841,"exp":1783395961,"jti":"vS7MHtDY3exe2zB0O0jLrQ","uid":4021 }
```

> token ตัวอย่างนี้ `exp` ผ่านไปแล้ว — เวลาเทสกับ `jose` ให้ fix เวลา:
> `jwtVerify(token, key, { ..., currentDate: new Date(1783395845 * 1000) })`
> (secret ชุดนี้เป็นค่าสุ่มทิ้งสำหรับเทสเท่านั้น **ห้าม**ใช้เป็น secret จริง)
>
> vector นี้ generate จากโค้ดจริงของพอร์ทัลและ verify ผ่าน `jose` แล้ว — ธรรมเนียมของสองทีม:
> **ทุกครั้งที่แก้ `Services/GatewayTokenService.cs` ต้อง generate vector ใหม่เทียบกับ `jose`
> แล้วอัปเดตบล็อกนี้** (v1.1: เพิ่ม claim `uid` · v1.2: `iss` เปลี่ยนเป็น host จริง
> `https://portal.penso.co.th`)

## 3. กติกาการ verify ฝั่ง PMS

ลำดับที่ **ต้องทำ (MUST)**:

1. verify ลายเซ็นด้วย shared secret และ **บังคับ `algorithms: ['HS256']` เท่านั้น**
   (ห้ามให้ token เป็นคนเลือก alg — กันโจมตี `alg: none`)
2. ตรวจ `iss` = `https://portal.penso.co.th` และ `aud` = `pms`
3. ตรวจ `exp` โดยยอมรับ clock skew ได้ไม่เกิน 60 วินาที
4. `sub` ต้องเป็นอีเมลที่มีอยู่ในระบบ PMS — ไม่พบ = ปฏิเสธ (อย่า auto-สร้าง user เว้นแต่ตั้งใจ)

**ควรทำ (SHOULD — แนะนำอย่างยิ่ง):**

5. กัน replay ด้วย `jti`: จำ `jti` ที่ใช้แล้วไว้จนพ้น `exp` แล้วปฏิเสธค่าซ้ำ —
   เพราะ URL ที่มี token จะค้างอยู่ใน browser history / server log ได้
6. เมื่อ verify ไม่ผ่าน ให้แสดง**หน้า error พร้อมลิงก์กลับพอร์ทัล** —
   **ห้าม auto-redirect กลับ `https://portal.penso.co.th/go/pms`** เพราะถ้าปัญหาเกิดจาก config
   (เช่น secret ไม่ตรงกัน) จะกลายเป็น redirect loop ไม่รู้จบ

## 4. โค้ดตัวอย่างฝั่ง PMS (Next.js App Router + `jose`)

```bash
npm install jose
```

### 4.1 Gateway endpoint — `app/api/sso/gateway/route.ts`

```ts
import { jwtVerify } from 'jose';
import { NextRequest, NextResponse } from 'next/server';

const SECRET = new Uint8Array(Buffer.from(process.env.SSO_SHARED_SECRET ?? '', 'base64'));
const PORTAL = 'https://portal.penso.co.th';

// กัน replay: จำ jti ที่ใช้แล้วไว้จนพ้นอายุ token (อายุแค่ 120 วิ ใช้ Map ในหน่วยความจำได้)
// ข้อจำกัด: ถ้า deploy หลาย instance / serverless ให้ย้ายไป Redis หรือตารางใน DB แทน
const usedJti = new Map<string, number>();
function seenBefore(jti: string, exp: number): boolean {
  const now = Math.floor(Date.now() / 1000);
  for (const [k, e] of usedJti) if (e < now) usedJti.delete(k);
  if (usedJti.has(jti)) return true;
  usedJti.set(jti, exp + 120);
  return false;
}

export async function GET(req: NextRequest) {
  const token = req.nextUrl.searchParams.get('token');
  if (!token) return NextResponse.redirect(PORTAL);

  let email: string, jti: string, exp: number;
  try {
    const { payload } = await jwtVerify(token, SECRET, {
      algorithms: ['HS256'],            // MUST: ล็อก algorithm
      issuer: 'https://portal.penso.co.th',
      audience: 'pms',
      clockTolerance: 60,               // เผื่อนาฬิกาสองเซิร์ฟเวอร์เหลื่อมกัน
    });
    email = String(payload.sub ?? '').trim().toLowerCase();
    jti = String(payload.jti ?? '');
    exp = Number(payload.exp ?? 0);
  } catch {
    return ssoErrorPage('ลิงก์หมดอายุหรือไม่ถูกต้อง'); // ห้าม redirect อัตโนมัติ — กัน loop
  }

  if (!email || !jti || seenBefore(jti, exp))
    return ssoErrorPage('ลิงก์นี้ถูกใช้ไปแล้ว กรุณาเข้าใหม่ผ่านพอร์ทัล');

  const user = await findUserByEmail(email);          // TODO: ตาราง user ของ PMS
  if (!user) return ssoErrorPage('ไม่พบบัญชีผู้ใช้นี้ในระบบ PMS');

  const res = NextResponse.redirect(new URL('/', req.url));
  await createSession(res, user);                     // TODO: session ของ PMS เอง (ดู 4.4)
  return res;
}

function ssoErrorPage(message: string) {
  return new NextResponse(
    `<!doctype html><meta charset="utf-8"><body style="font-family:sans-serif;text-align:center;padding:60px">
     <h3>${message}</h3><a href="${PORTAL}/go/pms">กลับเข้าผ่านพอร์ทัล</a></body>`,
    { status: 401, headers: { 'content-type': 'text/html; charset=utf-8' } });
}
```

### 4.2 Logout endpoint — `app/api/sso/logout/route.ts`

ถูกเรียกจากหน้า Logout ของพอร์ทัลด้วย `fetch(url, { mode: 'no-cors', credentials: 'include' })`
— portal.penso.co.th กับ pms.penso.co.th เป็น same-site กัน คุกกี้ `SameSite=Lax` จึงถูกส่งมาด้วย

```ts
import { NextResponse } from 'next/server';

export async function GET() {
  const res = new NextResponse(null, { status: 204 });
  res.cookies.set('pms_session', '', {
    path: '/', maxAge: 0, httpOnly: true, secure: true, sameSite: 'lax',
  });
  // ถ้า PMS เก็บ session ฝั่ง server (DB/Redis) ให้ลบ record ที่นี่ด้วย
  return res;
}
```

### 4.3 Middleware กันหน้า protected — `middleware.ts`

```ts
import { NextRequest, NextResponse } from 'next/server';

export function middleware(req: NextRequest) {
  if (!req.cookies.get('pms_session')) {
    // ไม่มี session → วนกลับเข้า flow SSO (ถ้ายังล็อกอินพอร์ทัลอยู่จะเด้งกลับมาแบบไร้รอยต่อ
    // ถ้าไม่ได้ล็อกอิน พอร์ทัลจะพาไปหน้า login ของพอร์ทัลเอง — จบที่นั่น ไม่เกิด loop)
    return NextResponse.redirect('https://portal.penso.co.th/go/pms');
  }
  return NextResponse.next();
}

export const config = {
  matcher: ['/((?!api/sso|_next|favicon.ico|.*\\.(?:png|jpg|svg|css|js)).*)'],
};
```

### 4.4 ข้อกำหนด session cookie ของ PMS

- ชื่อแนะนำ `pms_session`, ตั้ง `HttpOnly; Secure; SameSite=Lax; Path=/` **scope เฉพาะ host ตัวเอง**
  (ไม่ต้องใส่ `Domain=` — อย่าแปะระดับ `.penso.co.th` เพราะจะไปชนกับคุกกี้ที่พอร์ทัลจัดการ)
- เนื้อในใช้อะไรก็ได้ที่ทีมถนัด: JWT ของตัวเอง / iron-session / next-auth session / session id + DB
- อายุ session กำหนดเองได้อิสระ (พอร์ทัลใช้ 8 ชม. sliding เป็น reference)
- การ logout ฝั่ง PMS เอง (ปุ่มในแอป) ให้พาไป
  `https://portal.penso.co.th/Account/Logout?returnUrl=https://pms.penso.co.th/` เพื่อ Single Logout ทุกระบบ

## 5. Shared secret — สร้าง / แลกเปลี่ยน / เก็บ / หมุนเวียน

### 5.1 สร้าง (ทำครั้งเดียว โดยคนที่ดูแล deploy)

key ดิบ 32 ไบต์ (256-bit) encode เป็น **base64 มาตรฐาน** — ใช้คำสั่งไหนก็ได้:

```powershell
# PowerShell (ใช้ได้ทั้ง 5.1 และ 7 — แบบ [RandomNumberGenerator]::GetBytes(32) ตรง ๆ ใช้ได้เฉพาะ pwsh 7+)
$b = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
[Convert]::ToBase64String($b)
```
```bash
# Node
node -e "console.log(require('crypto').randomBytes(32).toString('base64'))"
# หรือ OpenSSL
openssl rand -base64 32
```

### 5.2 วิธีแลกเปลี่ยน (นำ secret เดียวกันไปไว้สองระบบ)

- ทั้งสองระบบดูแลโดยทีมเดียวกัน → **generate ครั้งเดียวแล้ววางลง config บนเซิร์ฟเวอร์ทั้งสองฝั่งโดยตรง**
  (remote เข้าไปวางเอง) — นี่คือ "การแลก" ทั้งหมด ไม่มีพิธีอะไรมากกว่านี้
- **ห้าม**ส่ง secret ผ่าน LINE / อีเมล / แชท แบบ plaintext และ**ห้าม commit ลง git ทั้งสอง repo**
  ถ้าจำเป็นต้องส่งข้ามคน ให้ใช้ password manager ที่แชร์รายการได้ (Bitwarden/1Password)
  หรือช่องทางเข้ารหัส แล้วลบข้อความทันทีที่ตั้งค่าเสร็จ
- ใครเห็น secret = ปลอมเป็นผู้ใช้คนไหนก็ได้ในระบบ PMS — ปฏิบัติกับมันเหมือนรหัสผ่าน admin

### 5.3 ที่เก็บแต่ละฝั่ง

| ฝั่ง | ที่เก็บ | หมายเหตุ |
|---|---|---|
| SSO Portal | `appsettings.json` (หรือ `appsettings.Production.json`) **บนเซิร์ฟเวอร์เท่านั้น** → `Sso:Apps[pms]:GatewaySecret` | อย่าใส่ในไฟล์ที่ commit |
| PMS (Next.js) | env var `SSO_SHARED_SECRET` ใน `.env.local` / host env | **ห้าม**ใช้ prefix `NEXT_PUBLIC_` (จะหลุดไป client bundle) และเช็คว่า `.env*` อยู่ใน `.gitignore` |

> ⚠️ **ชื่อตัวแปรฝั่ง PMS คือ `SSO_SHARED_SECRET` เป๊ะ ๆ** (แอป PMS อ่านชื่อนี้เท่านั้น)
> ตอนส่งค่า secret ผ่าน password manager ให้เขียนชื่อตัวแปรนี้กำกับไปด้วย —
> ถ้าใส่ผิดชื่อ แอปจะมองไม่เห็น secret แล้วอาการที่เจอคือกด tile แล้วขึ้น
> "ลิงก์เข้าระบบไม่ถูกต้อง" ทั้งที่ token ถูกทุกอย่าง

### 5.4 การหมุนเวียน (rotation)

Token อายุแค่ 120 วินาที การหมุน secret จึงง่าย เลือกได้ 2 แบบ:

- **แบบง่าย**: เปลี่ยน config สองฝั่งแล้ว restart ใกล้เคียงกัน — ช่วงคาบเกี่ยวผู้ใช้ที่กดเข้า PMS
  พอดีอาจเจอหน้า error หนึ่งครั้ง กดกลับพอร์ทัลแล้วเข้าใหม่ได้
- **แบบไร้รอยต่อ**: ให้ PMS รองรับ 2 คีย์ชั่วคราว (verify ด้วยคีย์ใหม่ก่อน ไม่ผ่านค่อย fallback
  คีย์เก่า) → เปลี่ยนฝั่งพอร์ทัลเป็นคีย์ใหม่ → ถอดคีย์เก่าออกจาก PMS
- ควรหมุนทันทีเมื่อสงสัยว่ารั่ว และตามรอบปกติ (เช่น ปีละครั้ง)

## 6. Config ฝั่งพอร์ทัลสำหรับ PMS (เพิ่มใน `Sso:Apps[]` แล้ว — เหลือใส่ secret จริงตอน deploy)

```json
{
  "Key": "pms",
  "Name": "PMS",
  "Description": "ระบบ PMS",
  "Url": "https://pms.penso.co.th",
  "Strategy": "GatewayRedirect",
  "GatewayUrl": "https://pms.penso.co.th/api/sso/gateway?token={token}",
  "GatewaySecret": "<base64 32 ไบต์ — ใส่เฉพาะบนเซิร์ฟเวอร์>",
  "HomeUrl": "https://pms.penso.co.th/",
  "LogoutUrl": "https://pms.penso.co.th/api/sso/logout"
}
```

(ปรับ URL ตามโดเมนจริงของ PMS — ต้องอยู่ใต้ `*.penso.co.th`) จากนั้นให้สิทธิ์ผู้ใช้:
เพิ่มแถว `dbo.UserApps (UserKey, AppKey='pms')` หรือติ๊กผ่านหน้า Permissions ของพอร์ทัล

## 7. สรุปเช็คลิสต์ฝั่ง PMS

- [ ] `npm install jose` + สร้าง `app/api/sso/gateway/route.ts` ตามข้อ 4.1
- [ ] verify ครบ: HS256 เท่านั้น, iss, aud, exp (±60 วิ), user มีจริง, jti ใช้ครั้งเดียว
- [ ] สร้าง `app/api/sso/logout/route.ts` (GET, ล้าง session, ตอบ 204)
- [ ] middleware: ไม่มี session → redirect `https://portal.penso.co.th/go/pms`
- [ ] ปุ่ม logout ในแอป → `https://portal.penso.co.th/Account/Logout?returnUrl=...`
- [ ] `SSO_SHARED_SECRET` อยู่ใน env ฝั่ง server เท่านั้น ไม่ commit, ไม่ `NEXT_PUBLIC_`
- [ ] เทสด้วย test vector ในข้อ 2 (fix `currentDate`) แล้วค่อยเทสของจริงผ่านพอร์ทัล

## 8. สถานะฝั่งพอร์ทัล (repo WebPortalX / SingleSignOn)

Implement เสร็จแล้ว ตรวจสอบแล้วว่า token ที่ mint ออกมา verify ผ่าน `jose` ด้วย options ตามข้อ 4.1:

- `Services/GatewayTokenService.cs` — mint JWT HS256 ตามสเปคนี้ (ไฟล์ใหม่; v1.1 เพิ่ม claim `uid`)
- `Controllers/SsoController.cs` — `GatewayUrl` รองรับ placeholder `{token}` (ของเดิม `{email}` ยังใช้ได้ สำหรับ Internal)
- `Models/SsoOptions.cs` — เพิ่มฟิลด์ `GatewaySecret` ต่อแอป
- `Models/Permissions.cs` + `Services/PermissionRepository.cs` — `UserAccess` ถือ `UserKey`
  (= `log_users.user_id`) เพื่อใส่ claim `uid` (ค่า 0/ไม่พบ → ไม่ใส่ claim)
- `appsettings.json` — เพิ่ม entry `pms` แล้ว (`GatewaySecret` เว้นว่างไว้ ใส่ค่าจริงบนเซิร์ฟเวอร์ตอน deploy)
- ถ้า config ใช้ `{token}` แต่ไม่ได้ตั้ง `GatewaySecret` → พอร์ทัลตอบหน้า SSO error (502) และ log สาเหตุ
  (ไม่ log ค่า token/secret)
