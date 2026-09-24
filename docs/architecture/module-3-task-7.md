# Module 3 — Task 7: ส่งออกข้อมูลตามพื้นที่และตรวจความเป็นอะตอม

## ขอบเขต

เพิ่ม `POST /export-simulation` ต่อท้าย `scope-probe-records` ของ Workspace, Project และ Site รวม 3 operations คืน JSON สำหรับพิสูจน์สิทธิ์และ transaction ไม่สร้างไฟล์ ไม่ส่งไป NAS ไม่สร้างงานเบื้องหลัง และเปิดเฉพาะ Development/Testing เช่นเดียวกับ technical records ใน Task 6

ยังไม่รวมหน้าจอเลือกพื้นที่/จัดการ/ข้อมูลของ Task 8 หรือการรับรองทั้ง Module ใน Task 9 ไม่ push หรือ merge อัตโนมัติ

## พฤติกรรมที่ส่งมอบ

- ต้องมี assignment ของ `(workspaceId, projectId, siteId)` ตรงทุกช่อง รวม null และมี `scope-probe:export` พร้อม recent MFA
- Export อย่างเดียวส่ง public fields ได้ ไม่ต้องมี read เพิ่ม แต่ `restrictedNote` ต้องมี restricted-read ในพื้นที่เดียวกัน ผู้ไม่มีสิทธิ์จะไม่ได้ property นี้เลย รวมกรณีค่าจัดเก็บเป็น null
- รับเฉพาะ `createdFrom` และ `createdTo` แบบ DateTimeOffset ที่ offset เป็นศูนย์; ขอบต้นรวม ขอบปลายไม่รวม เมื่อส่งทั้งคู่ต้อง from < to ช่วงอนาคตที่เรียงถูกต้องใช้ได้และคืนรายการว่าง
- Repository กรอง exact scope และช่วงเวลาก่อนเรียง CreatedAtUtc/Id และอ่านสูงสุด 101 แถว ภายใน transaction เดียวกับการตรวจสิทธิ์ หากเกิน 100 ตอบ 400 พร้อม denial audit โดยไม่ส่งรายการบางส่วน
- Response เป็น `{items, rowCount}` ที่ materialize แล้ว ใช้ projection ร่วมกับ Task 6 ไม่มี IQueryable, stream หรือผลข้างเคียงภายนอกหลุดออกจาก transaction
- Audit `scope.record.export` ระบุ actor, acting role, scope, assignment, correlation, row-count, destination-type=`response-json` และ normalized filters รูปแบบ `O`; ขอบที่ไม่ส่งใช้ `unbounded` ไม่บันทึกเนื้อหา record หรือข้อมูลลับ
- Audit และ commit สำเร็จก่อนคืนผลให้ HTTP serialize; หาก audit insert หรือ deferred COMMIT ล้ม ต้องตอบ 503 แบบ sanitized โดยไม่ส่งข้อมูล
- ใช้ cookie, CSRF, Origin/Host และ no-store เดิม OpenAPI มี capability, MFA, response variants และ error contracts ของทั้งสามระดับ โดยยังตรวจ identity contracts เดิม

## หลักฐานเรื่องการแข่งขันและ rollback

ใช้ PostgreSQL จริงและ HTTP cookies จริง หยุดคำขอด้วย DbCommandInterceptor เฉพาะชุดทดสอบ มีสัญญาณว่าทั้ง operation และคำขอแก้สิทธิ์มาถึง advisory lock `7241002` ไม่ใช้ Sleep เพื่อเดาลำดับ

Matrix ใหม่ประกอบด้วย detail/read, update/write และ export เทียบกับ revoke assignment, เปลี่ยน role grants, ปิดบัญชี, ปิด Workspace, ปิด Project และปิด Site ทั้งสองลำดับ รวม 36 กรณี เพิ่มจาก race เดิม 9 กรณี:

- Operation ได้ lock ก่อน: สำเร็จได้ก่อนการเปลี่ยนสิทธิ์; หลัง mutation commit cookie เดิมใช้ต่อไม่ได้
- Mutation commit ก่อน: operation ตรวจสถานะใหม่และตอบ 401 โดยไม่อ่าน/เขียน/ส่งข้อมูล
- ตรวจ record/version, assignment, สถานะ parent/account, user SecurityVersion และ session revocation จริง

Fault tests ตรวจ list/detail/create/update/export/denied-export และ lifecycle mutations ทุกชนิดข้างต้น: audit ล้มต้อง rollback สิทธิ์/assignment/version/session ที่เกี่ยวข้อง และคง record เดิม Session assertions ครอบคลุม Id, Stage, SecurityVersion, ExpiresAtUtc, MfaVerifiedAtUtc และ RevokedAtUtc ไม่รวม LastSeenAtUtc ซึ่ง middleware ต่ออายุนอก business transaction

หลัง role grants เปลี่ยน มีการล็อกอินและ MFA ใหม่ก่อนเทียบ response เพื่อไม่ให้ 401 บังข้อผิดพลาด authorization: ถอน restricted-read แล้ว public export ยังได้แต่ไม่มี restricted field; ถอน export แล้วตอบ 403; role ของพื้นที่หนึ่งไม่ให้ export อีกพื้นที่ แม้บัญชีเดียวกัน

มี deferred constraint trigger ทำให้ล้มตอน COMMIT จริง เพื่อยืนยันว่า DTO ที่เตรียมไว้ยังไม่ออก HTTP และ audit ไม่ถูกบันทึกบางส่วน รวมถึงทดสอบ audit update/delete ถูกปฏิเสธ

## ข้อวินิจฉัยระหว่างทำ

1. ใช้ worktree และ dependency เดิม ไม่ทำ Tasks 1–6 ซ้ำ ไม่แก้ main — หากเลือกผิดจะเสี่ยงทับงานเดิม
2. เพิ่ม ExportAsync ที่คืน bounded array แทนเปิด IQueryable — รักษา exact-scope query และ materialization ภายใน transaction; ถ้าผิดจะเสี่ยงข้อมูลข้ามพื้นที่หรือส่งก่อน audit
3. เพิ่ม metadata allowlist/OpenAPI พร้อม routes และ test-only hooks เพื่อ acceptance ไม่รอ Task 9 — หากเลื่อนจะมีช่องว่าง contract/audit ตั้งแต่เริ่มใช้
4. เพิ่มสัญญาณ lock ทั้งสองฝั่งและ session assertions ตาม Task 7 จึงครอบคลุม Minor ที่ทับซ้อนจาก Task 6 — ไม่ถือว่าปิด Minor อื่นที่ไม่ได้ตรวจ
5. ตรวจอิสระเฉพาะ Task 7 เพิ่มตามขอบเขตส่งมอบ ไม่แทน whole-branch review — ยังไม่อนุมัติ push/merge หรือพร้อม Production
6. Lifecycle ใช้ implementation เดิมจาก Tasks 2–6 เพิ่ม regression/fault tests ไม่สร้าง production fix ที่ไม่มี RED — หาก fault ไม่ตรงจุดอาจพลาดขอบเขต transaction จึงตรวจทั้ง insert และ deferred commit
7. ผู้ตรวจไม่รับรอง Tasks 1–6 หรือ Minor เดิมทั้งหมด ตรวจเฉพาะจุดเชื่อมที่จำเป็น — ข้อค้างเดิมยังต้องตามต่อ
8. UI stale response/cache/back/logout เป็น Task 8 — E2E identity 13 กรณีไม่ใช่ scoped UI acceptance
9. Whole-branch review, throughput, multiple replicas และ production readiness เป็น Task 9/production gates — ไม่ใช้ผล Task 7 เปิด technical probes ใน Production
10. ผู้ตรวจทำ static review ส่วน runtime gates รันโดยผู้พัฒนา — ไม่อ้าง independent full rerun

## Code Review และข้อค้าง

Franklin ตรวจ tracked diff และ untracked files แบบ read-only ไม่พบ Critical/Important มี Minor 1 ข้อที่ตรวจเทียบโค้ดแล้วและบันทึกไว้ตามกระบวนการ executing-plans:

- เมื่อ export เกิน 100 รายการ ข้อความ 400 ยังเป็นข้อความทั่วไป ไม่แนะนำให้ลดช่วงเวลา แม้ enforcement และ OpenAPI ระบุเพดานถูกต้อง การแก้ต้องส่งต่อ typed denial ที่ sanitize แล้วผ่าน ScopeOperation พร้อม test ไม่ใช่เปลี่ยน title เฉพาะ service เพราะถูกแทนที่หลัง denial audit

## ผลตรวจสอบ

ผลวันที่ 24 กันยายน 2026: Test, Build, Lint และ E2E ผ่านครบ; Code Review ไม่พบ Critical/Important มี Minor 1 ข้อตามข้างต้น

- Baseline Task 6: 68/68
- RED export: 22 กรณีล้มด้วย 405 ก่อนเพิ่ม routes; OpenAPI ใหม่ 3 กรณีล้ม ขณะที่ 12 contracts เดิมผ่าน
- GREEN ชุดแรก export/audit/OpenAPI/identity: 102/102; race: 45/45
- Fault acceptance: audit 14/14 ผ่าน; fresh-login test รอบแรกชน CSRF 20/IP/นาที แก้เวลาจำลองเป็นรอบละ 1 นาที โดยไม่เปลี่ยน production limiter
- Frontend Node 24: 28/28; ESLint และ Next build ผ่าน
- HTTPS/E2E: 13/13 และ TLS/cookie/CSRF/host acceptance บนพอร์ต 4001 ผ่าน
- Backend focused รวม Task 6 และ OpenAPI: 203/203 ไม่มี skipped (4 นาที 27 วินาที)
- Backend ทั้งชุด: 783/783 ไม่มี skipped (17 นาที 4 วินาที)
- Backend Build: 0 warnings, 0 errors; `dotnet format --verify-no-changes --no-restore` ผ่าน exit 0 หลังจัด whitespace ของไฟล์ใหม่

คำสั่งตรวจใช้ Node 24.19.0 และ .NET SDK 10.0.401 ตามเครื่องมือของ workspace:

```sh
dotnet test backend/TPR10.sln --no-restore
dotnet build backend/TPR10.sln --no-restore
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
npm test
npm run lint
npm run build
node infra/nginx/smoke-identity-https.mjs 4001 --e2e
```

Logs ชั่วคราว: `/private/tmp/tpr10-m3t7-*.log` ไม่รวมใน Git

## งานถัดไป

เมื่อผู้ใช้สั่ง Task 8 จึงเริ่ม Portal selector และหน้าจอจัดการ Organization/Assignment/technical records พร้อมตรวจ stale response และการเปลี่ยน scope ไม่เริ่มอัตโนมัติจากการส่งมอบนี้
