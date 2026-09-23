# คู่มือ Module 2 — Tasks 2–5: HTTPS, Session บัญชีผู้ใช้ และ MFA

## ขอบเขต

มี API สำหรับ CSRF, Login, ตรวจ Session, Logout และ MFA พร้อม CLI สร้างผู้ดูแลแรกและ use case จัดการบัญชีแล้ว API จัดการบัญชียังไม่เปิดจน Task 6 มี permission+MFA policy ครบ ยังไม่มีหน้า Login และยังไม่ใช่การอนุมัติขึ้น Production

## MFA (Task 5)

ต้องกำหนด `Identity:Csrf:KeyRingPath`, `CertificatePath` และ certificate password (ถ้ามี) ตามคู่มือ key ring ด้านล่างก่อน enroll แม้ development หากใช้ ephemeral keys API จะตอบ503 แทนเก็บ factor ที่สูญหายหลัง restart สำรอง key ring พร้อม encryption certificate/private key แยกจากฐานข้อมูลด้วยช่องทางที่ได้รับอนุมัติ ห้ามใส่ secret/certificate password ใน Git

ทุก endpoint ต่อไปนี้เป็น POST ต้องมี cookie, Origin และ CSRF ที่ผูก session พร้อม response no-store:

| Endpoint | เงื่อนไข/ผลสำเร็จ |
| --- | --- |
| `/api/v1/auth/mfa/enroll` | EnrollmentRequired หรือ Staff Active ที่ login ไม่เกิน10นาที; คืน provisioningUri ครั้งเริ่มต้น ให้ authenticator อ่าน URI โดยไม่ส่งไปบริการ QR ภายนอก |
| `/api/v1/auth/mfa/confirm` | JSON `{code}` 6หลักจาก factor ที่เริ่มโดย session เดียวกัน; คืน `{session,recoveryCodes}` หลัง commit |
| `/api/v1/auth/mfa/challenge` | ChallengeRequired หรือ Active สำหรับ step-up; คืน `{session}` พร้อม cookie ใหม่ |
| `/api/v1/auth/mfa/recover` | JSON `{code}` เป็น recovery code; คืน session แบบ EnrollmentRequired โดยไม่มี assurance พร้อมยกเลิก factor/codes/session เดิมทั้งหมด |

เก็บ recovery codes ทั้ง10ชุดไว้ในที่ปลอดภัยนอกอุปกรณ์ authenticator จะแสดงครั้งเดียวและไม่อ่านกลับจากฐานข้อมูล เมื่อกู้สำเร็จต้อง enroll และ confirm factor ใหม่; รหัสเก่าและ cookie เก่าใช้ไม่ได้ การกู้ไม่ขยายเวลาสูงสุดของ login เดิม

หลัง confirm/challenge/recover ต้องขอ CSRF ใหม่ Token เก่าถูกปฏิเสธ TOTPใช้ได้ครั้งเดียวต่อ timestep แม้ส่งจาก session อื่น ยอมรับเวลาต่างกัน ±30วินาที ควร sync เวลาเครื่อง/API กับแหล่งเวลาที่เชื่อถือได้

Enrollment และ restricted challenge หมดอายุ10นาที ให้ logout/login ใหม่; pending enrollment ที่หมดอายุเริ่มใหม่ได้ และจะยกเลิก pending เดิม Confirmation ที่ไม่สำเร็จไม่เปิด factor จริง Assuranceครบ15นาทีจะเห็น `MfaChallengeRequired`/permissionsว่างและต้องส่ง challenge ใหม่ PasswordChangeRequired ใช้ MFA ไม่ได้จน Task7เปลี่ยนรหัสผ่านครบ

รหัสผิด5ครั้งใน15นาทีล็อก MFA account15นาที คำขอถัดไป429พร้อม Retry-After เปลี่ยน session หรือรอ IP rate limitครบ1นาทีไม่ล้าง account lock; keyหาย/ถอดรหัสไม่ได้503แบบไม่ให้ assurance อย่าสร้าง key ใหม่ทับแล้วคาดว่า factorเก่าจะใช้ได้

Operator-assisted recovery ยังไม่เปิด HTTP ใน Task5 ต้องมี `users:recover-mfa` แยกจาก users:manage, recent MFAไม่เกิน15นาที และห้ามทำให้ตนเอง ต้องยืนยันตัวบุคคลนอกระบบตามนโยบายองค์กรก่อนบันทึกเหตุผล/เลขอ้างอิงเคส ห้ามใส่รหัสลับหรือเอกสารส่วนบุคคลดิบใน audit ไม่มี backdoor ข้าม MFA ให้ผู้ดูแลคนเดียวที่สูญเสียทั้ง factor และ recovery codes

Bootstrapใหม่ seed capability `users:recover-mfa` เพิ่มด้วย ฐานข้อมูลที่มีบัญชีอยู่ก่อนแล้วจะไม่ถูกเปลี่ยน grants อัตโนมัติ; Task6ต้องตรวจ catalog/grants และ operator ที่ได้รับอนุมัติก่อนเปิด route

## สร้างผู้ดูแลเริ่มต้น (Task 4)

เตรียม PostgreSQL และรัน migrations ตามคู่มือฐานข้อมูลก่อน กำหนด `TPR10_CONNECTION_STRING` ของฐานข้อมูลเป้าหมายผ่านช่องทางลับที่องค์กรอนุมัติ จากนั้นเปิด terminal แบบ interactive ในรากโปรเจกต์:

```sh
dotnet run --project backend/src/TPR10.Api -- --bootstrap-admin
```

คำสั่งไม่เปิด web server และไม่รัน migration ให้อัตโนมัติ รับชื่อผู้ใช้ รหัสผ่าน และยืนยันรหัสผ่านจาก prompt โดยไม่แสดงรหัสผ่านทั้งสองครั้ง ถ้ายืนยันไม่ตรงกันจะไม่สร้างบัญชี/catalog/audit ห้ามใส่รหัสผ่านใน arguments, environment, pipe, log หรือเอกสาร ใช้ Escape ยกเลิกขณะกรอกรหัสผ่าน

รหัสออก: `0` สร้างสำเร็จ, `2` มีบัญชีใดก็ตามอยู่แล้วจึงไม่เปลี่ยนแปลง, `1` ข้อมูลผิดหรือฐานข้อมูล/audit ล้มเหลว, `64` รูปแบบคำสั่ง/terminal/config ไม่ถูกต้อง ไม่มีบัญชีหรือรหัสผ่านเริ่มต้นให้ ใช้รหัสผ่านตามนโยบาย Argon2id ของระบบ บัญชีแรกต้องตั้งค่า MFA ตามหัวข้อ Task5 ก่อนใช้สิทธิ์ผู้ดูแล และ privileged API ยังรอ Task6

สอง process แข่งกันจะสร้างได้เพียงหนึ่งราย ใช้ transaction และ advisory lock `7241002` ร่วมกับ account mutations; seed 5 role classes และ permissions `users:manage`, `roles:manage`, `roles:read`, `audit:read`, `system:probe`, `users:recover-mfa` ด้วย ID คงที่ ไม่มีบัญชีทดลอง หากเชื่อมต่อขาดระหว่าง commit ให้ตรวจสถานะฐานข้อมูลก่อน retry; ไม่รับรอง exactly-once acknowledgement

### ข้อตกลงบัญชีที่จะเปิดใน Task 6

- `POST /api/v1/users`: สร้างบัญชี บังคับเปลี่ยนรหัสผ่านในการเข้าใช้ครั้งแรก; ไม่มี self-registration
- `PATCH /api/v1/users/{id}`: เปลี่ยน active หรือ roles; ต้องมี `users:manage` และเมื่อระบุ roles ต้องมี `roles:manage` เพิ่มด้วย
- `GET /api/v1/users`: page เริ่ม 1, pageSize เริ่ม 25 และจำกัด 100; ไม่คืน credential, MFA factor หรือ token
- ชื่อ normalize ซ้ำตอบ 409; ข้อมูลผิด 400; ไม่พบ target 404; ไม่มีสิทธิ์ 403; ห้ามปิด/ถอดผู้ดูแล active คนสุดท้าย (409)
- เปลี่ยน active/roles จะเพิ่ม security version และ revoke session พร้อม audit ใน transaction เดียว; audit เขียนไม่ได้ต้อง rollback ทั้งรายการ
- ตัวเชื่อม API อ่าน actor จาก authenticated principal ไม่รับจาก JSON และตั้ง no-store ใน handler; Task 6 ต้องทดสอบ permission+MFA รวม no-store ของกรณีถูกปฏิเสธ/exception ก่อนเปิด mapping ไม่ถือการทดสอบ use case ใน Task 4 เป็นหลักฐาน HTTP RBAC

ชุดทดสอบ CLI ใช้ Python 3 และ PTY บน macOS/Linux พร้อม .NET 10 และ Docker PostgreSQL; ไม่สร้างบัญชีในฐานข้อมูลใช้งานจริง

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
5. คำขอไม่ผ่านจะเป็น 403 พร้อม audit `security.csrf.denied` ก่อนเรียก endpoint ไม่มี business mutation; หากฐานข้อมูล/audit ใช้ไม่ได้ใน auth flow จะ fail closed เป็น 503 โดยไม่แสดงรายละเอียดภายใน (endpoint อื่นอาจใช้ generic 500 เดิม)

หลัง Login สำเร็จ server consume pre-auth flow ใน transaction เดียวกับ session/audit แล้วลบ pre-auth cookie ผู้ใช้ต้องเรียก CSRF ใหม่ซึ่งผูกกับ session ID; ใช้ได้ทั้ง restricted/active session แต่ไม่ใช่หลักฐานสิทธิ์ ถ้ามี session cookie ที่ไม่ผ่านการตรวจ จะไม่ลดกลับไปใช้ pre-auth ในคำขอเดียวกัน cookie ที่หมดอายุ/ผิดรูปจะถูกลบเพื่อให้คำขอถัดไปเริ่ม flow ใหม่ได้

Login ที่ไม่มี CSRF ได้ 403; ถ้า CSRF ถูกต้องจึงตรวจรหัสผ่าน ส่วน technical-probe เดิมยังเปิดเฉพาะ Development/Testing ไม่ใช่ endpoint ทางธุรกิจที่ผ่าน RBAC แล้ว

## สัญญา Login และ Session

| Endpoint | ผลลัพธ์ |
| --- | --- |
| `POST /api/v1/auth/login` | JSON `{username,password}`; สำเร็จ 200 พร้อม SessionView และ cookie; ข้อมูลไม่ถูกต้อง 401 แบบไม่บอกว่ามีบัญชีหรือไม่; ถูกล็อก/เกินอัตรา 429 พร้อม Retry-After |
| `GET /api/v1/auth/session` | 200 พร้อม `userId`, ชื่อ `stage`, `permissions`, `mfaVerifiedAtUtc`; ไม่มี session ที่ใช้ได้ 401 ไม่ redirect |
| `POST /api/v1/auth/logout` | ต้องมี session และ CSRF ใหม่; revoke session พร้อม audit ใน transaction แล้วตอบ 204 และลบ cookie |

ทุก response ในกลุ่ม auth มี `Cache-Control: no-store` ทั้งสำเร็จและล้มเหลว Login ขณะมี session ที่ใช้ได้ตอบ 409 ให้ Logout ก่อนเปลี่ยนบัญชี ไม่มี remember-me และไม่รับ stage/permission จากผู้ส่ง

ข้อค้าง Minor ของ Task 3: 409 กรณี Login ซ้ำยังไม่มี body แบบ Problem Details; consumer ต้องรับ body ว่างได้ จะแก้มาตรฐาน error แยกตามรายการตรวจทาน

Logout ใช้ conditional revoke ใน transaction: หาก rotation เปลี่ยน session หลังผ่าน authentication แล้ว Logout จะตอบ 401 ให้ตรวจสถานะใหม่ ไม่แจ้ง 204 เท็จ หาก Logout ชนะก่อน การหมุน token เก่าจะถูกปฏิเสธ ไม่เกิด session ใหม่

Cookie `__Host-tpr10_session` ใช้ Secure, HttpOnly, SameSite=Lax, Path=/ ไม่มี Domain ค่า random 32 bytes ส่งเป็น base64url และเก็บเฉพาะ SHA-256 ใน DB ไม่ส่ง token ใน body หรือบันทึกใน application/audit log

Session หมดอายุเมื่อไม่ใช้งาน 30 นาที หรือครบ 8 ชั่วโมงนับจาก Login แม้มีการใช้งานต่อเนื่อง Middleware ตรวจ revocation, account active, security version, assurance ที่จำเป็น และอ่าน permission ปัจจุบันทุกคำขอ การต่อ idle เกิดหลังผ่าน transport/CSRF/authorization เท่านั้น จึงไม่ต่ออายุจากคำขอ CSRF ผิด

Stage ที่ต้องเปลี่ยนรหัสผ่านมาก่อน MFA; ถ้ามี factor ยืนยันแล้วใช้ `MfaChallengeRequired`, ถ้า role class บังคับแต่ยังไม่มี factor ใช้ `MfaEnrollmentRequired`; ทั้งสาม restricted stage คืน permissions ว่างและ middleware ปฏิเสธ action นอก allowlist การทำ enrollment/challenge ใช้งานผ่าน API Task5 ตามหัวข้อข้างต้น

Lockout เก็บในตารางเพิ่ม `login_attempt_windows` โดย hash ของชื่อที่ normalize แล้ว ทั้งชื่อที่มีและไม่มีบัญชี: ผิด 5 ครั้งใน 15 นาที คำขอถัดไปพัก 15 นาที ตารางจำกัด 10,000 entries และล้างเมื่อพ้นอายุ 30 นาที; เมื่อเต็มไม่เปิด bucket ใหม่ ตอบ 429 counters อยู่ข้าม restart และ row lock ป้องกันการนับขาดเมื่อส่งพร้อมกัน ค่าชุดนี้เป็นนโยบายพัฒนา/ทดสอบตามแผน ต้องประเมิน capacity และผลกระทบการล็อกบัญชีเป้าหมายก่อน production

Migration เพิ่มตารางใหม่เท่านั้น ไม่แก้ migration เดิมหรือ audit history ให้รัน `dotnet ef database update --project backend/src/TPR10.Api` กับฐานข้อมูลพัฒนาที่ระบุไว้ก่อนเรียก Login; deployment จริงต้องทบทวน migration ตามกระบวนการเดิม

`ISessionService.IssueAsync`, `RevokeUserAsync`, `RotateAsync` เปลี่ยน tracked entities ไม่ commit เอง ผู้เรียกเป็นเจ้าของ transaction/audit/SaveChanges โดย Rotate ต้องอยู่ใน transaction และต้องพิสูจน์ stage/MFA จากฝั่ง server ก่อนเรียก ไม่เปิดเป็น public endpoint ใหม่; token เก่าถูก revoke และ CSRF เก่าใช้กับ token ใหม่ไม่ได้ โดยรักษาเวลาเริ่ม/หมดอายุสูงสุดเดิม

Tasks 4–7 ที่เปลี่ยนบัญชี รหัสผ่าน role หรือ assurance ต้องใช้ security version/revocation/rotation ใน transaction เดียวกับ mutation; `RevokeUserAsync` เพิ่ม security version ของ user ด้วยเพื่อครอบคลุม session ที่ออกพร้อมกับการเพิกถอน caller ต้องจัดการ concurrency conflict แบบ fail closed ไม่แก้ role แล้วถือว่า cookie เก่าถูกเพิกถอนอัตโนมัติ การป้องกัน business permission และความสดของ MFA ยังต้องทำ Task 6

Audit Login/Logout สำเร็จมี actor/target user และ correlation; Login ผิด/ถูกล็อกไม่บันทึกชื่อที่กรอกหรือรหัสผ่าน หาก audit ล้มเหลว session/การ consume pre-auth/การ revoke จะ rollback ด้วย ไม่มีการส่ง session cookie ก่อน commit ผล commit ที่ไม่แน่นอนจากเครือข่ายต้องตรวจ session ก่อน retry ไม่รับรอง exactly-once delivery

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

รัน backend tests/build/format, Node tests, Next lint/build และ independent code review ก่อนถือว่าจบ Task 3 ลำดับปัจจุบันคือ authentication → CSRF → authorization → idle activity → endpoint; ห้ามถือว่า pre-auth validation หรือ stage เป็นหลักฐาน business permission

ยังต้องตรวจ production capacity, key backup/rotation, browser flow ใน Task 8 และ dependency vulnerabilities เดิมก่อน deploy รอบนี้ไม่อัปเกรด dependency ข้าม major หรือรับรองความพร้อมทั้ง Module 2
