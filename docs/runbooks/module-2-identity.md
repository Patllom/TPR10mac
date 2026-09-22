# คู่มือ Module 2 — Task 2: CSRF ก่อนเข้าสู่ระบบและ HTTPS

## ขอบเขต

รอบนี้เปิดเฉพาะ `GET /api/v1/auth/csrf` ยังไม่มี Login, Logout, session authority, RBAC หรือ MFA ของ Tasks 3–9 และยังไม่ใช่การอนุมัติขึ้น Production

Next ยังคง dev **4000** และ production build **4001** ส่วน **4443** เป็น HTTPS ทางเข้าเดียวของเว็บและ API สำหรับทดสอบความปลอดภัย HTTP ใช้ดู landing page เท่านั้น ไม่ใช้เป็นหลักฐานว่า cookie/auth ทำงาน

| ทางเข้า | ปลายทาง | ข้อกำหนด |
| --- | --- | --- |
| `https://localhost:4443/` | Next 4000 หรือ 4001 | ใบรับรองต้องเชื่อถือได้ |
| `https://localhost:4443/api/…` | API `127.0.0.1:5080` | proxy และ API ต้องเห็นกันผ่าน loopback |
| API โดยตรง | private เท่านั้น | ห้าม publish บน LAN/อินเทอร์เน็ต |

## สัญญา CSRF

1. Browser เรียก `/api/v1/auth/csrf` แบบ same-origin จะได้ `{token}` และ `Cache-Control: no-store`
2. Server ตั้ง `__Host-tpr10_preauth` พร้อม Secure, HttpOnly, Path=/, SameSite=Lax และไม่มี Domain เก็บเฉพาะ SHA-256 ของค่า cookie ในฐานข้อมูล
3. คำขอเปลี่ยนข้อมูลต้องส่ง cookie, `Origin` ตรงกับ HTTPS scheme/host/port ที่อนุญาต และ `X-CSRF-Token` ที่ได้จากข้อแรก ห้ามเก็บ token ลง localStorage หรือ log
4. Token ผูกกับ pre-auth record และ purpose เฉพาะ ผ่าน Data Protection; อายุเริ่มต้น 10 นาที ไม่ต่ออายุโดย GET ซ้ำใน flow เดิม
5. คำขอไม่ผ่านจะเป็น 403 พร้อม audit `security.csrf.denied` ก่อนเรียก endpoint ไม่มี business mutation; หากบันทึก audit ไม่ได้จะ fail closed เป็น 500 และไม่แสดงรายละเอียดภายใน

หากส่ง session cookie อยู่แล้วหรือมี authenticated principal จะปฏิเสธ ไม่ลดกลับไปใช้ pre-auth โดยเงียบ ๆ การผูก CSRF กับ restricted/active session และ rotation อยู่ Task 3

การเรียก `/api/v1/auth/login` ที่ไม่มี CSRF จึงได้ 403 แต่หาก CSRF ถูกต้องจะได้ 404 เพราะยังไม่มี Login API ส่วน technical-probe เดิมเปิดเฉพาะ Development/Testing เพื่อทดสอบ mutation เท่านั้น ไม่ใช่ endpoint ทางธุรกิจที่ผ่าน authentication แล้ว

## ค่าตั้งต้นและขอบเขตทรัพยากร

ใช้ configuration section `Identity:Csrf` หรือ environment variable ที่แทน `:` ด้วย `__`

| ค่า | ค่าเริ่มต้น | ช่วงที่รับ |
| --- | --- | --- |
| `AllowedOrigins` | `https://localhost:4443` เฉพาะพัฒนา | exact HTTPS authority ไม่มี path/query/userinfo |
| `LifetimeMinutes` | 10 | 1–30 |
| `MaxActiveFlows` | 10000 | 1–100000 |
| `PerIpPermitLimit` | 20/นาที | 1–10000 |
| `GlobalPermitLimit` | 600/นาที | 1–100000 |

Rate limit ครอบคลุม issuer และ unsafe methods ใช้ fixed window ไม่เข้าคิว ส่ง 429 พร้อม Retry-After=60 และ no-store โดยไม่เขียน denial audit ทุกครั้งเพื่อไม่ให้โจมตีขยายฐานข้อมูลผ่าน audit ได้ Health GET ไม่ถูกจำกัดด้วย budget นี้

การรับคำขอหัก per-IP และ global budget พร้อมกันภายใต้ lock เดียว คำขอที่เกินโควตา IP ไม่กินโควตารวม เก็บ IP เฉพาะคำขอที่รับเข้ามา จึงมี entries ไม่เกิน GlobalPermitLimit และล้างทุก window ใช้เวลาชนิด monotonic จาก TimeProvider เพื่อไม่อิงการปรับเวลานาฬิกาของระบบ

GET issuer ล้าง pre-auth ที่หมดอายุ/ถูกใช้/ถูกเพิกถอน และใช้ PostgreSQL transaction advisory lock ตรวจเพดานร่วมกันหลาย API instances; ไม่ลบ audit หรือ business data เมื่อเต็มตอบ 503 ผู้ถือ flow เดิมที่ยังใช้ได้ยังขอ token ซ้ำได้ ทั้ง rate limit และจำนวน active flows มีหน้าที่ต่างกัน: rate budget อยู่ใน process จึงต้องทบทวน distributed limiting ก่อนเพิ่มจำนวน replicas

## กุญแจ Data Protection

Development/Testing ใช้ ephemeral key ได้: restart แล้ว token เดิมใช้ไม่ได้ ต้องขอใหม่ Production ต้องกำหนด origins เองและมีทั้งค่าต่อไปนี้ มิฉะนั้น host ไม่เริ่มทำงาน:

- `Identity__Csrf__AllowedOrigins__0` เช่น HTTPS origin จริงของระบบ
- `Identity__Csrf__KeyRingPath` absolute path ของ directory ถาวรที่บัญชี API อ่าน/เขียนได้แต่ผู้ใช้อื่นเข้าไม่ได้
- `Identity__Csrf__CertificatePath` absolute path ของ PFX ที่มี private key สำหรับเข้ารหัส key ring เก็บอยู่นอก Git
- `Identity__Csrf__CertificatePassword` ส่งผ่าน secret management หาก PFX มีรหัสผ่าน ห้ามเขียนค่าในเอกสารหรือ command history

ต้องสำรอง key ring พร้อม certificate/private key ที่สัมพันธ์กัน ทดสอบ restore และวางแผน certificate rotation ก่อนใช้งานจริง ไม่ใช้ TLS CA private key เป็นกุญแจแอป และไม่ลบ key เก่าโดยไม่มีแผน migration

ใช้ application name `TPR10.Identity` และ purpose `TPR10.Identity.Csrf.v1` แยกจาก MFA secret encryption ตาม [เอกสาร Data Protection ของ Microsoft](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0) การกำหนด directory เองต้องระบุการเข้ารหัสกุญแจเองด้วย บน macOS .NET ใช้ keychain ชั่วคราวแทน EphemeralKeySet และ dispose certificate เมื่อ host ปิด ตาม [ข้อจำกัด crypto ข้ามแพลตฟอร์ม](https://github.com/dotnet/docs/blob/main/docs/standard/security/cross-platform-cryptography.md)

## เปิด HTTPS preview ในเครื่อง

ต้องเตรียม .NET 10, PostgreSQL ที่ migrate แล้ว, Node dependencies, nginx และใบรับรอง localhost ก่อน ใช้ฐานข้อมูลพัฒนาตามคู่มือ Module 1 ไม่ให้แอป migrate Production อัตโนมัติ

การ trust CA เปลี่ยนการตั้งค่าความปลอดภัยของเครื่อง ผู้ใช้ต้องดำเนินการเองอย่างตั้งใจ ตัวอย่างเมื่อเลือกติดตั้ง [mkcert จากโครงการต้นทาง](https://github.com/FiloSottile/mkcert):

```sh
mkcert -install
TPR10_TLS_DIR=$(mktemp -d /private/tmp/tpr10-local-tls.XXXXXX)
mkcert -cert-file "$TPR10_TLS_DIR/localhost.pem" -key-file "$TPR10_TLS_DIR/localhost.key" localhost
chmod 600 "$TPR10_TLS_DIR/localhost.key"
```

ห้ามแจกหรือ commit `rootCA-key.pem`, private key, PFX หรือ key ring เปิด browser ใหม่หลัง trust CA และต้องไม่มีคำเตือนใบรับรอง ไม่ใช้ `ignoreHTTPSErrors`, `curl -k` หรือกดข้ามคำเตือนเป็นผล acceptance

เริ่ม Next โดยเลือกอย่างใดอย่างหนึ่ง:

```sh
npm run dev -- --hostname 127.0.0.1
# หรือ npm run build แล้ว
npm run start -- --hostname 127.0.0.1
```

ในอีก terminal ตั้ง connection string ของฐานข้อมูลพัฒนาอย่างปลอดภัยแล้วเริ่ม API:

```sh
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --project backend/src/TPR10.Api --urls http://127.0.0.1:5080
```

สร้าง nginx server config เป็นไฟล์ใหม่ (renderer ไม่เขียนทับไฟล์เดิม):

```sh
TPR10_WEB_UPSTREAM=127.0.0.1:4000 \
TPR10_API_UPSTREAM=127.0.0.1:5080 \
TPR10_TLS_CERT="$TPR10_TLS_DIR/localhost.pem" \
TPR10_TLS_KEY="$TPR10_TLS_DIR/localhost.key" \
node infra/nginx/render-config.mjs "$TPR10_TLS_DIR/identity.conf" --https
```

เปลี่ยน web upstream เป็น 4001 หากใช้ production build นำไฟล์ที่ได้ไป include ภายใน `http { … }` ของ nginx instance สำหรับพัฒนา ตรวจ `nginx -t` ก่อน start/reload แล้วเปิด `https://localhost:4443` ห้ามใช้ template HTTP เดิมเพื่อทดสอบ auth

หากใช้ Docker ต้องให้ nginx กับ API ใช้ network namespace เดียวกัน เช่น API `--network container:<nginx-id>` และ bind API ที่ 127.0.0.1 โดยไม่ publish API port การใช้ nginx container ไปหา host API ผ่าน gateway ไม่ตรงกับ trusted-loopback policy ห้ามแก้ด้วยการ trust ทุก proxy

Proxy ส่ง Host และ X-Forwarded-Host แบบมี port และเขียนทับ forwarded headers จาก client เสมอ Unknown Host ถูกปิด connection; ASP.NET เชื่อเฉพาะ proxy 127.0.0.1/::1 และ ForwardLimit=1

## ทดสอบ acceptance อัตโนมัติโดยไม่เปลี่ยน trust store

จาก root ของ worktree ให้ Docker Desktop ทำงานและมี `docker`, `dotnet`, `openssl`, `curl`, Node ใน PATH ต้องว่างพอร์ต 4443 กับพอร์ตเว็บที่จะทดสอบ:

```sh
dotnet tool restore
npm run build
node infra/nginx/smoke-identity-https.mjs 4001
node infra/nginx/smoke-identity-https.mjs 4000
```

สคริปต์สร้าง CA อายุ 1 วันใน temp directory และให้ curl เชื่อถือเฉพาะ CA นี้ ตรวจ TLS จริง, เว็บ, cookie flags, no-store, CSRF 403/201 และ hostile Host รวมถึงพิสูจน์ว่า client ที่ไม่ trust CA ถูกปฏิเสธ ใช้ฐานข้อมูลทดสอบใน Docker แยก ไม่เชื่อมฐานข้อมูลผู้ใช้ ล้างเฉพาะ containers/network/ไฟล์ชั่วคราวที่สร้างเอง และหยุด Next ที่เริ่มเองเมื่อจบ ไม่เปิด preview ค้างไว้

## จุดตรวจรับก่อนขั้นถัดไป

รัน backend tests/build/format, Node tests, Next lint/build และ independent code review ก่อนถือว่าจบ Task 2 Task 3 ต้องเพิ่ม authentication ก่อน CSRF และ authorization หลัง CSRF พร้อม tests session-bound token; ห้ามถือว่า pre-auth validation เป็นหลักฐานสิทธิ์ผู้ใช้

ยังต้องตรวจ production capacity, key backup/rotation, browser flow ใน Task 8 และ dependency vulnerabilities เดิมก่อน deploy รอบนี้ไม่อัปเกรด dependency ข้าม major หรือรับรองความพร้อมทั้ง Module 2
