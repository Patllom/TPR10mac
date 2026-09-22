# รายงานตรวจรับ Module 2 — Task 2

สถานะ: Task 2 ผ่าน implementation, Code Review อิสระ, การแก้ Important ด้วย TDD และ final Test/Build/Lint เมื่อ 2026-09-23 มี Minor ค้าง 1 ข้อตามรายการท้ายเอกสาร ยังไม่ใช่การรับรอง Production หรือทั้ง Module 2

## ขอบเขตที่ส่งมอบ

- Pre-auth CSRF: Data Protection token ผูกกับ cookie hash และ server-side record อายุ 10 นาที
- ตรวจ HTTPS, Origin/Host, trusted proxy, token หมดอายุ/แก้ไข/ผิด flow และปฏิเสธ session cookie จนกว่าจะมี session authority ใน Task 3
- Rate limit ต่อ IP/รวม, เพดาน pre-auth records ที่บังคับพร้อมกันด้วย DB transaction lock, cleanup รายการที่ใช้ไม่ได้
- Denial audit แยก transaction ก่อน mutation; ไม่มี token/cookie/Origin ดิบใน audit metadata
- TLS reverse proxy localhost:4443 ไป Next dev4000/production4001 และ API loopback
- Production ต้องมี explicit origins และ certificate-encrypted persistent key ring
- [คู่มือใช้งานภาษาไทย](../runbooks/module-2-identity.md) และสคริปต์ acceptance ที่ไม่เปลี่ยน system trust

ไม่มี Login, Logout, session authentication, RBAC, MFA หรือหน้า login ใหม่ใน Task 2

## หลักฐาน TDD และข้อค้นพบ

| เรื่อง | RED | GREEN |
| --- | --- | --- |
| Token/cookie/transport/expiry/binding | HTTP tests 9 ล้มเหลวก่อน middleware | 9 ผ่าน |
| Rate limiting | คำขอเกินเพดานยังได้ 200 | ได้ 429 ก่อนเพิ่ม DB record |
| Production/config bounds | config ที่ไม่ปลอดภัยยังเปิด host ได้ 6 กรณี | ทั้ง 6 ปฏิเสธ |
| HTTPS renderer | 6 กรณียังเป็น HTTP/รับ config ไม่ปลอดภัย | ทั้ง 6 ผ่าน |
| Explicit origin override | binder ต่อ array ทำให้ localhost ยังถูกอนุญาต | ไม่มี localhost เมื่อกำหนด origin เอง |
| Routing alias | trailing slash 2 กรณีข้าม transport/rate guard | endpoint metadata ครอบคลุม alias |
| Review: IP เดียวกิน global quota | HTTP test สอง IP พบว่า IP ใหม่ได้429หลังอีก IP flood | atomic admission ทำให้ IP ใหม่ยังผ่านและ denied ไม่กิน global quota |

เพิ่มหลักฐาน proxy remote ที่ไม่เชื่อถือ, purpose แยก, consumed/revoked flow, origin ซ้ำ/ผิด port/หาย, token header ซ้ำ, audit failure, concurrent capacity และกุญแจข้าม restart โดยใช้ PostgreSQL จริง ไม่ mock การตัดสินความปลอดภัย

ระหว่างทำพบ macOS ไม่รองรับ EphemeralKeySet จึงใช้ DefaultKeySet เฉพาะ macOS และให้ DI dispose certificate; TLS harness เดิมตรวจ PostgreSQL temporary socket server จึงแก้ให้รอ TCP; empty POST ที่ไม่มี body ได้ transport400 จึงทดสอบ CSRF ด้วย JSON request ที่ถูกต้อง แล้วได้403/201ตามคาด

## Final verification หลังแก้ Code Review

| คำสั่ง | ผล |
| --- | --- |
| `dotnet test backend/TPR10.sln --verbosity quiet` | 101 ผ่าน ไม่มีล้มเหลว |
| `dotnet build backend/TPR10.sln --no-restore --verbosity quiet` | ผ่าน 0 warnings/errors |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | ผ่าน |
| `npm test` | 23 ผ่าน ไม่มีล้มเหลว |
| `npm run lint` | ผ่าน ไม่มี ESLint warning/error |
| `npm run build` | ผ่าน รวม type check และ static pages |
| `node infra/nginx/smoke-identity-https.mjs 4001` | TLS จริงผ่านกับ Next production |
| `node infra/nginx/smoke-identity-https.mjs 4000` | TLS จริงผ่านกับ Next dev |
| `git diff --check` | ผ่าน |

TLS acceptance ใช้ CA ที่สร้างเฉพาะชุดทดสอบและ curl --cacert ไม่ใช้ -k และตรวจด้วยว่า client ที่ไม่ trust CA ถูกปฏิเสธ ตรวจ secure cookie, no-store, CSRF403/201 และ hostile Host จริง หยุด Next และล้าง containers/network/temp files ที่สร้างเองแล้ว ไม่ได้ติดตั้ง CA ในระบบหรือทดสอบ browser login ซึ่งยังไม่มี

## Review และการส่งต่อ

รอบตรวจทานอิสระโดย Ptolemy ตัดจาก BASE `8089675` ถึง implementation `332e929` บน branch `codex/module-2-identity`: ไม่พบ Critical, พบ Important 1 และ Minor 1 ผู้ตรวจรัน renderer tests6/6 และ diagnostic .NET ที่ยืนยัน Important โดยไม่ได้อ้างว่ารัน acceptance ทั้งหมดแทน coordinator

Important แก้ใน commit `c6fc6c6` หลัง HTTP regression test สอง IP เป็น RED แล้ว GREEN: เปลี่ยน chained limiters เป็น fixed-window admission ที่หัก per-IP/global พร้อมกัน จำกัดจำนวน IP entries ไม่เกิน global admissions ใช้ monotonic clock และเพิ่ม tests concurrency/window reset/IPv4-mapped IPv6 จากนั้น coordinator รัน full tests 101+23, Build/Lint และ TLS ทั้งสองพอร์ตซ้ำผ่าน ไม่มี second review ตามกระบวนการ inline ที่อนุมัติ ไม่มี merge/push

### Minor ที่เก็บไว้ (Deferred minors)

1. Certificate/PFX/password/private key ยังตรวจจริงเมื่อ resolve singleton ครั้งแรก ไม่ใช่ทั้งหมดตอน startup; startup validation ปัจจุบันตรวจ configuration bounds/absolute paths/ไฟล์มีอยู่ การตั้งค่ากุญแจที่เสียจึงอาจทำให้ host เริ่มได้แต่คำขอใช้งานล้มเหลว ต้องเพิ่ม fail-fast validation หรือปรับคำรับรอง startup ใน runbook ก่อน Production (ไม่มีการข้ามการเข้ารหัสหรืออนุญาต mutation เมื่อเกิดข้อผิดพลาด)

ข้อที่ผู้ตรวจเว้นไว้และ coordinator คงขอบเขต: session/rotation/role revocation Task3/6, reset/recovery/TOTP Task5/7, MFA/forced-change/authorization Task4/5/7, Next cache/return URL/browser login Task8, OpenAPI contract Task9 และ audit actor/role/scope ใน authenticated lifecycle ของงานถัดไป ยังไม่รับรอง distributed rate limits, capacity หรือ key backup/rotation เชิงปฏิบัติการ ดู rulings ใน ledger เพื่อทำต่อโดยไม่ข้ามประเด็นเหล่านี้

Task 3 ต้องใส่ authentication ก่อน CSRF และ authorization ตาม stage/permission ภายหลัง พร้อมการเปลี่ยน binding/rotation เมื่อเข้าสู่ระบบ ห้ามนำ pre-auth flow ไปใช้เป็นหลักฐานสิทธิ์

ยังต้องผ่าน production gates เรื่อง owner/hostname/TLS/key backup/rotation/capacity และแก้ dependency vulnerabilities เดิมก่อน deploy (ผล audit เดิม 4 high และ 1 critical; รอบนี้ไม่ได้อัปเกรด dependencies) ข้อ Minor 2 เรื่องจาก Task 1 ยังอยู่นอกขอบเขตรอบนี้
