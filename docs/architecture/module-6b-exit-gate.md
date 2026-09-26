# รายงานตรวจรับ Module 6B — Task 8

สถานะ: **ผ่านการตรวจรับทางเทคนิค** — แก้ HTTPS dev timeout และยืนยันใหม่ครบ dev34/34, production36/36 พร้อม Test/Build/Lint/Review แล้ว **Task 8 ทั้งหมด, NAS จริง และ Production ยังไม่ปิด**

ฐานเริ่มงาน `527dda53303d2e8d87df0a408906cddcc617b336` บน `codex/module-6b-evidence-storage` ใช้ worktree แยก ไม่แก้ checkout เดิม ไม่ push/merge อัตโนมัติ

ผู้ใช้อนุญาตหยุด Preview ชั่วคราวและเปิดกลับหลังทดสอบ และยืนยันว่ายังไม่มี NAS จึงแยก gate ดังนี้:

| Gate | สถานะ |
| --- | --- |
| ทางเทคนิค: tests/build/lint/audit/HTTPS/review/recovery | ผ่าน; backend1,270/1,270 บน source เดิมที่ตรวจ SHA ตรง, focusedใหม่16/16, Node43/43, transport1/1, HTTPSใหม่70/70, Build/Lint/Format/audit/Linux ผ่านตามเวลาที่บันทึก |
| NAS จริง | รอ test share และอนุญาต disruption เฉพาะพื้นที่ |
| Production | ยังไม่อนุมัติ ต้องมี NAS/restore/load และผู้รับผิดชอบลงนาม |

## ขอบเขตหลักฐาน

เพิ่ม `EvidenceStorageExitTests` ตรวจ Production ไม่มี HTTP publisher ทดลองหรือ public/static evidence, กู้คืน PostgreSQL ด้วย pg_dump/pg_restore ร่วมกับไฟล์สังเคราะห์และ protected key/config เปิด host ใหม่ ตรวจ identity/bytes/สิทธิ์ และงานย้ายที่ค้าง รวม Prepared ที่มี Active copies แต่ยังไม่มี publication ต้องอ่านไม่ได้

ชุดทดสอบไม่ใช่ NAS จำลองที่นับเป็น NAS จริง ไม่ restore ทับข้อมูลผู้ใช้ และไม่เพิ่ม publisher/seed production เพื่อ demo

## ข้อวินิจฉัยระหว่างดำเนินงาน

- POST `/evidence/publish` ได้ 405 จาก method matcher ก่อน route constraint ไม่ใช่ publisher ที่เปิดให้ใช้งาน จึงยอมรับ 404 หรือ 405 ควบคู่ OpenAPI allowlist เดิมที่มีเฉพาะ GET สาม operation; ไม่เพิ่ม endpoint เปล่าเพียงให้ตอบ 404 ตามตัวอย่างในแผน หากผิดอาจมองข้าม endpoint ที่ตอบ denial จึงต้องคงการตรวจ registration/allowlist ด้วย
- CSRF issuance ใช้ฐานข้อมูลจริง จึงใช้ฐานข้อมูล Testcontainers แทน connection ที่จงใจเชื่อมไม่ได้ ความล้มเหลว 503 รอบแรกเป็น fixture ไม่ใช่ช่องโหว่ Production
- การทดสอบ exit เป็น regression ของการปิดเส้นทางที่มีอยู่ ไม่สร้างช่องโหว่ใน Production เพื่อให้ได้ RED; การเพิ่ม helper backup มี compiler RED ก่อน implementation ผลนี้ไม่ใช่ RED ของ business feature
- Restore ในเครื่องคืน root เดิมและคง storage fingerprint ไม่รับรอง relocation ข้ามเครื่องโดยแก้ config fingerprint; ต้องมี deployment drill เพิ่ม

## ความครบถ้วนตามข้อกำหนด

| ข้อกำหนด | หลักฐานในระยะ 6B | ขอบเขตที่ยังค้าง |
| --- | --- | --- |
| R02 วันเวลาอยู่ใน pixels | pipeline/full/thumbnail และ visual synthetic artifacts | instant/challenge จริงและกล้องเป็น 6C |
| R03 local/NAS/เปลี่ยน/ย้าย | protected adapter, registry, migration, checksum/read tests | NAS disruption จริงและ load |
| R13 ไม่ลบอัตโนมัติ | เก็บต้นทาง/Fallback และ orphan report | ไม่มีสิทธิ์ถอดหรือลบจาก Completed |
| R14–15 สิทธิ์ภาพ | owner/HR scope, supervisor/admin denial, revocation | GPS/หน้าประวัติเป็น 6C |

## คู่มือ

ดู [คู่มือ storage และ recovery](../runbooks/module-6b-storage.md) เอกสารนี้จะบันทึก command/เวลา UTC/exit code/จำนวนจริงเมื่อชุดตรวจจบ ไม่ใช้ผล Tasks 1–7 แทนผล Task 8

## คำสั่งและผลรอบหลังแก้ Review backend (ก่อนแก้ transport)

วันที่ 26 กันยายน 2026; เวลา UTC ด้านล่างเป็นเวลาไฟล์ log สิ้นสุด รอบเต็มใช้ source backend ที่ตรึง SHA-256 ไว้ และ artifacts แยกจาก smoke fixture ไม่มีการเปลี่ยน backend ระหว่างรัน ไม่ใช้จำนวนผ่านจากงานก่อนหน้า

| คำสั่ง | เวลา UTC | exit / ผล |
| --- | --- | --- |
| `npm ci` | รอบเตรียมก่อน review | 0; lockfile ติดตั้งได้ มี deprecation warning ของ ESLint 8 แต่ audit ไม่พบช่องโหว่ |
| `npm test` | 01:52:56 | 0; 43/43 |
| `npm run lint` | 01:52:56 | 0 |
| `npm run build` หลัง dev หยุด | 01:57:19 | 0 |
| `dotnet restore backend/TPR10.sln` | 01:42:00 | 0; รอบ build/test แบบ artifacts แยก restore ซ้ำตาม source จริง |
| `dotnet build backend/TPR10.sln --artifacts-path <พื้นที่เฉพาะ build>` | 01:52:59 | 0; 0 warnings / 0 errors |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | 01:51:09 | 0 |
| `dotnet test backend/TPR10.sln --artifacts-path <พื้นที่เฉพาะ full suite> --logger 'console;verbosity=normal'` | 01:51:04–02:23:35 | 0; 1,270/1,270 ไม่ล้มเหลว/ไม่ข้าม ใช้เวลา32.4713นาที |
| `npm audit` | 01:59:31 | 0; ไม่พบช่องโหว่จากแหล่งข้อมูลขณะตรวจ |
| `dotnet list backend/TPR10.sln package --vulnerable --include-transitive --no-restore` | 01:59:34 | 0; ไม่พบแพ็กเกจที่แจ้งช่องโหว่ |
| `dotnet list backend/tests/TPR10.E2E.Fixture/TPR10.E2E.Fixture.csproj package --vulnerable --include-transitive --no-restore` | 01:59:35 | 0; ไม่พบแพ็กเกจที่แจ้งช่องโหว่ |
| Linux SDK10.0.401: `dotnet test ... --filter 'FullyQualifiedName~EvidenceStampTests\|FullyQualifiedName~EvidenceImageBudgetTests'` | 01:59:39 | 0; 49/49; source/cache read-only, artifacts tmpfs, `TZ=America/Los_Angeles`, ไม่มี network |

Linux native test ปิด NuGet audit เฉพาะ container ที่ไม่มี network; audit แยกด้านบนเป็นตัวตรวจช่องโหว่ ไม่ใช้ผล restore offline แทน audit รุ่นฟอนต์และ native libraries คงเดิม

### ประวัติ HTTPS ก่อนแก้ transport (ไม่ใช่ผลล่าสุด)

ทุกชุดตรวจ CA เฉพาะ Firefox profile, cookie/CSRF, hostile Host และไม่เปิด API port สาธารณะ ไม่มี TLS bypass ไม่มี Playwright retry/skip

| คำสั่ง | เวลา UTC | exit / browser cases |
| --- | --- | --- |
| `node infra/nginx/smoke-identity-https.mjs 4000 --e2e` | 01:53:41 | 0; 13/13 |
| `node infra/nginx/smoke-identity-https.mjs 4000 --e2e --spec tests/e2e/scopes.spec.ts` | 02:24:18–02:26:12 (รอบแยก) | 1; 9/11 มี timeout ยังไม่ผ่าน |
| `node infra/nginx/smoke-attendance-directory-https.mjs 4000` | 02:27:22–02:28:06 (รอบแยก) | 0; 7/7; เก็บผล timeout รอบก่อนหน้าตามส่วนท้าย |
| `node infra/nginx/smoke-attendance-storage-https.mjs 4000` | 01:57:09 | 0; 3/3 |
| `node infra/nginx/smoke-identity-https.mjs 4001 --e2e` | 01:57:58 | 0; 13/13 |
| `node infra/nginx/smoke-identity-https.mjs 4001 --e2e --spec tests/e2e/scopes.spec.ts` | 01:58:39 | 0; 11/11 |
| `node infra/nginx/smoke-identity-https.mjs 4001 --e2e --spec tests/e2e/scopes-boundary.spec.ts` | 01:58:57 | 0; 2/2 |
| `node infra/nginx/smoke-attendance-directory-https.mjs 4001` | 01:59:28 | 0; 7/7 |
| `node infra/nginx/smoke-attendance-storage-https.mjs 4001` | 01:59:50 | 0; 3/3 |

### ภาพประทับและหลักฐานในเครื่อง

สร้างภาพสังเคราะห์ใหม่ด้วย `TPR10_STAMP_ARTIFACTS=<พื้นที่ทดสอบ> dotnet test ... --filter FullyQualifiedName~Synthetic_visual_artifacts` ผ่าน 4/4 และเปิดดู portrait thumbnail กับ landscape full จริง อ่าน `25/09/2026 00:00:00 (UTC+7)` พร้อมเข้า/ออกได้ ไม่ทับส่วนภาพ ไม่มีข้อมูลบุคคลจริง

Log/ภาพอยู่ใน `.superpowers/sdd/2026-09-25-module-6b-evidence-storage/` โดย prefix `task8-` และ `task8-final-`; source manifest คือ `task8-backend-source.sha256` ไม่ commit log หรือภาพทั้งหมด เก็บ workspace ไว้เพราะ NAS gate ยังไม่ปิด

## ผลล่าสุดหลังแก้ transport และสถานะส่งต่อ

ชุดล่าสุดรันหลังถอด instrumentation ชั่วคราวออกทั้งหมด ไม่เปลี่ยน timeout/retry/skip หรือ assertions ของ browser และไม่แก้ Next/backend business code ผลอยู่ใน `task8-transport-*.log`:

| การตรวจ | เวลา UTC วันที่26กันยายน2026 | ผล |
| --- | --- | --- |
| regression transport กับ config เดิม | 03:51:18 | RED1 ตามที่คาด: HTTP ถูกบังคับ upgrade |
| regression หลังแก้ และหลังปรับตาม review | 03:51:45 / 03:59:01 | GREEN1/1 ทั้งสองครั้ง; รอบหลังตรวจ echo WebSocket frame จริง |
| `npm test` / `npm run lint` / `dotnet format ... --verify-no-changes --no-restore` | 03:58:12–13 | exit0; Node43/43 |
| backend build / focused exit+health+bounds | รอบ03:55–03:57 | exit0; 0warnings0errors /16ผ่านไม่ข้าม |
| `npm run build` หลัง dev หยุด | 03:58:31 | exit0 |
| HTTPS dev identity / scopes / directory / storage | 03:56:04–03:58:20 | 13/13 +11/11 +7/7 +3/3 =34/34 |
| HTTPS production identity / scopes / boundary / directory / storage | 03:59–04:01 | 13/13 +11/11 +2/2 +7/7 +3/3 =36/36 |

คำสั่ง HTTPS ใช้ entrypoint/port/spec ตามตารางประวัติด้านบน; regressionเพิ่มคือ `node --test infra/nginx/test-web-transport.mjs` (ต้องมี Docker/OpenSSL และport4000ว่าง) Full backend1,270/1,270 เป็นผลรอบ02:23:35 บน source backend280ไฟล์ที่ตรวจ SHA ตรงหลังแก้ transport ไม่อ้างว่ารัน full ซ้ำในรอบ transport ส่วน focused16/16 และ build เป็นการรันใหม่

- แก้ Important backendสองข้อด้วยTDDและแก้ transportด้วยregressionจริง; ผ่านการตรวจทางเทคนิคตามขอบเขตนี้
- ผู้ใช้อนุญาต commit/push และเปิด PR สำหรับงาน6Bแล้ว การจัดส่ง Git ไม่ใช่การอนุมัติ merge หรือ Production และไม่เรียก Task8 หรือ Module6B ว่าเสร็จทั้งหมด ยังไม่ได้ deploy เข้า Preview checkout เดิม
- คืน Preview checkout เดิมแล้ว ตรวจด้วย CA เดิมโดยไม่ข้าม TLS ได้ API `ready` และ `/login`200; ไม่เปลี่ยนฐานข้อมูลเดิม และ git status ของ checkout เดิมยังเป็นหกไฟล์ที่มีอยู่ก่อนงานนี้
- NAS จริง, cross-machine restore, load และ Production sign-off ยังเป็น gate แยกที่ต้องจัดเตรียมภายหลัง ไม่มีการทดสอบถอด NAS จริงในรอบนี้

## Code Review ทั้งสาขาและผลแก้

ผู้ตรวจอิสระ Dirac ตรวจช่วง merge-base `b8b9c0f..527dda5` และไฟล์ Task 8 ที่ยังไม่ commit แบบ read-only ไม่รัน tests แทนผู้ดำเนินงาน ไม่พบ Critical; พบ Important สองข้อและ Minor สองข้อ

Important ที่แก้ด้วย RED → GREEN:

1. `StorageHealthScanner` เคยข้ามต้นทางที่ `AcceptWrites=false` ทำให้ไม่รายงานไฟล์ Active/Fallback เสียหลังเริ่มย้าย เพิ่มสองกรณีก่อน/หลัง cutover ได้ RED `MissingObjects=0` แทน 1 แล้วเปลี่ยน gate ตรวจ integrity ให้ใช้ health ที่ผ่าน probe โดยไม่เปิดสิทธิ์เขียนกลับ
2. Resume เคย track unfinished manifest ทั้งหมดภายใต้ authorization lock เพิ่มกรณี 122 items (Completed 2/ค้าง 120) ได้ RED track120เกิน100 แล้วเปลี่ยนเป็น set-based update ใน transaction เดิม ตรวจ reset ครบทุก item, fencing/version เพิ่มและ Completed ไม่เปลี่ยน

ชุด regression ของ review ผ่าน 3/3 หลังแก้ และ backend ทั้งชุดหลังแก้ผ่าน 1,270/1,270 รวมกรณีใหม่ทั้งหมด ไม่ส่ง rereview แทน TDD; HTTPS dev ที่เคยค้างแก้และยืนยันใหม่ตามตารางผลล่าสุดแล้ว

### Minor ที่เลื่อนไว้

- ป้าย readiness อาจแสดงค่าจากการโหลดก่อน TTL หมด แม้ปุ่ม/API ปฏิเสธถูกต้อง ให้ตรวจเวลาและ probe ใหม่
- Restore test ตรวจเจ้าของ/anonymous แต่ยังไม่ครอบคลุม authenticated non-owner และ HR ข้ามหน่วยงานหลัง restore โดยตรง ชุด read-policy ปกติทดสอบสิทธิ์เหล่านี้แยกอยู่แล้ว ไม่อ้างว่า restore drill พิสูจน์ matrix ทั้งหมด

### ข้อวินิจฉัยต่อรายการที่ reviewer ไม่รับรอง

| ประเด็น | ข้อวินิจฉัยและต้นทุนที่ยังเหลือ |
| --- | --- |
| camera/GPS/challenge/pairing/event FK/publisher | อยู่ 6C; 6B ยังไม่ใช่หน้าลงเวลาใช้งานจริง |
| NAS disruption/ACL/durability | คง gate รอ NAS จริง ไม่ขยาย local test ไปแทนอุปกรณ์ |
| Windows/native x64 | รอบนี้ตรวจ macOS/Linux ARM64 เท่านั้น ต้องตรวจ runtime อื่นเพิ่ม |
| multi-replica | ไม่รับรองเกิน single API process |
| throughput/slow clients/initial manifest seed | ต้องวัดบน deployment จริง การแก้ resume ลด memory ไม่รับประกัน SLA |
| cross-machine restore/mount identity/RTO/RPO | local drill คืน root/config เดิม ต้องมี DR drill เพิ่ม |
| bytes ที่ส่งไปแล้ว | เรียกคืนจากผู้รับไม่ได้; ใช้ reauthorization และ bounded delivery ก่อน/ระหว่างส่ง |
| OS/service identity/DBA ระดับสูง | อยู่นอก application authorization boundary ต้องควบคุมสิทธิ์ปฏิบัติการ |
| scheduled orphan reconciliation/cleanup | มี report-only ไม่อ้าง scheduled cleanup และไม่ลบอัตโนมัติ |
| legacy Prepared backfill | ไม่มี production writer เดิมในขอบเขตนี้ หากมีข้อมูลจริงต่างจากสมมติฐานต้องจัดแผน migration ใหม่ |
| dependency/license certification | ผู้ดำเนินงานรัน audit ล่าสุด; ไม่อ้างการรับรองกฎหมายหรือความปลอดภัยถาวร |
| final tests/edits หลัง review | ผู้ดำเนินงานรับผิดชอบ fresh gates และบันทึกทุก failure; review ไม่ใช่ผลทดสอบ |

## ผลระหว่างรันที่ไม่ผ่านและข้อจำกัด

### ตรวจสาเหตุ dev timeout ต่อหลังผู้ใช้สั่งดำเนินการ

เก็บเฉพาะ path ของ static assets, เวลา, status และจำนวน bytes จาก nginx/Next โดยไม่เก็บ query, headers ของบัญชี, cookie หรือ body:

- `task8-asset-diagnostic.log`: scopes10/11; webpack.js ค้าง upstream27.141วินาที/0bytes แล้ว499เมื่อ browser ยกเลิก
- `task8-next-asset-diagnostic.log`: scopes11/11 แต่ยังไม่ถือว่าแก้ เพราะยังไม่ได้เปลี่ยน config
- `task8-next-asset-diagnostic-2.log`: scopes11/11 แต่มี CSS upstream19.399และ7.174วินาที ขณะที่ Next รับและตอบในไม่กี่มิลลิวินาที
- `task8-connect-diagnostic.log`: scopes9/11; CSS ค้าง5.459และ28.220วินาที โดย `upstream_connect_time=-`, `upstream_header_time=-`, upstream0bytes ยืนยันว่าค้างก่อนเชื่อมต่อถึง Next ไม่ใช่ MFA ปฏิเสธหรือการ compile ของ request นั้น

ขอบเขตการวินิจฉัย: พบปัญหาที่ช่วงเปิด TCP จาก nginx container ไป host ในเครื่องทดสอบ; ยังไม่ระบุสาเหตุภายใน Docker Desktop/kernel config เดิมไม่ใช้ upstream pool และส่ง `Connection: upgrade` ทุก HTTP request จึงมี connection churn สูง แก้เฉพาะ HTTPS local template ให้ pool16 และส่ง Upgrade เฉพาะคำขอที่มี Upgrade ไม่เปลี่ยน auth/CSRF/TLS/timeout หรือ assertions

Regression ใหม่ `node --test infra/nginx/test-web-transport.mjs` ใช้ rendered nginx จริง, CA เฉพาะ client, synthetic HTTP upstream ตรวจ HTTP ไม่ถูกบังคับ upgrade, socket reuse5คำขอ และ WebSocket เห็น RED กับ config เดิมแล้ว GREEN หลังแก้ ไม่ใช้การ grep template แทนพฤติกรรม transport รอบ diagnostic หลังแก้ scopes11/11 ผ่าน มี469assetsและเวลาสูงสุด0.154วินาที; matrixหลังถอดdiagnosticผ่าน70/70ตามตารางผลล่าสุด ไม่รับประกันว่าเครือข่ายจะไม่มีเหตุขัดข้องใดในอนาคต

### Review เฉพาะ transport และการปรับชุดทดสอบ

ผู้ตรวจอิสระ Anscombe ตรวจสามไฟล์ transport/config/test และบันทึกวินิจฉัยแบบ read-only ไม่พบ Critical/Important พบ Minor สองข้อที่แก้แล้ว:

1. เดิมตรวจเพียง101แล้วปิด socket จึงเพิ่ม WebSocket handshake ที่ถูกต้องและ echo frame ไป–กลับจริงผ่าน TLS/nginx โดยใช้ implementation ที่มากับ Next รุ่นที่ตรึงไว้ ไม่เพิ่ม dependency
2. readiness เดิมจับทุก error จึงเปลี่ยนให้ retry เฉพาะ `ECONNREFUSED` ขณะ nginx เริ่มทำงาน; status/body/TLS/assertion ผิดจะล้มทันที ไม่ retry measured requests

ชุด transport หลังแก้ข้อย่อยผ่าน1/1 ไม่มี skip; Node43/43, Lint, backend Build0warnings0errors, Format และ focused backend16/16 ผ่านใหม่ Source backend280ไฟล์ตรง SHA เดิม จึงแยก provenance ของ full1,270/1,270 รอบ02:23:35UTC ออกจาก focused รอบใหม่นี้ ไม่อ้างว่า rerun full ทั้งชุด

ข้อวินิจฉัยต่อรายการที่ผู้ตรวจไม่รับรอง: (1) ผู้ดำเนินงานเป็นผู้ยืนยัน RED/GREEN และ matrix จริง ไม่ใช้ review แทน test; (2) echo frame พิสูจน์ bidirectional tunnel แต่ไม่ใช่การแก้ source ระหว่าง session เพื่อพิสูจน์ Next HMR เต็มวงจร; (3) ไม่รับรอง Docker/kernel internals หรือ production throughput; (4) backend และ Task8 ส่วนเดิมใช้ whole-branch review ก่อนหน้าและผลทดสอบแยก ไม่มีการยกเว้น gate เงียบ ๆ

- Browser dev รอบแรก: scopes 10/11 (timeout กรณี read ค้างหลัง logout อีกหน้าต่าง), directory 6/7 (ไม่พบ Portal heading ภายใน 5 วินาที), storage 2/3 (admin case timeout30วินาที) ยังไม่ยืนยัน root cause จึงไม่อ้างว่าแก้ dev timeout แล้ว ไม่เพิ่ม timeout ไม่ skip และไม่เปลี่ยน assertions เพื่อผ่าน
- รอบ production-build แรก: identity13/scopes11/boundary2/directory7/storage3 ผ่าน แต่มีบางชุดรันก่อนแก้ review จึงรัน matrix ใหม่หลังตรึง source
- รอบแยก dev scopes หลัง backend จบ (ไม่มีชุดทดสอบ/build อื่นพร้อมกัน) ยังผ่าน9/11 ล้มสองกรณี: `scopes.spec.ts:97` กลับเข้าแท็บหลังเปลี่ยนบัญชี ค้างใน login helper ที่ `waitForLoadState('load')`; และ `scopes.spec.ts:129` read ค้างหลัง logout อีกหน้าต่าง ค้างก่อนเริ่ม MFA เพราะปุ่มยัง disabled ทั้งสองรายงานรอ `/_next/static/chunks/app/layout.js`, document=`interactive` ไม่ใช่ assertion ว่าข้อมูลรั่ว จึงไม่สรุปว่ามี privacy leak หรือโยนสาเหตุให้ภาระ full suite แต่ยังถือว่า gate ไม่ผ่าน
- อาการ dev pending JavaScript เคยบันทึกใน Module2/3 แต่ผลเก่าไม่พิสูจน์ root cause ของรอบนี้ การแก้ต่อควรเก็บ timing/status ของ Next → nginx → Firefox สำหรับ asset ที่ค้าง โดยไม่บันทึก cookies/body/private DOM แล้วสร้าง regression ก่อนแก้สาเหตุ ไม่เพิ่ม timeout หรือ rerun จนเลือกเฉพาะผลผ่าน
- Metadata ใบรับรอง Preview แสดง `notAfter=2026-09-25T17:06:34Z` แต่การตรวจจริงหลังคืนบริการด้วย `curl -q --cacert ...` โดยไม่ข้าม TLS ได้ `/login` 200 และ API ready/live ข้อมูลเวลาจึงไม่สอดคล้องกัน ไม่สรุปว่า TLS ใช้ไม่ได้จากวันที่เพียงอย่างเดียว ไม่เปลี่ยน CA/trust store; ผล acceptance ใช้ CA ชั่วคราวของ fixture แยกต่างหาก
