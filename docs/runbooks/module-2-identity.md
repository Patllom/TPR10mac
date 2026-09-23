# คู่มือ Module 2 — HTTPS, Session บัญชีผู้ใช้ MFA สิทธิ์ และ Exit Gate

## ขอบเขต

มี API สำหรับ CSRF, Login, Session, Logout, MFA และ reset พร้อม CLI สร้างผู้ดูแลแรก, API จัดการบัญชี/role ภายใต้ permission+MFA และหน้า Login/Portal แล้ว ผล technical verification แยกจากการอนุมัติขึ้น Production ดู [Exit Gate Module 2](../architecture/module-2-exit-gate.md)

## สิทธิ์และ Audit (Task 6)

ทุก route ด้านล่างต้อง Active session, capability ปัจจุบันและ MFAไม่เกิน15นาที; unsafe methods ต้อง Origin/CSRF ด้วย ทุกresponse no-store:

| Endpoint | Capability | ข้อมูลเข้า |
| --- | --- | --- |
| GET/POST `/api/v1/users`, PATCH `/api/v1/users/{id}` | `users:manage`; ระบุRoleIdsต้อง`roles:manage`เพิ่ม | สัญญาบัญชีด้านล่าง |
| GET/POST `/api/v1/roles` | `roles:manage` | GET `page`เริ่ม1/`pageSize`เริ่ม25สูงสุด100 คืน`{items,total,page,pageSize}`; POST `{name,roleClass}` |
| PATCH `/api/v1/roles/{id}` | `roles:manage` | `{name}` classเปลี่ยนไม่ได้ |
| PUT `/api/v1/roles/{id}/permissions` | `roles:manage` | `{permissionIds:[UUID]}` แทนรายการเดิมทั้งชุด |
| PUT `/api/v1/users/{id}/roles` | `roles:manage` | `{roleIds:[UUID]}` แทนรายการเดิมทั้งชุด |
| GET `/api/v1/permissions` | `roles:read` | catalogระบบ สร้างcapabilityเองไม่ได้ |
| POST `/api/v1/users/{id}/sign-out-everywhere` | `users:manage` | ยกเลิกทุกsessionและเพิ่มversion |
| POST `/api/v1/users/{id}/mfa/recover` | `users:recover-mfa` | `{reason,evidenceReference}` ห้ามself-recovery |

GET `/api/v1/system/identity-probe` และ POST `/api/v1/system/technical-probes` เฉพาะTesting/Development ต้อง`system:probe`+MFA ปัญหาที่policyปฏิเสธใช้ `urn:tpr10:session-required`, `permission-denied`, `mfa-required`, `stage-restricted` พร้อมcorrelation

เปลี่ยนgrants/rolesจะrevokeaffectedusersรวมผู้ทำรายการถ้าตนได้รับผล ต้องlogin/MFAใหม่ไม่ใช้cookie/CSRFเก่า ถ้าทำให้ไม่มีadminclassหรือadminที่มีusers:manage/roles:manageเหลือจะ409; ไม่พบtarget404; รายการซ้ำ/UUIDที่ไม่มี/ข้อมูลผิด400

ก่อนใช้ฐานเก่าให้backupและรันmigrationตามคู่มือdeploymentโดยตรวจdestination ห้ามใช้ฐานproductionทดสอบ `ExpandIdentityAudit` ไม่เขียนทับauditเก่า ไม่คืนgrantที่เคยถอด เติมcatalogเฉพาะฐานbootstrapเดิม ผู้มีroles:manage+MFAใช้GETpermissions/PUTrolepermissionsเพื่ออนุมัติcapabilityใหม่อย่างตั้งใจ หากไม่มีผู้มีอำนาจเหลือให้หยุดและใช้ขั้นตอนกู้คืนที่องค์กรอนุมัติ ไม่แก้SQLสิทธิ์โดยพลการ

Auditใหม่เก็บactor/roleที่APIเลือกจริง/action/target/outcome/correlationและscopenullจนModule3 คอลัมน์ใหม่ในauditเก่าnullไม่ใช่ข้อมูลสูญหาย Audit+mutationrollbackพร้อมกัน ห้ามใส่password/token/secret/หลักฐานบุคคลดิบในreason/evidenceReference; metadataallowlistไม่ตรวจsecretที่แฝงในข้อความอิสระได้ทั้งหมด

HTTPS smoke แบบไม่ใส่ `--e2e` ใช้ session fixture เฉพาะฐานทดสอบ; แบบ `--e2e` ตรวจ login/TOTP ใน browser จริงร่วมกับ API integration tests

ข้อค้างMinor Task6: test audit failure ของการเปลี่ยน grants ตรวจ rollback grants/security version แล้ว แต่ยังไม่ได้ให้targetloginก่อนfault จึงไม่ใช่หลักฐานเฉพาะว่าsessionrow/cookieย้อนกลับครบ โค้ดใช้transactionเดียวกัน; เก็บเพิ่มregressionกรณีนี้แยกในรอบถัดไป

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

ข้อค้าง Minor Task5: หาก logout หรือ provisioning response สูญหายก่อน confirm ต้องรอ pending factor ครบ10นาทีนับจาก enroll แล้ว logout/login ใหม่ก่อนเริ่มใหม่ แม้ session เดิมหมดเวลาเร็วกว่านั้นก็ยังต้องรอ factor ไม่มี endpoint ยกเลิก pending ในรอบนี้

รหัสผิด5ครั้งใน15นาทีล็อก MFA account15นาที คำขอถัดไป429พร้อม Retry-After เปลี่ยน session หรือรอ IP rate limitครบ1นาทีไม่ล้าง account lock; keyหาย/ถอดรหัสไม่ได้503แบบไม่ให้ assurance อย่าสร้าง key ใหม่ทับแล้วคาดว่า factorเก่าจะใช้ได้

Operator-assisted recovery เปิด HTTP ตามหัวข้อ Task6 ต้องมี `users:recover-mfa` แยกจาก users:manage, recent MFAไม่เกิน15นาที และห้ามทำให้ตนเอง ต้องยืนยันตัวบุคคลนอกระบบตามนโยบายองค์กรก่อนบันทึกเหตุผล/เลขอ้างอิงเคส ห้ามใส่รหัสลับหรือเอกสารส่วนบุคคลดิบใน audit ไม่มี backdoor ข้าม MFA ให้ผู้ดูแลคนเดียวที่สูญเสียทั้ง factor และ recovery codes

Bootstrapใหม่ seed capability `users:recover-mfa` เพิ่มด้วย ฐานข้อมูลเดิมไม่เปลี่ยน grants อัตโนมัติ ให้ตรวจ catalog/grants และ operator ที่ได้รับอนุมัติตามขั้นตอน Task6 ด้านบน

## สร้างผู้ดูแลเริ่มต้น (Task 4)

เตรียม PostgreSQL และรัน migrations ตามคู่มือฐานข้อมูลก่อน กำหนด `TPR10_CONNECTION_STRING` ของฐานข้อมูลเป้าหมายผ่านช่องทางลับที่องค์กรอนุมัติ จากนั้นเปิด terminal แบบ interactive ในรากโปรเจกต์:

```sh
dotnet run --project backend/src/TPR10.Api -- --bootstrap-admin
```

คำสั่งไม่เปิด web server และไม่รัน migration ให้อัตโนมัติ รับชื่อผู้ใช้ รหัสผ่าน และยืนยันรหัสผ่านจาก prompt โดยไม่แสดงรหัสผ่านทั้งสองครั้ง ถ้ายืนยันไม่ตรงกันจะไม่สร้างบัญชี/catalog/audit ห้ามใส่รหัสผ่านใน arguments, environment, pipe, log หรือเอกสาร ใช้ Escape ยกเลิกขณะกรอกรหัสผ่าน

รหัสออก: `0` สร้างสำเร็จ, `2` มีบัญชีใดก็ตามอยู่แล้วจึงไม่เปลี่ยนแปลง, `1` ข้อมูลผิดหรือฐานข้อมูล/audit ล้มเหลว, `64` รูปแบบคำสั่ง/terminal/config ไม่ถูกต้อง ไม่มีบัญชีหรือรหัสผ่านเริ่มต้นให้ ใช้รหัสผ่านตามนโยบาย Argon2id ของระบบ บัญชีแรกต้องตั้งค่า MFA ตามหัวข้อ Task5 ก่อนใช้ privileged API ตามตาราง Task6

สอง process แข่งกันจะสร้างได้เพียงหนึ่งราย ใช้ transaction และ advisory lock `7241002` ร่วมกับ account mutations; seed 5 role classes และ permissions `users:manage`, `roles:manage`, `roles:read`, `audit:read`, `system:probe`, `users:recover-mfa` ด้วย ID คงที่ ไม่มีบัญชีทดลอง หากเชื่อมต่อขาดระหว่าง commit ให้ตรวจสถานะฐานข้อมูลก่อน retry; ไม่รับรอง exactly-once acknowledgement

### ข้อตกลงบัญชีที่เปิดใน Task 6

- `POST /api/v1/users`: สร้างบัญชี บังคับเปลี่ยนรหัสผ่านในการเข้าใช้ครั้งแรก; ไม่มี self-registration
- `PATCH /api/v1/users/{id}`: เปลี่ยน active หรือ roles; ต้องมี `users:manage` และเมื่อระบุ roles ต้องมี `roles:manage` เพิ่มด้วย
- `GET /api/v1/users`: page เริ่ม 1, pageSize เริ่ม 25 และจำกัด 100; ไม่คืน credential, MFA factor หรือ token
- ชื่อ normalize ซ้ำตอบ 409; ข้อมูลผิด 400; ไม่พบ target 404; ไม่มีสิทธิ์ 403; ห้ามปิด/ถอดผู้ดูแล active คนสุดท้าย (409)
- เปลี่ยน active/roles จะเพิ่ม security version และ revoke session พร้อม audit ใน transaction เดียว; audit เขียนไม่ได้ต้อง rollback ทั้งรายการ
- ตัวเชื่อม API อ่าน actor จาก authenticated principal ไม่รับจาก JSON; middlewareตั้ง no-store ก่อนauthorization ส่วนTask6ทดสอบ permission+MFA และdenialทุกadminrouteจริง ไม่ใช้ผลusecaseTask4แทนHTTP RBAC

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

Login ที่ไม่มี CSRF ได้ 403; ถ้า CSRF ถูกต้องจึงตรวจรหัสผ่าน ส่วน technical-probe เปิดเฉพาะ Development/Testing และตรวจ named permission/MFA แล้ว แต่ไม่ใช่ business endpoint ที่ผ่าน scope authorization

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

Tasks 4–7 ที่เปลี่ยนบัญชี รหัสผ่าน role หรือ assurance ต้องใช้ security version/revocation/rotation ใน transaction เดียวกับ mutation; `RevokeUserAsync` เพิ่ม security version ของ user ด้วยเพื่อครอบคลุม session ที่ออกพร้อมกับการเพิกถอน caller ต้องจัดการ concurrency conflict แบบ fail closed ไม่แก้ role แล้วถือว่า cookie เก่าถูกเพิกถอนอัตโนมัติ Task6บังคับnamedpermission/MFAแล้ว ส่วนscopeธุรกิจอยู่Module3

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

## Password reset และ forced password change (Task 7)

| Endpoint (POST ทั้งหมด) | สิ่งที่ต้องส่ง | ผลสำเร็จ |
| --- | --- | --- |
| `/api/v1/auth/password-reset/request` | `{username}` และ pre-auth CSRF/Origin | 202 ข้อความทั่วไปเหมือนกัน ไม่รับรองว่ามีบัญชีหรือส่งอีเมลแล้ว |
| `/api/v1/auth/password-reset/complete` | `{token,password}` และ CSRF/Origin | 204; token ใช้ครั้งเดียว เพิกถอนทุก session และ token เก่า ไม่มี auto-login |
| `/api/v1/auth/password/change` | `{currentPassword,newPassword}` พร้อม session และ CSRF/Origin | 204; รองรับ Active/PasswordChangeRequired ล้าง cookie และต้อง login ใหม่ |
| `/api/v1/users/{id}/password-reset` | session ของผู้ดูแลที่มี `users:manage`, MFA ปัจจุบัน และ CSRF/Origin | 200 `{temporaryPassword,expiresAtUtc}` แสดงครั้งเดียวผ่าน no-store response |

รหัสชั่วคราวหมดอายุใน 15 นาที ใช้ login สำเร็จได้ครั้งเดียวและได้เฉพาะ `PasswordChangeRequired` ห้ามเข้าหน้าธุรกิจก่อนเปลี่ยนรหัส หลังเปลี่ยนต้อง login ใหม่และทำ MFA ตาม role/factor เดิม หาก response หายหรือ session หลัง login หาย ให้ผู้ดูแลออกใหม่ ห้ามบันทึกหรือส่งรหัสใน email/log/screenshot/ticket ผู้ดูแลต้องส่งต่อผ่านช่องทางยืนยันตัวบุคคลที่องค์กรอนุมัติ

Email reset ใน production **ยังไม่พร้อม**: `Identity:Reset:EmailEnabled` ต้องเป็น false (ค่าเริ่มต้น production) การตั้ง true ทำให้ startup validation ปฏิเสธ จนกว่าจะเชื่อม delivery adapter Module 5 Endpoint ยังคงตอบข้อความทั่วไป 202 แต่ไม่สร้าง outbox และไม่กล่าวว่าส่งแล้ว

Testing/Development เปิดได้เมื่อมี persistent key ring พร้อม certificate ตามข้อกำหนด CSRF ตั้ง `Identity:Reset:EmailEnabled=false` เพื่อปิดได้ เมื่อเปิด คำขอที่เข้าเกณฑ์มี token hash ใน DB และ payload เข้ารหัสใน outbox แต่ยังไม่มี background email worker ผู้พัฒนาเรียก `ResetOutboxDispatcher.DispatchAsync(requestId, ct)` ผ่าน DI scope และอ่านครั้งเดียวด้วย `DevelopmentResetSink.Take(requestId)` ผ่าน DI เท่านั้น ไม่มี HTTP route สำหรับดู token และ sink ไม่ถูกลงทะเบียนใน production ห้าม log ค่าที่อ่านได้

คำขอซ้ำขณะที่ token เดิมยังใช้ได้ไม่สร้าง token ใหม่ Outbox retry ใช้ request ID/payload เดิม สูงสุด 5 ครั้ง เว้น 1 นาที และไม่ส่งเมื่อหมดอายุ/ใช้แล้ว/เพิกถอน/บัญชีปิด/recipient เปลี่ยน Adapter จริงต้อง deduplicate ด้วย request ID เพราะการส่งกับ DB commit ไม่ใช่ exactly-once และต้องมี retention/monitoring ใน Module 5

Migration `AddTemporaryCredentialLifecycle` เพิ่ม nullable expiry/consumed ใน local credential ไม่เปลี่ยน credential เดิม ห้าม downgrade หลังเปิดใช้ temporary password โดยไม่มีแผนรักษาการ consume/expiry ไม่ล้าง MFA หรือ lockout เดิมเมื่อ reset; หากยังติด lockout ต้องรอเวลานโยบายเดิม

`password/change` ใช้ persistent account budget ร่วมกับ login: ยืนยัน current password ผิด 5 ครั้งใน 15 นาทีจะพัก 15 นาที ครอบคลุมทุก session และการยิงพร้อมกัน เมื่อถูกพักคืน 429 พร้อม `Retry-After` แม้ current password ถูกต้อง ต้องรอจนหมด lockout ไม่สามารถสลับระหว่าง login/change เพื่อหลบ budget; บันทึก failed/throttled audit โดยไม่มีรหัสผ่าน

รายละเอียดหลักฐานและข้อจำกัด รวม Minor เรื่อง eligible-account enumeration coverage อยู่ใน [รายงาน Task 7](../architecture/module-2-task-7-verification.md)

## Task 8: หน้าเว็บและการทดสอบ HTTPS

เส้นทางใช้งานคือ `/` → `/login` → ขั้นเปลี่ยนรหัสผ่านหรือ MFA ที่ API กำหนด → `/portal` และ `/portal/account` ไม่ถือหน้า Portal เป็นตัวแทนการตรวจ permission ของ API

ใช้ Node 22.23.2 ตาม `.nvmrc` หรือรุ่นที่ผ่าน `engines` ใน package.json; Next dev ยังใช้ 4000 และ production ยังใช้ 4001:

```bash
npm ci
npx playwright install firefox
npm test
npm run lint
npm run build
node infra/nginx/smoke-identity-https.mjs 4001 --e2e
node infra/nginx/smoke-identity-https.mjs 4000 --e2e
```

คำสั่ง HTTPS ต้องมี Docker Desktop, .NET 10 SDK/EF tool และ OpenSSL ใน PATH พอร์ต 4443 และพอร์ต Next ที่เลือกต้องว่าง ชุดทดสอบสร้าง PostgreSQL/NGINX/API container ใหม่ ใช้ key ring/CA ชั่วคราว และปิดเฉพาะทรัพยากรที่สร้างเองเมื่อจบ ไม่แตะฐานข้อมูลเดิมหรือ trust store ของ macOS

Browser ใช้ Firefox ของ Playwright พร้อม certificate policy เฉพาะ disposable profile; `ignoreHTTPSErrors` เป็น false การทดสอบไม่ครอบคลุม Chrome/WebKit ชุดทดสอบเพิ่ม rate budget เฉพาะ fixture; production/default policy ไม่เปลี่ยน

`TPR10_API_ORIGIN` เป็น server-only origin ที่ operator กำหนด ไม่ใช้ `NEXT_PUBLIC_` และไม่คำนวณจาก incoming Host สำหรับ fixture ใช้ `https://localhost:4443` ซึ่ง NGINX ส่ง `/api` ไป API loopback พร้อม trusted forwarded headers; Next process เชื่อถือ CA ผ่าน `NODE_EXTRA_CA_CERTS` เฉพาะ process การตั้งค่า production ต้องมี private routing/TLS/trusted proxy ที่ส่ง canonical authority ตาม API allowlist ตรงกัน **การชี้ตรง HTTP 5080 โดยไม่มี trusted HTTPS metadata ใช้ authenticated session ไม่ได้**

ฟอร์มจะปิด input/button จน JavaScript พร้อม หากผู้ใช้ปิด JavaScript จะมีคำแนะนำและไม่ส่ง password ด้วย native GET เมื่อเปลี่ยน session ใช้ full navigation; กลับจาก bfcache ให้ตรวจ server ใหม่ ไม่เก็บ session token ใน JavaScript/storage และไม่ใช้ QR service ภายนอกส่ง MFA secret

Reset link ใช้ `/auth/reset#token=...` เท่านั้น หน้าเว็บย้ายค่าเข้า memory และล้าง fragment รวมการเปิดลิงก์ใหม่ในหน้าเดิม ไม่ส่ง token ใน query; refresh หลังล้าง fragment จะสูญเสีย token ใน memory ซึ่งต้องเปิดลิงก์ที่ได้รับใหม่ ห้ามนำ URL/token ไปใส่ ticket หรือ log การส่งอีเมล production ยังปิดรอ Module 5; การแสดงข้อความทั่วไปไม่ใช่หลักฐานว่าส่งอีเมลแล้ว

`POST /api/v1/auth/logout-all` เป็น self-service แยกจาก admin sign-out เดิม: actor มาจาก cookie ที่ตรวจซ้ำใน transaction, body ไม่มีสิทธิ์เลือกผู้ใช้อื่น, revoke ทุก session/security version และ audit commit พร้อมกัน ใช้ CSRF/Origin validation เหมือน mutation อื่น

Dependency อัปเกรดตามการอนุมัติผู้ใช้เป็น Next 15.5.26/React 19.3 และมี PostCSS override เฉพาะ Next เป็น 8.5.28 ต้องตรวจ audit และ compatibility ใหม่ทุกครั้งที่ปรับรุ่น ไม่ใช้ `npm audit fix --force` โดยไม่ตรวจผล ดู [รายงาน Task 8](../architecture/module-2-task-8-verification.md) สำหรับหลักฐานและข้อจำกัดล่าสุด

ข้อจำกัด UX ที่ทราบจาก review: เมื่อ session หมดอายุ cookie เก่าอาจยังอยู่ใน browser การกด login ครั้งแรกจะหยุดที่ CSRF 403 และล้าง cookie ให้กดเข้าสู่ระบบอีกครั้ง ระบบยังไม่ retry mutation อัตโนมัติ ประเด็นนี้บันทึกเป็น Minor เพื่อเพิ่ม regression และแก้แยก ไม่ใช่ authentication bypass

## จุดตรวจรับก่อนขั้นถัดไป

รัน backend tests/build/format, Node tests, ESLint/Next build, browser E2E และ independent code review ก่อนปิด technical gate ลำดับ API คือ authentication → CSRF → authorization → restricted stage → idle activity → endpoint; ห้ามถือว่า pre-auth validation หรือ stage เป็นหลักฐาน business permission

ยังต้องผ่าน Security owner, production capacity, key backup/rotation, deployment topology และเงื่อนไขใน Exit Gate ก่อน deploy ไม่รับรองพร้อม production จากผล Test/Build/Lint เพียงอย่างเดียว

## Task 9: OpenAPI และการรับงาน

เรียก `GET /api/openapi/v1.json` ผ่าน origin ของ API/proxy ที่กำหนด จะได้ cookie scheme, required CSRF header, named permissions/MFA/stage, conditional role permission และ request/response schemas ของ route ปัจจุบัน OpenAPI metadata ไม่ใช่ตัว enforcement; API guards เดิมเป็น authority

`x-tpr10-session-stages` อธิบาย session ที่มีอยู่และไม่แทนเงื่อนไขของ service; anonymous route ไม่บังคับมี cookie เพียงเพราะ extension นี้ระบุ Active ทุก mutation ขอ CSRF ใหม่หลัง session เปลี่ยน และห้าม retry POST/PUT/PATCH/DELETE อัตโนมัติเมื่อไม่ทราบผล commit

Error client ต้องใช้ status และตรวจ Content-Type ก่อน parse JSON: Login ซ้ำ 409 และ revalidation/binding บางกรณี body ว่างได้ Response ของ auth อาจมีข้อมูลลับที่แสดงครั้งเดียว เช่น provisioning URI, recovery codes หรือ temporary password ห้าม log body หรือเก็บ screenshot/trace ของผู้ใช้จริง

### Migrate และตรวจหลังอัปเกรด

1. Operations ตรวจ connection destination/backup/restore approval ก่อนเสมอ ไม่ใช้ฐาน production กับ test suite
2. กำหนด connection string ผ่าน secret management แล้ว `dotnet tool restore` และ `dotnet ef database update --project backend/src/TPR10.Api` โดยใช้ SDK ตาม global.json; ไม่ให้ web startup migrate อัตโนมัติ
3. ตรวจ applied migration/health/audit preservation และสิทธิ์ catalog ปัจจุบัน ไม่ downgrade temporary credential lifecycle หรือแก้ migration เก่าย้อนหลัง
4. Task 9 เปลี่ยนเอกสาร/schema ไม่เพิ่ม migration หรือ grant ผู้ใช้โดยอัตโนมัติ ผู้ดูแลต้องอนุมัติ role grants ตามขั้นตอนเดิม

### Restore key ring และตรวจการกู้คืน

1. ต้องมี incident/restore approval และสำรองฐานข้อมูล, key-ring directory, encryption certificate/private key ที่สัมพันธ์กัน ผ่านช่องทางลับแยกกัน; ห้ามเขียน secret ลง ticket/log/Git
2. ใช้ environment กู้คืนที่แยกจาก production หยุด API/worker ของ environment เป้าหมายตามแผน ไม่ copy ทับ service ที่กำลังเขียน key
3. Operations restore snapshot ที่เลือกพร้อม ownership/permission ของ service account; กำหนด application name `TPR10.Identity`, path และ certificate ที่ตรงกัน ห้ามลบ key เก่าหรือสร้างใหม่ทับโดยหวังถอดรหัส factor เดิม
4. ตรวจ certificate/private key/password โดยเรียก key-dependent operation จริงด้วยบัญชีทดสอบที่ได้รับอนุมัติ ไม่ถือเพียง startup สำเร็จเป็นหลักฐาน เพราะ PFX โหลดแบบ lazy
5. ตรวจ CSRF, MFA challenge ของ factor ที่มีอยู่ และ encrypted reset payload เฉพาะใน isolated development/test sink; ห้ามส่งอีเมลจริงหรือเปิดเผยค่า secret เพื่อพิสูจน์
6. หากถอดรหัสไม่ได้ ให้ fail closed และใช้ snapshot/incident escalation ที่องค์กรอนุมัติ ไม่ข้าม MFA หรือแก้ SQL ให้มีสิทธิ์โดยพลการ บันทึกเฉพาะผล/เวลา/correlation และผู้อนุมัติ
7. ใช้ admin sign-out-everywhere/revoke ตาม policy หลัง restore หากมีความเสี่ยง session เก่าฟื้นกลับมา; เปิด traffic ได้เมื่อ Security/Operations ยอมรับผล ไม่ใช่เมื่อ agent รัน tests ผ่าน

ชุด `CsrfTests.Persistent_keys_survive_restart_and_are_encrypted_at_rest` และ `MfaResilienceTests.Restart_with_same_keys_works_but_lost_keys_fail_closed` เป็นหลักฐาน integration เฉพาะเครื่องทดสอบ ยังไม่ใช่การซ้อม restore ขององค์กร

### Revoke / reset / recovery / no-secret-log

- บัญชีถูกปิดหรือ role/grant เปลี่ยนต้องตรวจ cookie เดิมถูกปฏิเสธใน request ถัดไป; self logout-all ใช้ auth endpoint ส่วน administrator ใช้ users endpoint ตาม permission ของตน
- Admin reset ออกรหัสครั้งเดียวผ่านช่องทางยืนยันตัวบุคคลที่องค์กรอนุมัติ ผู้รับต้อง forced change แล้ว login/MFA ใหม่; ไม่เก็บรหัสใน ticket หรือ command history
- Operator MFA recovery ต้องมี `users:recover-mfa` และ recent MFA ห้าม self recovery; เหตุผล/เลขเคสไม่ใช่ที่เก็บเอกสารบุคคลหรือ secret
- ไม่เปิด HTTP body logging, cookie/header logging, browser trace/video/screenshot ของ flow จริง; ตรวจ reverse proxy/access log ไม่บันทึก query ที่มี secret ให้ reset link ใช้ fragment ตาม UI contract
- Audit ใช้ allowlist ของ metadata แต่ไม่รับรองตรวจ secret ที่แฝงใน free text ได้ทั้งหมด เมื่อสงสัยรั่วให้ revoke/rotate ตาม incident policy ไม่เผยค่าที่พบในเอกสาร
