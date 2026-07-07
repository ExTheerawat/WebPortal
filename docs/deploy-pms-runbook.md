# Runbook: Deploy PMS SSO + วาง Secret (จังหวะ B)

> ทำตามลำดับบน→ล่าง แต่ละขั้นมี "ตรวจผล" ก่อนไปขั้นถัดไป — อย่าข้าม
> ผู้เกี่ยวข้อง: ฝั่ง portal (Arnon) + ฝั่ง PMS (คนถือ ssh เข้าเครื่อง PMS)
> เวลารวม ~30-45 นาที · **ผู้ใช้กระทบเฉพาะช่วง stop→start ของ portal (ขั้น 2.4 → 3.2, ~2-5 นาที
> ถ้าเตรียม secret ไว้ก่อนตามลำดับนี้)** — ประกาศ maintenance เผื่อ 15 นาทีปลอดภัยสุด
> คำสั่ง PowerShell ทั้งหมดทดสอบกับ Windows PowerShell 5.1 (shell มาตรฐานบนเซิร์ฟเวอร์) แล้ว

## หลักการที่ควรรู้ก่อนเริ่ม

- **portal ↔ PMS ไม่ต้องคุยกันเลยในระดับเซิร์ฟเวอร์** — flow ทั้งหมดวิ่งผ่าน browser
  (redirect + fetch) เพราะฉะนั้นไม่ต้องเปิด firewall ระหว่างสองเครื่อง แค่ browser ของผู้ใช้
  ต้องเข้าถึงทั้ง `portal.penso.co.th` และ `pms.penso.co.th`
- **secret มีค่าเดียว ใช้สองที่**: ฝั่ง portal ใน `appsettings.json` (`Sso:Apps[pms]:GatewaySecret`)
  และฝั่ง PMS ใน env **`SSO_SHARED_SECRET`** — ⚠️ ชื่อฝั่ง PMS ต้องเป๊ะ ถ้าตั้งผิดชื่อ
  แอปมองไม่เห็น secret แล้วอาการคือ "ลิงก์เข้าระบบไม่ถูกต้อง" ทั้งที่ทุกอย่างถูก
- token มีอายุ 120 วินาที + PMS ยอม clock skew ~60 วิ ⇒ **นาฬิกาสองเครื่องต้องเพี้ยนกันไม่เกิน ~1 นาที**
- แก้ `appsettings.json` แล้ว**ต้อง restart** เสมอ — พอร์ทัล bind config ผ่าน `IOptions<SsoOptions>`
  ซึ่งอ่านค่าครั้งเดียวตอนแอปสตาร์ต (ไม่ hot-reload)

---

## Phase 0 — เช็คก่อนวันจริง (5 นาที)

- [ ] GitHub `main` มี commit "Add PMS app to SSO portal…" (`30261a4`) และ runbook นี้
- [ ] นัดคนถือ ssh เครื่อง PMS ให้อยู่พร้อมกัน (Phase 5 ต้องใช้)
- [ ] มี password manager ที่แชร์รายการกันได้ (เช่น Bitwarden) เตรียมไว้
- [ ] เช็คเวลาเครื่อง:
  - portal server (PowerShell): `w32tm /query /status` → offset ต้องไม่เกินหลักวินาที
  - PMS server (ssh): `timedatectl` → `System clock synchronized: yes`
  - ถ้าเครื่องใดไม่ sync ให้แก้ NTP ก่อนวันจริง — นี่คือสาเหตุ "token หมดอายุปริศนา" อันดับหนึ่ง

## Phase 1 — Publish build (เครื่อง dev ของ Arnon, ~5 นาที)

```powershell
cd D:\Work\WebPortalX
git pull                      # ต้องได้ 30261a4 (หรือใหม่กว่า)
git log --oneline -1          # ตรวจว่าใช่ commit ที่ตั้งใจ
dotnet publish -c Release -o D:\Deploy\portal-pms
```

**ตรวจผล:**
```powershell
Select-String -Path D:\Deploy\portal-pms\appsettings.json -Pattern '"Key": "pms"'
```
ต้องเจอ 1 บรรทัด (entry `pms` อยู่ในไฟล์ที่จะเอาขึ้น) — และโฟลเดอร์ publish
**ไม่ควรมี** โฟลเดอร์ `keys/` (Data Protection keys อยู่บนเซิร์ฟเวอร์เท่านั้น ถูกแล้ว)

จากนั้น copy โฟลเดอร์ `D:\Deploy\portal-pms` ไปที่เครื่อง portal (RDP copy/paste หรือ network share)

## Phase 2 — เตรียมของบนเซิร์ฟเวอร์ portal (ผ่าน RDP — **พอร์ทัลยังเปิดให้ใช้อยู่ทั้ง Phase นี้จนถึง 2.4**)

สมมติ path ของ site คือ `C:\inetpub\portal` และ App Pool ชื่อ `portal`
(ดูของจริง: IIS Manager → Sites → คลิก site ที่ bind `portal.penso.co.th` → Basic Settings)

### 2.1 Backup ก่อนเสมอ

```powershell
New-Item -ItemType Directory -Force C:\Backup | Out-Null
Compress-Archive -Path C:\inetpub\portal -DestinationPath "C:\Backup\portal-before-pms-$(Get-Date -Format yyyyMMdd-HHmm).zip"
```

> zip ที่ได้จะมีโฟลเดอร์ `portal\` เป็นชั้นแรกข้างใน — จุดนี้สำคัญตอน rollback (Phase 7)

### 2.2 ⚠️ เทียบ appsettings.json ก่อนทับ

ไฟล์ใหม่จาก publish จะ**ทับ** `appsettings.json` บนเซิร์ฟเวอร์ เปิดเทียบสองไฟล์ก่อน:

```powershell
fc.exe C:\inetpub\portal\appsettings.json <path publish ที่ copy มา>\appsettings.json
```

- **กรณีปกติ** (ต่างกันแค่ entry `pms`): ไปข้อ 2.3 แล้วใช้คำสั่ง copy แบบ ก.
- **กรณีเซิร์ฟเวอร์มีค่าเฉพาะเครื่อง** (เช่น ConnectionStrings/Smtp ที่แก้ไว้ไม่ตรง repo):
  ใช้คำสั่ง copy แบบ ข. (ไม่ทับ appsettings) แล้วค่อยเพิ่ม entry `pms` เข้าไฟล์เดิมด้วยมือ
  (เนื้อหา entry ดู `docs/pms-gateway-token-spec.md` §6)

### 2.3 Generate secret **ตอนนี้เลย — ก่อน stop** (ค่าจะได้พร้อมวาง ไม่กินเวลา downtime)

รันบนเซิร์ฟเวอร์ (ใช้ได้ทั้ง PowerShell 5.1 และ 7):

```powershell
$b = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
[Convert]::ToBase64String($b)
```

ได้สตริง base64 ยาว 44 ตัวลงท้าย `=` — **นี่คือ secret ค่าเดียวของทั้งระบบ**
เปิด Notepad แล้วเตรียมบรรทัดนี้ไว้พร้อมวาง (แทนค่าจริงลงไป):

```json
"GatewaySecret": "<ค่าที่ได้>",
```

### 2.4 หยุด → ทับไฟล์ (เริ่มช่วง downtime)

```powershell
Import-Module WebAdministration
Stop-WebAppPool -Name "portal"          # ปลดล็อกไฟล์ DLL

# แบบ ก. (กรณีปกติ — ทับทุกไฟล์รวม appsettings.json):
robocopy <path publish ที่ copy มา> C:\inetpub\portal /E

# แบบ ข. (กรณีคงไฟล์ config ของเซิร์ฟเวอร์ไว้ — ไม่ทับ appsettings.json):
robocopy <path publish ที่ copy มา> C:\inetpub\portal /E /XF appsettings.json
```

ใช้ `/E` เท่านั้น **ห้ามใช้ `/MIR`** — /MIR จะลบ `C:\inetpub\portal\keys\` (Data Protection keys)
ถ้า keys/ หาย: ผู้ใช้ทุกคนหลุด login + คุกกี้ Remember Me พังทั้งหมด (ระบบไม่พัง แต่ทุกคนต้อง login ใหม่)

## Phase 3 — วาง secret + เปิดระบบ (ต่อเนื่องจาก 2.4, ~2 นาที)

### 3.1 วาง secret ใน appsettings.json

เปิด `C:\inetpub\portal\appsettings.json` หา entry `"Key": "pms"` → แทนที่บรรทัด
`"GatewaySecret": "",` ด้วยบรรทัดที่เตรียมไว้ใน Notepad (ข้อ 2.3) แล้ว save
(แบบ ข. คือจุดที่ต้องเพิ่ม entry `pms` ทั้งก้อนเข้าไฟล์เดิมตรงนี้)

ระวัง JSON พัง: ค่าอยู่ในเครื่องหมายคำพูด, คอมมาท้ายบรรทัดตามของเดิม
**ถ้า JSON พัง อาการคือทั้งเว็บเปิดไม่ขึ้น (HTTP 500.30)** — ไม่ใช่แค่ tile PMS พัง

### 3.2 Start + ตรวจผลฝั่ง portal (จบช่วง downtime)

```powershell
Start-WebAppPool -Name "portal"
```

เปิด browser → `https://portal.penso.co.th` → login ด้วย `arnon@p9.co.th`:

- [ ] เว็บเปิดขึ้นปกติ — ถ้าเจอ **HTTP 500.30 ทั้งเว็บ** = appsettings.json พัง (JSON parse ไม่ผ่าน)
      → เปิดไฟล์เช็ค syntax จุดที่เพิ่งแก้ / เทียบกับ backup → save → `Restart-WebAppPool -Name "portal"`
- [ ] เห็น tile **PMS** (สิทธิ์ใน DB ใส่รอไว้แล้ว)
- [ ] คลิก tile → browser วิ่งไป `https://pms.penso.co.th/api/sso/gateway?token=eyJ...`
      (ยัง error ฝั่ง PMS ได้ — Phase 5 ยังไม่ทำ — ที่ต้องเห็นคือ **token เป็น `eyJ...` ไม่ใช่อีเมลเปล่า**)
- [ ] ถ้าเจอหน้า error ของ **portal** (502) ตอนกด tile = `GatewaySecret` ว่าง/ไม่ใช่ base64
      → แก้ 3.1 ใหม่ + restart

> อยากดูไส้ token: copy เฉพาะส่วนกลาง (ระหว่างจุดแรกกับจุดที่สอง) มา decode base64 ในเครื่อง —
> ต้องเห็น `iss=https://portal.penso.co.th`, `aud=pms`, `sub=<อีเมล>`, `uid=<ตัวเลข>`
> **อย่า**เอา token ทั้งก้อนไปวางเว็บ decode ออนไลน์ตอนที่ยังไม่หมดอายุ (มันคือบัตรผ่าน 120 วิ)

## Phase 4 — ส่ง secret ให้ทีม PMS (~2 นาที)

1. สร้างรายการใน password manager ชื่อ: **`PMS SSO_SHARED_SECRET`** ใส่ค่าจากข้อ 2.3
2. แชร์ให้คนถือ ssh ฝั่ง PMS พร้อมข้อความกำกับ:
   *"ใส่เป็น env ชื่อ `SSO_SHARED_SECRET` เป๊ะ ๆ (ไม่ใช่ SSO_GATEWAY_SECRET ที่สเปคเคยเรียก)"*
3. **ห้าม**ส่งผ่าน LINE/อีเมล/แชท plaintext — ใครได้ secret = ปลอมเป็นใครก็ได้ใน PMS
4. ลบค่าออกจาก Notepad บนเซิร์ฟเวอร์ (เหลือแค่ใน appsettings + password manager)

## Phase 5 — ฝั่ง PMS (คนถือ ssh, ~5 นาที)

```bash
cd /opt/pms
sudo nano .env.production
```

เพิ่ม 3 บรรทัด (ค่า secret เอาจาก password manager):

```
SSO_ENABLED=true
SSO_SHARED_SECRET=<ค่าจาก password manager>
LOCAL_ADMIN_USER=arnonbass
```

แล้ว restart ให้ env ใหม่ถูกอ่าน:

```bash
docker compose up -d
docker compose ps            # container ต้องเป็น running/healthy
docker compose logs --tail 50   # ไม่ควรมี error ตอน boot
```

> ถ้า compose ไม่ recreate container (เพราะ image/config เดิม): `docker compose up -d --force-recreate`
> เช็คว่า env เข้าแล้วจริง: `docker compose exec <ชื่อ service> printenv | grep SSO_`

## Phase 6 — Smoke test ทันที (5 นาที ก่อนปล่อย UAT เต็ม)

ใช้ browser หน้าต่าง incognito ใหม่:

1. `https://portal.penso.co.th` → login `arnon@p9.co.th` → คลิก tile PMS
   → **ต้องเข้า dashboard PMS โดยไม่เจอหน้า login ของ PMS**
2. กลับ portal → คลิก tile PMS ซ้ำ → ต้องเข้าได้อีก (token ใหม่ทุกครั้ง jti ไม่ซ้ำ)
3. กด **ออกจากระบบ** ที่ portal → เปิด `https://pms.penso.co.th` ใหม่
   → ต้องไม่ล็อกอินแล้ว (โดนเด้งเข้า flow SSO → หน้า login portal)
4. `https://pms.penso.co.th/login` → เข้าด้วย `arnonbass` (local fallback) → ต้องเข้าได้
5. (UAT เต็ม) user ที่ไม่มีสิทธิ์ `pms` login portal → ต้องไม่เห็น tile และเข้า
   `https://portal.penso.co.th/go/pms` ตรง ๆ แล้วโดนเด้งกลับ hub
6. (Regression) login เข้า Internal / Leave / KPI / Helpdesk ผ่าน portal ให้ครบ — ต้องปกติทุกตัว

## Phase 7 — Rollback (ถ้าจำเป็น)

| ฝั่ง | วิธีถอย | ผล |
|---|---|---|
| PMS | แก้ `.env.production` → `SSO_ENABLED=false` → `docker compose up -d` | ปิดทาง SSO, `/login` ปกติยังใช้ได้ |
| portal | คำสั่งด้านล่าง | กลับ build เดิม 4 แอป |
| DB | ไม่ต้องทำอะไร — แถวสิทธิ์ `pms` ไม่มีผลเมื่อ config ไม่มีแอป `pms` | — |

คำสั่ง rollback ฝั่ง portal (zip จากข้อ 2.1 มีโฟลเดอร์ `portal\` อยู่ชั้นแรก
จึงต้องแตกลงที่ `C:\inetpub` **ไม่ใช่** `C:\inetpub\portal`):

```powershell
Stop-WebAppPool -Name "portal"
Expand-Archive -Path "C:\Backup\portal-before-pms-<เวลา>.zip" -DestinationPath C:\inetpub -Force
Start-WebAppPool -Name "portal"
```

`-Force` = ทับไฟล์ที่มีอยู่ด้วยของใน backup — รวม `keys/` ซึ่งเป็นไฟล์ชุดเดียวกับก่อน deploy อยู่แล้ว ปลอดภัย

## Troubleshooting: อาการ → สาเหตุ → เช็คที่ไหน

| อาการ | สาเหตุที่เป็นไปได้ | เช็ค/แก้ |
|---|---|---|
| **ทั้งเว็บ portal เปิดไม่ขึ้น (HTTP 500.30)** หลัง start | `appsettings.json` พัง (JSON parse ไม่ผ่าน — แอป start ไม่ได้เลย) | เช็ค syntax จุดที่แก้ / เทียบ backup → save → restart pool |
| กด tile → หน้า error ของ **portal** (502) | `GatewaySecret` ว่าง หรือไม่ใช่ base64 มาตรฐาน | log ของ portal; แก้ appsettings + restart |
| Redirect ถึง PMS แต่ "ลิงก์หมดอายุหรือไม่ถูกต้อง" | ① secret สองฝั่งไม่ตรง ② **ชื่อ env ผิด** (ต้อง `SSO_SHARED_SECRET`) ③ นาฬิกาเพี้ยนเกิน ~60 วิ | `printenv | grep SSO_` ใน container; เทียบค่ากับ password manager ทีละตัวอักษร; `w32tm`/`timedatectl` |
| "ลิงก์นี้ถูกใช้ไปแล้ว" | กด refresh/back บนหน้า gateway (jti ใช้ครั้งเดียว) | ปกติ — กลับ portal แล้วกด tile ใหม่ |
| "ไม่พบบัญชีผู้ใช้นี้ในระบบ PMS" | อีเมลใน PMS ไม่ตรง `sub` (lowercase) | เช็คตาราง user ฝั่ง PMS |
| ไม่เห็น tile PMS | build เก่า / user ไม่มีสิทธิ์ | `/go/pms` ตรง ๆ: ถ้า 404 = build เก่า, ถ้าเด้ง hub = ไม่มีสิทธิ์ (`db/GrantPmsAccess.sql`) |
| token เป็นอีเมลเปล่าไม่ใช่ `eyJ...` | server ยังรัน build เก่า หรือ GatewayUrl ไม่มี `{token}` | เทียบ appsettings บนเซิร์ฟเวอร์กับ repo |
| PMS 404 ที่ `/api/sso/gateway` | PMS ยังไม่ deploy build ที่มี endpoint / `SSO_ENABLED` ไม่ true | ฝั่ง PMS ตรวจ build + env |

## หลังเสร็จ

- [ ] secret อยู่แค่ 3 ที่: appsettings บนเซิร์ฟเวอร์ portal, `.env.production` บนเครื่อง PMS, password manager
- [ ] **ห้าม** copy appsettings ที่มี secret กลับเข้า repo / commit
- [ ] แจ้งทีม PMS ว่า UAT เปิดแล้ว + ใครเจอปัญหาให้แนบ URL ณ จุดที่พัง (มี token หมดอายุติดมาไม่เป็นไร)
