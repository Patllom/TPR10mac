# ผลตรวจส่งมอบ Module 7D — Task18

สถานะ: **Task18 ผ่านการตรวจรับตามขอบเขตแล้ว** ผู้ตรวจอิสระยืนยันแก้ Important ครบ Test backend 3578/3578, frontend261/261, Build, Lint/Format และ diff check ผ่านครบ ไม่มีการ push หรือ merge

ฐาน Task1–17: `acd8c518300c5bf54f3419f4a31667053765ba1f` ใน worktree `module-7a-policy-permissions/TPR10` ผู้ลงมือ Bohr เพิ่ม risk coverage; Erdos ตรวจเฉพาะ Task18; Kuhn ตรวจทั้งโมดูลและส่วนแก้; Gauss ลงมือแก้ตามข้อพบ โดย controller ตรวจหลักฐานและรันชุดตรวจสุดท้าย

ผู้ตรวจทั้งโมดูลพบ Important 6 จุดและยืนยันว่าแก้แล้วทั้งหมด:

1. W-I1: worker รับมือข้อผิดพลาดเชื่อมต่อฐานข้อมูลแบบที่ EF ห่อไว้ทั้งขั้นอ่านและปิดคำขอ โดยไม่กลืนข้อผิดพลาดโปรแกรมทั่วไป
2. W-I2: ส่งข้อมูลเอกสารซ้ำด้วยลำดับล็อก registry → identity สอดคล้องกับคำสั่งเขียน รวมคิว เปรียบเทียบ และกู้ receipt
3. W-I3: เมื่อรับรองข้อเสนอวันจบงานใหม่ ยุติข้อเสนอค้างที่ใช้ฐานเดิมพร้อมเก็บประวัติและเพิ่ม version; ข้อเสนอเก่าที่หมดฐานมีทางยุติผ่านผู้ตรวจที่มีสิทธิ์
4. W-I4: ผู้อนุมัติทดแทนค้นพบ อ่านหลักฐาน และลงผลในงานที่มีสิทธิ์ปัจจุบันได้ โดยไม่เพิ่มสิทธิ์รายงานทั่วไป และยังเพิกถอน prepared read เมื่อเปลี่ยนการมอบหมายอย่างชัดแจ้ง
5. W-I5: หน้าเอกสารระบุชนิดหลักฐานทางธุรกิจได้ รวมแก้เอกสารเดิมที่ไม่มีชนิดและกู้ผลคำสั่ง update ไม่อนุมานชนิดจาก MIME
6. W-I6: ผู้ตรวจการเงินอัปโหลดหลักฐานใหม่สำหรับแก้เงินคืนที่รับรองแล้วได้ทั้ง Approved/Closed ตามเงื่อนไขและสิทธิ์เดิม

ผู้ตรวจพบ F1-I1 เพิ่มจากชุดแก้: หลังบันทึกเอกสารสำเร็จแต่ response หาย การกู้ receipt ยังเหลือ version เก่าในฟอร์ม แก้โดยใช้การล้างฟอร์มเมื่อยืนยันสำเร็จร่วมกับทางบันทึกปกติ เทสต์เบราว์เซอร์พิสูจน์ commit → response หาย → receipt ตรงคำสั่ง → เปิดเอกสารปัจจุบันและแก้ครั้งใหม่ได้ ผู้ตรวจยืนยันแก้แล้ว ไม่ล้างคำสั่งที่ผลยังไม่ทราบเพียงเพราะได้ 409/404

การตรวจรับชุดแก้สุดท้ายไม่พบ Critical/Important ใหม่ในขอบเขตที่ตรวจ ไม่ใช่การรับรองว่าไม่มีข้อบกพร่องทุกกรณี หลักฐานรายละเอียดอยู่ใน task-18-whole-review-report.md, task-18-fix1-review-report.md และ task-18-residual-review-report.md ภายใต้ `.superpowers/sdd/2026-10-31-module-7d-settlement-refunds/`

มือถือจริงและ NAS จริงยังพักไว้ รายการติดตามเดิมยังไม่ปิดจากผล smoke ที่ผ่าน

## หลักฐานหลังแก้และชุดตรวจสุดท้าย

- ชุดแก้หลัก: backend เฉพาะจุด 102/102 และ subset วันจบงาน 29/29 ซึ่งทับซ้อนกัน ไม่บวกเป็นจำนวน unique; frontend 83/83; HTTPS employee1+reviewer7 บน dev/prod รวม16 ผ่าน
- ชุดแก้ F1-I1: frontend 83/83; employee HTTPS dev/prod ผ่าน โดยมี RED ล้มตรงฟอร์มไม่ถูกล้างหลัง receipt200 ก่อนแก้
- แก้ Format หนึ่งตำแหน่งในเทสต์ด้วย whitespace เท่านั้น ผู้ตรวจยืนยัน tokens เดิม รอบ controller แรกหยุดที่ Format exit2 ก่อน Build/Test; เก็บรอบที่ล้มไว้ ไม่ถือว่าผ่าน
- ชุดตรวจสุดท้ายตรึง source/config/test 829 paths และคงงานเดิม 14 paths ตาม hash ใช้ Node24/.NET10 ไม่มีการแก้ source ระหว่างรัน; ตรวจหลังจบแล้ว source ทั้ง829 pathsตรงเดิมและ prior14ยังครบ
- ผลชุดใหญ่สุดท้าย: frontend Test261/261, backend Test3578/3578, Build/Lint/Format/diff check ผ่านครบ

หลักฐานในเครื่อง: `/tmp/7d-t18-final-*-summary.jsonl`, `/tmp/7d-t18-final-source.json`, `/tmp/7d-t18-fix1-evidence/commands.json` และ `/tmp/7d-t18-residual-evidence/commands.json` ชุดรายงานหลังแก้เปิดเผย source ที่เปลี่ยนระหว่างบางรันก่อนตรึง final source; ไม่อ้างว่า 102 เทสต์รันบน binaryสุดท้ายชุดเดียว ชุดย่อย29 ใช้ sourceสุดท้ายสำหรับ W-I3 และชุดใหญ่สุดท้ายเป็นหลักฐานรวมเมื่อจบแล้ว

ส่วนถัดไปเป็นหลักฐาน risk coverage ก่อนแก้ข้อพบจากการตรวจทั้งโมดูล คงไว้เพื่อแสดงที่มา ไม่ใช้แทนผลหลังแก้:

## หลักฐานใหม่และขอบเขต

- COMMIT failure ใช้ deferred PostgreSQL constraint trigger ในฐานจำลองหลังการบันทึกจริง ตรวจ snapshot เงิน/การจัดสรร/receipt/audit/closure ไม่เปลี่ยน แล้วคำสั่งเดิมสำเร็จครั้งเดียว ทดสอบ COMMIT ของ closure แยกตามธุรกรรมจริง ไม่อ้างว่า approval กับ closure เป็น transaction เดียวกัน
- Race คืนเงินกับรับรองค่าใช้จ่ายใช้สอง connection และ barrier ตรวจ PID ผู้ถือ lock อยู่ใน `pg_blocking_pids` ของผู้รอจริง ทดสอบทั้งสองลำดับ รวมลองใหม่ด้วย version สดและยังถูกจำกัดด้วยยอด
- T11-M1: refund correction ↔ incident recovery เสนอหลักฐานซ้ำทั้งสองลำดับ รวม evidence ID เดิมและคนละ ID ที่ hash เท่ากัน ต้องปฏิเสธด้วย duplicate-proof code และไม่มีผลเงิน/receipt ใหม่ ขอบเขตใหม่เป็น pending proposals ไม่อ้างว่าครบทุก accepted/legacy mutation matrix
- T4-M1: ทดสอบ unique pair/version โดย command ต่างกัน จากนั้นมีหลายคู่ต่อ command แล้วรัน Down ของ schema Task4 จริง ต้อง 23505 ที่ unique command index และคงข้อมูล/index/history พร้อมยืนยันเวอร์ชันใหม่ยังเพิ่มได้ ไม่ใช่ผ่านจาก guard ของ migration รุ่นหลัง
- T17-M1: positive PNG bytes ผ่าน work-proof route จริง พร้อม no-store; ถอนสิทธิ์ระหว่าง I/O และหลังเตรียม response ก่อนส่งต้องไม่ส่งภาพ สิทธิ์เจ้าของ/admin ไม่แทนสิทธิ์ผู้ตรวจ และ evidence ที่ไม่ผูก entity ต้องถูกปฏิเสธ ขอบเขตนี้ใช้ lane refunds ไม่อ้างว่าครบทุก lane
- Lost response จำลองการไม่รับผลหลัง service commit แล้วเรียก command เดิม ตรวจเท่ากับ receipt ที่เก็บและไม่มีผลเงินเพิ่ม พร้อมปฏิเสธหลังถอนสิทธิ์ มี mapped HTTP guard regression ประกอบ ไม่อ้างว่าได้ตัดสาย TCP จริง
- 7C clone ใช้ pg_dump/pg_restore ใน container แล้ว migrate สองครั้ง/idempotent SQL สองครั้ง ตรวจยอดเดิม 10,000/จ่าย 2,500/กัน 500, grants เดิม, ตารางใหม่ว่าง และไม่มีวันแก้ไขสมมติ
- Backup/restore เก็บ pending refund, Closed/ClosureRecord, adjustment case และ revoked rights; local evidence move ตรวจ digest/length, current location, ประวัติและการอ้างอิงคืนเงิน แล้วไฟล์ปลายทางเสียต้องถูกปฏิเสธ ไม่ใช่ NAS หรือการกู้คืน production

เทสต์ใหม่เป็น risk-coverage GREEN เมื่อโค้ดเดิมผ่าน ไม่สร้าง failure เทียม Compile/analyzer/FK/identity/historical-schema fixture failures เก็บไว้ตามจริง ไม่ใช่ product RED และไม่มีการแก้ production เพื่อให้เทสต์ผ่าน

## ความต่างของ source และ HTTPS

ใช้ Node 24.22.1 / .NET 10 จาก runtime ที่ brief ระบุ เปิด test seed เฉพาะ `TPR10_PAYMENT_E2E=1` และ diagnostics ไม่มีการปิด TLS เพิ่ม timeout/retry หรือเปลี่ยน production defaults ตัว runner เป็นเจ้าของ web/container ของตน; หลัง dev ต้องมี build ใหม่ก่อน production preview

HTTPS ใช้ worktree ที่ dirty: `infra/nginx/https-smoke-fixture.mjs` มี 46 insertions/12 deletions เดิม เพิ่ม import/session สำหรับ HTTPS504, ตรวจ interruption, เส้นทาง owned web/browser process, diagnostic capture และ cleanup; พร้อมไฟล์ HTTPS504 เดิมและ request E2E ที่ dirty (5 insertions/1 deletion) คง prior14 byte-for-byte ไม่อ้าง clean committed-tree acceptance และไม่ถือว่าปิด HTTPS504; diff ตรงของ fixture/request เก็บใน `/tmp/7d-t18-evidence/dirty-https-inputs.patch` (SHA-256 `9b9fe265da415b6f4c098f492c66698d06abafda67f9d98372d9dc0102dfb239`)

ระหว่าง HTTPS มีการแก้/format เทสต์และเขียนเอกสาร จึงไม่อ้าง entire-tree hash คงเดิม ผล HTTPS จะใช้ได้เฉพาะเมื่อ runtime source, build/config/dependencies lock และ E2E/fixture inputs ที่ใช้ยังคงเดิม รายการ delta และหลักฐาน runtime แยกจาก test/docs อยู่ในตารางหลักฐานท้ายรายงานและรายงานผู้ลงมือ

## ประเด็นที่ยังเปิด

controller ดูแล wholebranch review boundary/followup inventory แยกจากงานนี้ เก็บ M2-R1/R2-C1, HTTPS 504, Minor log, OpenAPI 500, T13-M1/T14-M1, T12-M1/T13-REVIEW-M1/T14-REVIEW-M2/T15-REVIEW-M1, T15-REVIEW-M2, T16-M1/T16-M2, T17-M2 ไว้ ส่วน worker EF-wrapper ได้แก้ตาม W-I1 และผ่านการตรวจเฉพาะกรณีแล้ว ยังไม่รับรอง outage/failover ใน production ไม่ปิดรายการอื่นจากเทสต์ชื่อคล้ายกัน

T4-M1/T11-M1/T17-M1 ผ่านผู้ตรวจอิสระแล้วสำหรับช่องว่างที่ระบุข้างต้นเท่านั้น ไม่ขยายเป็นการรับรองทุกกรณีของ migration, หลักฐานซ้ำ หรือการส่งไฟล์

พักมือถือจริง/NAS จริง ไม่มีเงินจริง deploy/migrate ฐานจริง push/merge การซ้อมไม่ได้รับรองโหลดจริง disaster recovery ทั้งระบบ หรือ RPO/RTO หลักฐานใน `/tmp/7d-t18-evidence` และ scratch report ต้องเก็บก่อนล้างเครื่อง รายงานนี้ไม่เพิ่มสิทธิ์ให้เปลี่ยนนโยบายเมื่อข้อมูลไม่ครบ

คู่มือปฏิบัติงาน: [เคลียร์และคืนเงิน](../runbooks/field-advance-settlements.md)

<!-- task18-evidence:start -->
## ผลสุดท้ายเฉพาะขอบเขตผู้ลงมือ

ผ่าน focused ใหม่ 15 เคส (race 3 + delivery 12) และ mapped regression ที่รันจาก source เดิมจริง 63 เคส ไม่มี skipped ในผลสรุปสุดท้าย ไม่ใช่ fullG ทั้ง branch; format verify เฉพาะไฟล์ใหม่ผ่าน ไม่มี product RED/production edits

HTTPS settlement dev 1+7 และ production 1+7 เคส; regression identity/scopes/scopes-boundary/7A policy/grant/7B request/7C payment/supplement เป็น **production 4001 เท่านั้น** ผลเหล่านี้ไม่อ้างว่า regression ทุก spec ผ่าน dev ด้วย Build หลัง dev ผ่านก่อน production และไม่มี dev/build ใหม่คั่นใน production matrix

### ตารางคำสั่งและเวลา UTC

| ชุด/คำสั่ง | ผ่าน | เริ่ม → จบ UTC | Exit | Log/manifest |
|---|---:|---|---:|---|
| `dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~FieldAdvanceSettlementRaceTests' --logger 'console;verbosity=normal'` | 3 | 2026-11-03T17:00:31.461619+00:00 → 2026-11-03T17:00:53.144041+00:00 | 0 | [20261103T170031460601Z-final-race.log](/tmp/7d-t18-evidence/20261103T170031460601Z-final-race.log); [20261103T170031460601Z-final-race-before.sha256.json](/tmp/7d-t18-evidence/20261103T170031460601Z-final-race-before.sha256.json) |
| `dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~FieldAdvanceSettlementDeliveryTests' --logger 'console;verbosity=normal'` | 12 | 2026-11-03T17:06:50.180622+00:00 → 2026-11-03T17:07:40.491230+00:00 | 0 | [20261103T170650179798Z-final-delivery-pending-digest.log](/tmp/7d-t18-evidence/20261103T170650179798Z-final-delivery-pending-digest.log); [20261103T170650179798Z-final-delivery-pending-digest-before.sha256.json](/tmp/7d-t18-evidence/20261103T170650179798Z-final-delivery-pending-digest-before.sha256.json) |
| `dotnet test backend/TPR10.sln --filter <mapped-regression-filter> --logger "console;verbosity=normal"` | 50 | 2026-11-03T17:03:57.994583+00:00 → 2026-11-03T17:06:02.800224+00:00 | 0 | [20261103T170357993780Z-mapped-regression.log](/tmp/7d-t18-evidence/20261103T170357993780Z-mapped-regression.log); [20261103T170357993780Z-mapped-regression-before.sha256.json](/tmp/7d-t18-evidence/20261103T170357993780Z-mapped-regression-before.sha256.json) |
| `dotnet test backend/TPR10.sln --filter <mapped-additional-filter> --logger "console;verbosity=normal"` | 13 | 2026-11-03T17:07:40.583926+00:00 → 2026-11-03T17:08:11.346489+00:00 | 0 | [20261103T170740583129Z-mapped-additional.log](/tmp/7d-t18-evidence/20261103T170740583129Z-mapped-additional.log); [20261103T170740583129Z-mapped-additional-before.sha256.json](/tmp/7d-t18-evidence/20261103T170740583129Z-mapped-additional-before.sha256.json) |
| `dotnet format backend/TPR10.sln --no-restore --verify-no-changes --include backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceSettlementRaceTests.cs backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceSettlementDeliveryTests.cs backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceSettlementDeliverySupport.cs` | check | 2026-11-03T17:06:36.407467+00:00 → 2026-11-03T17:06:50.085216+00:00 | 0 | [20261103T170636406580Z-final-scoped-format-check.log](/tmp/7d-t18-evidence/20261103T170636406580Z-final-scoped-format-check.log); [20261103T170636406580Z-final-scoped-format-check-before.sha256.json](/tmp/7d-t18-evidence/20261103T170636406580Z-final-scoped-format-check-before.sha256.json) |
| `https-4000-field-advance-settlement (`runHttpsSmoke`, e2e=true)` | 1 | 2026-11-03T16:55:02.985721+00:00 → 2026-11-03T16:55:56.744444+00:00 | 0 | [20261103T165502984788Z-https-4000-field-advance-settlement.log](/tmp/7d-t18-evidence/20261103T165502984788Z-https-4000-field-advance-settlement.log); [20261103T165502984788Z-https-4000-field-advance-settlement-before.sha256.json](/tmp/7d-t18-evidence/20261103T165502984788Z-https-4000-field-advance-settlement-before.sha256.json) |
| `https-4000-field-advance-settlement-review (`runHttpsSmoke`, e2e=true)` | 7 | 2026-11-03T16:55:56.847345+00:00 → 2026-11-03T16:56:41.727220+00:00 | 0 | [20261103T165556846550Z-https-4000-field-advance-settlement-review.log](/tmp/7d-t18-evidence/20261103T165556846550Z-https-4000-field-advance-settlement-review.log); [20261103T165556846550Z-https-4000-field-advance-settlement-review-before.sha256.json](/tmp/7d-t18-evidence/20261103T165556846550Z-https-4000-field-advance-settlement-review-before.sha256.json) |
| `npm run build` | build | 2026-11-03T16:56:41.836008+00:00 → 2026-11-03T16:56:51.410008+00:00 | 0 | [20261103T165641835206Z-https-build-after-dev.log](/tmp/7d-t18-evidence/20261103T165641835206Z-https-build-after-dev.log); [20261103T165641835206Z-https-build-after-dev-before.sha256.json](/tmp/7d-t18-evidence/20261103T165641835206Z-https-build-after-dev-before.sha256.json) |
| `https-4001-field-advance-settlement (`runHttpsSmoke`, e2e=true)` | 1 | 2026-11-03T16:56:51.502763+00:00 → 2026-11-03T16:57:36.092479+00:00 | 0 | [20261103T165651501960Z-https-4001-field-advance-settlement.log](/tmp/7d-t18-evidence/20261103T165651501960Z-https-4001-field-advance-settlement.log); [20261103T165651501960Z-https-4001-field-advance-settlement-before.sha256.json](/tmp/7d-t18-evidence/20261103T165651501960Z-https-4001-field-advance-settlement-before.sha256.json) |
| `https-4001-field-advance-settlement-review (`runHttpsSmoke`, e2e=true)` | 7 | 2026-11-03T16:57:36.194427+00:00 → 2026-11-03T16:58:14.290860+00:00 | 0 | [20261103T165736193622Z-https-4001-field-advance-settlement-review.log](/tmp/7d-t18-evidence/20261103T165736193622Z-https-4001-field-advance-settlement-review.log); [20261103T165736193622Z-https-4001-field-advance-settlement-review-before.sha256.json](/tmp/7d-t18-evidence/20261103T165736193622Z-https-4001-field-advance-settlement-review-before.sha256.json) |
| `https-4001-identity (`runHttpsSmoke`, e2e=true)` | 13 | 2026-11-03T16:58:14.394443+00:00 → 2026-11-03T16:59:02.307916+00:00 | 0 | [20261103T165814393629Z-https-4001-identity.log](/tmp/7d-t18-evidence/20261103T165814393629Z-https-4001-identity.log); [20261103T165814393629Z-https-4001-identity-before.sha256.json](/tmp/7d-t18-evidence/20261103T165814393629Z-https-4001-identity-before.sha256.json) |
| `https-4001-scopes (`runHttpsSmoke`, e2e=true)` | 11 | 2026-11-03T16:59:02.445300+00:00 → 2026-11-03T16:59:52.457085+00:00 | 0 | [20261103T165902442999Z-https-4001-scopes.log](/tmp/7d-t18-evidence/20261103T165902442999Z-https-4001-scopes.log); [20261103T165902442999Z-https-4001-scopes-before.sha256.json](/tmp/7d-t18-evidence/20261103T165902442999Z-https-4001-scopes-before.sha256.json) |
| `https-4001-scopes-boundary (`runHttpsSmoke`, e2e=true)` | 2 | 2026-11-03T16:59:52.558865+00:00 → 2026-11-03T17:00:14.869704+00:00 | 0 | [20261103T165952558084Z-https-4001-scopes-boundary.log](/tmp/7d-t18-evidence/20261103T165952558084Z-https-4001-scopes-boundary.log); [20261103T165952558084Z-https-4001-scopes-boundary-before.sha256.json](/tmp/7d-t18-evidence/20261103T165952558084Z-https-4001-scopes-boundary-before.sha256.json) |
| `https-4001-field-advance-policy (`runHttpsSmoke`, e2e=true)` | 13 | 2026-11-03T17:00:14.971588+00:00 → 2026-11-03T17:02:11.931913+00:00 | 0 | [20261103T170014970801Z-https-4001-field-advance-policy.log](/tmp/7d-t18-evidence/20261103T170014970801Z-https-4001-field-advance-policy.log); [20261103T170014970801Z-https-4001-field-advance-policy-before.sha256.json](/tmp/7d-t18-evidence/20261103T170014970801Z-https-4001-field-advance-policy-before.sha256.json) |
| `https-4001-field-advance-grant (`runHttpsSmoke`, e2e=true)` | 11 | 2026-11-03T17:02:12.035473+00:00 → 2026-11-03T17:05:13.100468+00:00 | 0 | [20261103T170212034681Z-https-4001-field-advance-grant.log](/tmp/7d-t18-evidence/20261103T170212034681Z-https-4001-field-advance-grant.log); [20261103T170212034681Z-https-4001-field-advance-grant-before.sha256.json](/tmp/7d-t18-evidence/20261103T170212034681Z-https-4001-field-advance-grant-before.sha256.json) |
| `https-4001-field-advance-request (`runHttpsSmoke`, e2e=true)` | 11 | 2026-11-03T17:05:13.200343+00:00 → 2026-11-03T17:09:19.996137+00:00 | 0 | [20261103T170513199487Z-https-4001-field-advance-request.log](/tmp/7d-t18-evidence/20261103T170513199487Z-https-4001-field-advance-request.log); [20261103T170513199487Z-https-4001-field-advance-request-before.sha256.json](/tmp/7d-t18-evidence/20261103T170513199487Z-https-4001-field-advance-request-before.sha256.json) |
| `https-4001-field-advance-payment (`runHttpsSmoke`, e2e=true)` | 1 | 2026-11-03T17:09:20.095043+00:00 → 2026-11-03T17:10:16.572103+00:00 | 0 | [20261103T170920094264Z-https-4001-field-advance-payment.log](/tmp/7d-t18-evidence/20261103T170920094264Z-https-4001-field-advance-payment.log); [20261103T170920094264Z-https-4001-field-advance-payment-before.sha256.json](/tmp/7d-t18-evidence/20261103T170920094264Z-https-4001-field-advance-payment-before.sha256.json) |
| `https-4001-field-advance-supplement (`runHttpsSmoke`, e2e=true)` | 1 | 2026-11-03T17:10:16.670628+00:00 → 2026-11-03T17:11:23.767654+00:00 | 0 | [20261103T171016669850Z-https-4001-field-advance-supplement.log](/tmp/7d-t18-evidence/20261103T171016669850Z-https-4001-field-advance-supplement.log); [20261103T171016669850Z-https-4001-field-advance-supplement-before.sha256.json](/tmp/7d-t18-evidence/20261103T171016669850Z-https-4001-field-advance-supplement-before.sha256.json) |

คำสั่งเต็มทุก argv/start/end/exit อยู่ใน `.result.json` คู่กับ log; common env = PATH runtime Node24/.NET10 ตาม brief, DOTNET_ROOT; HTTPS เพิ่ม TPR10_PAYMENT_E2E=1 และ TPR10_SMOKE_DIAGNOSTICS=1. Filters ที่ใช้จริง: [mapped-regression-filter.txt](/tmp/7d-t18-evidence/mapped-regression-filter.txt) และ [mapped-additional-filter.txt](/tmp/7d-t18-evidence/mapped-additional-filter.txt)

### Mapping D01–D18

ตารางนี้อ้างเทสต์ที่ตรวจ body และรันจริง ไม่ใช้ชื่ออย่างเดียวเป็นการรับรอง ข้อมูลเต็มแต่ละ testcase → command/start/end/exit/log/source/full SHA-256: [d01-d18-evidence.json](/tmp/7d-t18-evidence/d01-d18-evidence.json). F1=final-race, F2=final-delivery-pending-digest, R1=mapped-regression, R2=mapped-additional ตามตารางคำสั่งข้างต้น D18 ผ่านการตรวจอิสระแล้ว ส่วนชุดตรวจรวมของ controller ดูสถานะล่าสุดต้นรายงาน

| รหัส | testcase ที่ผ่านจริง / ชุด / SHA-256 source (12 ตัวแรก) |
|---|---|
| D01 | `FieldAdvanceSettlementClosureTests.Authorized_family_summary_never_includes_hidden_money_and_rechecks_revocation` — R1 / `b3b7af1e0625` |
| D02 | `FieldAdvanceSettlementReviewTests.Returned_round_requires_owner_and_all_new_decisions` — R1 / `b92dafffb9e0`<br>`FieldAdvanceSettlementReviewTests.Owner_confirmation_then_three_distinct_decisions_consumes_once_and_replays` — R1 / `b92dafffb9e0`<br>`FieldAdvanceSettlementReviewTests.Final_budget_failure_preserves_reserved_document_and_accepted_prior_round` — R2 / `b92dafffb9e0` |
| D03 | `FieldAdvanceExpenseAllocationTests.Concurrent_connections_wait_then_enforce_budget_with_fresh_request_versions` — R1 / `c61073fcb348`<br>`FieldAdvanceExpenseAllocationTests.Pending_and_returned_rounds_share_one_document_budget` — R1 / `c61073fcb348` |
| D04 | `FieldAdvanceExpenseDocumentTests.Duplicate_review_requires_both_scopes_and_SoD_and_metadata_change_invalidates_review` — R1 / `6de8c69cf412`<br>`FieldAdvanceExpenseDocumentTests.Other_project_authority_and_family_history_are_required_before_review_and_replay` — R1 / `6de8c69cf412`<br>`FieldAdvanceExpenseDocumentTests.Hash_or_metadata_match_blocks_both_sides_and_one_command_can_create_multiple_cases` — R1 / `6de8c69cf412` |
| D05 | `FieldAdvanceSettlementMathTests` — R1 / `2868081385a3`<br>`FieldAdvanceSettlementDraftTests.Draft_uses_approved_snapshot_rounds_each_line_and_does_not_reserve_or_accept` — R1 / `4a0da821213f`<br>`FieldAdvanceSettlementDraftTests.Latest_working_request_policy_cannot_override_approved_account_snapshot` — R2 / `4a0da821213f`<br>`FieldAdvanceSupplementTests.Returned_supplement_requotes_current_policy_and_keeps_old_snapshot` — R2 / `7bc8f2d7e7bd` |
| D06 | `FieldAdvanceSettlementDeadlineTests.Calendar_deadline_and_return_window_keep_original_submission` — R1 / `00d7a5a54abc`<br>`FieldAdvanceSettlementDeadlineTests.Correction_has_no_effect_until_separate_checker_accepts_and_preserves_old_lateness` — R1 / `00d7a5a54abc` |
| D07 | `FieldAdvanceSettlementRaceTests.Refund_and_final_expense_wait_on_postgres_and_recheck_capacity` — F1 / `d254ae05d7bc`<br>`FieldAdvanceRefundCorrectionTests.Competing_correction_confirmations_apply_one_delta_under_family_lock` — R1 / `796424f974f8` |
| D08 | `FieldAdvanceRefundCorrectionTests.Closed_request_correction_uses_latest_net_version_once` — R1 / `796424f974f8`<br>`FieldAdvanceRefundCorrectionTests.Case_refund_requires_owner_and_checker_then_settles_only_its_case` — R1 / `796424f974f8` |
| D09 | `FieldAdvancePaymentIncidentTests.Before_and_after_paid_have_equal_actual_owner_liability` — R1 / `0877fccdf53e`<br>`FieldAdvancePaymentIncidentTests.Recovery_is_separate_proposal_until_checker_confirmation_and_never_restores_capacity` — R1 / `0877fccdf53e` |
| D10 | `FieldAdvanceIncidentPaymentBridgeTests.Recovery_does_not_restore_capacity_or_reuse_old_review` — R1 / `0e65d0a0c0c6` |
| D11 | `FieldAdvanceIncidentPaymentBridgeTests.Recovery_does_not_restore_capacity_or_reuse_old_review` — R1 / `0e65d0a0c0c6`<br>`FieldAdvancePaymentIncidentTests.Recovery_is_separate_proposal_until_checker_confirmation_and_never_restores_capacity` — R1 / `0877fccdf53e` |
| D12 | `FieldAdvanceSupplementClosureTests.Existing_returned_supplement_continues_but_new_creation_is_denied` — R1 / `a60d4e94906a`<br>`FieldAdvanceSupplementClosureTests.Create_and_close_follow_committed_lock_order` — R1 / `a60d4e94906a` |
| D13 | `FieldAdvanceExpenseAdjustmentTests.Excess_invoice_12000_cap_10000_tracks_2000_without_changing_expense_or_draft` — R1 / `de3509c41c24`<br>`FieldAdvanceExpenseAdjustmentTests.Excess_disposition_requires_checker_evidence_and_real_linked_financial_facts` — R1 / `de3509c41c24` |
| D14 | `FieldAdvanceExpenseAdjustmentTests.Reduction_keeps_document_consumed_until_full_route_approves` — R1 / `de3509c41c24`<br>`FieldAdvanceExpenseAdjustmentTests.Replacement_preserves_originals_applies_once_and_requires_latest_source` — R1 / `de3509c41c24` |
| D15 | `FieldAdvanceSettlementRaceTests.Commit_failure_rolls_back_money_allocation_receipt_and_closure` — F1 / `d254ae05d7bc`<br>`FieldAdvanceSettlementClosureTests.Refund_correction_and_close_follow_database_lock_order` — R1 / `b3b7af1e0625`<br>`FieldAdvanceSettlementClosureTests.Refund_acceptance_racing_close_rechecks_committed_balance_and_late_refund_is_denied` — R1 / `b3b7af1e0625`<br>`FieldAdvanceSettlementClosureTests.Shortfall_prepare_and_close_cannot_commit_against_stale_family` — R1 / `b3b7af1e0625`<br>`FieldAdvanceSettlementClosureTests.Every_open_obligation_blocks_zero_balance_closure` — R2 / `b3b7af1e0625`<br>`FieldAdvanceSettlementClosureTests.Registered_worker_closes_reconciled_request_without_a_human_command` — R2 / `b3b7af1e0625` |
| D16 | `FieldAdvanceSettlementDeliveryTests.Work_proof_positive_bytes_and_revocation_at_each_delivery_boundary` — F2 / `e22af2355992`<br>`FieldAdvanceSettlementDeliveryTests.Lost_final_review_response_recovers_exact_receipt_without_second_money_effect` — F2 / `e22af2355992`<br>`FieldAdvanceSettlementCommandTests.Same_person_second_account_cannot_replay_first_accounts_receipt` — R1 / `41c899d335ff`<br>`FieldAdvanceSettlementHttpTests.Committed_command_guard_can_deny_receipt_without_rolling_back` — R1 / `19c20cf95875` |
| D17 | `FieldAdvanceSettlementDeliveryTests.SevenC_backup_clone_migrates_twice_and_idempotent_sql_twice_without_money_grants_or_policy` — F2 / `e22af2355992`<br>`FieldAdvanceSettlementPolicyTests.Legacy_snapshot_is_readable_but_missing_correction_window_is_not_invented` — R1 / `ce2ef1c81797` |
| D18 | `FieldAdvanceSettlementDeliveryTests.Backup_restore_preserves_pending_refund_closed_case_and_revoked_rights` — F2 / `e22af2355992`<br>`FieldAdvanceSettlementDeliveryTests.Local_evidence_move_checks_digest_and_retains_financial_references` — F2 / `e22af2355992`<br>`FieldAdvanceSettlementDeliveryTests.Duplicate_pair_version_rejection_and_populated_down_are_atomic` — F2 / `e22af2355992` |

### ขอบเขต hash และความต่างระหว่างรัน

หลักฐาน [source-scope-audit.json](/tmp/7d-t18-evidence/source-scope-audit.json) และ [runtime-inputs-final.sha256.json](/tmp/7d-t18-evidence/runtime-inputs-final.sha256.json) ตรวจ runtime inputs **624 paths** จากเริ่มงานถึงจบตรงกันทั้งหมด; manifest SHA-256 `e4e73979b4c04834a55aaecc632be80e79a51a342f132de7b710043ff8b8e2d8`. Set นี้ใช้ทุก tracked path จากต้นงานยกเว้น docs/, .superpowers/, integration-test directory และ *.md แล้วรวม runtime paths ใน prior14 ด้วย ครอบคลุม app/backend production, E2E seed/spec/fixture, infra/config/dependency locks เป็น source-input evidence ไม่ใช่ historical binary hash

ก่อน/หลังแต่ละ run เก็บ tracked-plus-Task18-test manifest; **ไม่ใช่ทั้ง worktree** เพราะเอกสารใหม่ที่ยัง untracked ไม่อยู่ใน initial manifest เอกสารใหม่เก็บ hash แยกใน [task18-files.sha256.json](/tmp/7d-t18-evidence/task18-files.sha256.json). ไม่มีการอ้าง entire-tree stable โดยเฉพาะ run ที่ทับกับการแก้เทสต์ดังนี้:

| Run | source paths ที่เปลี่ยนระหว่าง before/after | runtime delta |
|---|---|---|
| https-4000-field-advance-settlement-review | `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceSettlementDeliveryTests.cs` | none |
| https-4001-field-advance-settlement | `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceSettlementDeliveryTests.cs` | none |
| https-4001-field-advance-settlement-review | `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceSettlementDeliveryTests.cs` | none |
| https-4001-identity | `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceSettlementDeliveryTests.cs` | none |
| scoped-format | `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceSettlementDeliveryTests.cs` | none |
| https-4001-scopes | `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceSettlementDeliveryTests.cs` | none |
| https-4001-field-advance-grant | `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceSettlementDeliveryTests.cs` | none |
| mapped-regression | `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceSettlementDeliveryTests.cs` | none |
| https-4001-field-advance-request | `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceSettlementDeliveryTests.cs` | none |

การนำ HTTPS เดิมมาใช้ครอบคลุม source runtime เดิมเท่านั้น; final focused ใช้ source ของ class/helper ที่ตรวจ hash เท่ากับไฟล์ส่ง review แล้ว แต่ไม่ได้กล่าวว่า source ของเทสต์ที่ไม่เกี่ยวข้องหรือเอกสารไม่เปลี่ยนระหว่างทุก run. prior14 ตรวจตรง byte hashes ทุกคำสั่งจาก `/tmp/7d-t18-prior.json`

### ประวัติที่ล้มและข้อจำกัดการยอมรับ

เก็บทุก failure log โดยไม่ทับ: race-first compile import/EF1002; focused-second xUnit2031; focused-third 10/15 (T11 actor มี Approver history และ T4 current-model บน schema เก่า); exact-gap-fix 4/5 (T4 Other* FK fixture); t4-provenance-fix (Down ถูก Task13 guard ก่อนถึง Task4); final-delivery 11/12 (เปรียบ JSON string แทน semantic record เพราะ jsonb canonicalization) ทั้งหมดแก้เฉพาะ fixture/assertion ไม่ใช่ product RED ส่วน historical GREEN ก่อนเพิ่ม Pending/digest assertions ไม่แทนผล F2 สุดท้าย รายละเอียดครบในรายงานผู้ลงมือ

ผลตรวจอิสระและชุดแก้ภายหลังบันทึกไว้ต้นรายงาน การตรวจรับสุดท้ายผ่านครบแล้ว ปรับสถานะแผนและ commit เฉพาะ24ไฟล์ที่ผ่านตรวจ โดยคง prior14ไฟล์เดิม
<!-- task18-evidence:end -->

## ข้อตัดสินใจระหว่างดำเนินแผนและผลกระทบหากตีความผิด

บันทึกตามลำดับใน progress ledger โดยคงแบบที่อนุมัติเป็นหลัก ไม่มีการอนุญาต deploy หรือย้ายฐานจริงจากข้อตัดสินใจเหล่านี้

| งาน | ข้อตัดสินใจ | ผลกระทบหากผิด |
|---|---|---|
| 1 | ทำเฉพาะ Task ที่ผู้ใช้สั่ง ไม่เริ่มงานถัดไปอัตโนมัติ | งานที่ยังไม่สั่งจะรอ |
| 1 | ผู้ลงมือรันเฉพาะจุด ผู้ควบคุมรันชุดใหญ่หลังตรวจอิสระ | เทสต์เฉพาะจุดยังใช้รับรองทั้งงานไม่ได้ |
| 1 | ใช้โมเดลสืบทอดตามกติกาเครื่องมือ ไม่ override | ไม่ได้ปรับต้นทุนด้วยการเลือกโมเดลเอง |
| 1 | ใช้ worktree แยกเดิมและคงงานค้างนอกขอบเขต | ต้องตรวจ hash และ stage แบบระบุไฟล์ |
| 1 | ยอดคงค้างติดลบตามแบบหมายถึงภาระบริษัท ไม่ใช่ตัวเลขผิดเสมอ | การตีความบัญชีและ workflow ต้องแก้หากนโยบายเปลี่ยน |
| 1 | ไม่รัน baseline ใหญ่ซ้ำเมื่อ source ตรงกับผลผ่านล่าสุด แต่รันครบหลังแก้ | ต้องยกเลิกการอ้าง baseline ทันทีหาก source ไม่ตรง |
| 1 | ปัญหาแข่งระหว่างแก้เอกสารกับรับรองฉบับอยู่ในข้อกำหนดความคงเดิม ต้องแก้ทันที | เพิ่มเทสต์และการล็อก |
| 2 | จำกัดขอบเขต Task2 และใช้ baseline ที่ hash ตรง | ไม่ครอบคลุมงานถัดไป |
| 2 | เพิ่ม validation/comparison ใน policy view เพื่อแสดงการเปลี่ยนกติกาครบ | อาจต้องปรับ UI contract หากตีความผิด |
| 3 | typed workflow/hash/response guard อยู่ใน Task4–15 ตามแผน | ห้ามกล่าวว่าจุดเชื่อมต่อเหล่านั้นเสร็จตั้งแต่ Task3 |
| 4 | แยก validator ร่วมและเพิ่ม migration ของคู่เอกสารซ้ำ | ต้องแก้ schema หากโครงสร้างไม่เหมาะสม |
| 5 | ร่างทั่วไปไม่กันยอด; การแก้รอบส่งกลับรักษายอดกันเดิมและคืนเฉพาะส่วนต่าง; ใช้ snapshot ฉบับอนุมัติ | การกันยอดหรือฐานอัตราจะผิดหากตีความสัญญาผิด |
| 8 | เติมสัญญา owner confirm/update/cancel และ guard เงินคืนร่วมกับค่าใช้จ่ายให้ครบแบบ | อาจต้องปรับ API แต่ไม่มีการย้ายฐานจริง |
| 9 | ใช้ latest accepted net/version และเคสแก้ไขแยก เก็บ Closed/history เดิม; ผู้ตรวจต้องต่างจากผู้เสนอ | ต้องทบทวน schema/contract หากนโยบายไม่ตรง |
| 10 | ใช้ full replacement และผลต่างการจัดสรร; สะสางส่วนเกินด้วยข้อเท็จจริงการเงินที่รับรอง ไม่ใช่บันทึกหมายเหตุให้ยอดศูนย์ | เพิ่มโครงสร้างข้อเท็จจริงและเทสต์ |
| 13 | ตรวจ guard ประวัติก่อน Down เพื่อไม่ให้ schema ถูกเปลี่ยนบางส่วนแล้วล้ม | อาจปฏิเสธการ downgrade ที่มีประวัติ แม้ลดเพียงหนึ่งขั้น |
| 14 | เพิ่ม provenance สำหรับการปิดโดยระบบ แยกจากคำสั่งมนุษย์ ไม่สร้างคนหรือบัญชีสมมติ | อาจต้องแก้ migration แต่ไม่กระทบฐานจริงในงานนี้ |
| 16 | เติม read contract และ explicit same-command recovery เท่าที่ UI ต้องใช้ โดยคงสิทธิ์เดิม | ต้องแก้ API/client หากสัญญาไม่เหมาะสม |
| 17 | เพิ่มคิวและข้อมูลเฉพาะงานที่ได้รับสิทธิ์ ไม่ให้สิทธิ์รายงานทั่วไปโดยอัตโนมัติ | ต้องตรวจการเปิดเผยข้อมูลทุกขอบเขตที่เพิ่ม |
| 17 | คงเทสต์เบราว์เซอร์หลาย hold และเพิ่มการโหลด scope ต่อเนื่อง เพราะไม่มีข้อกำหนดจำกัด 100 scope | เพิ่มเวลาเทสต์และความซับซ้อน pagination |
| 18 | ใช้ worktree และ baseline เดิมที่ตรวจแล้ว; รันชุดใหญ่บนโค้ดสุดท้าย; เก็บหลักฐานก่อนล้างไฟล์ชั่วคราว | ต้องเปิดเผย dirty inputs และไม่อ้าง clean tree |
| 18 | ตรวจจาก tree ก่อน 7D ที่ตรงกับ 7C ที่ merge แล้ว แทน merge-base ที่เก่าจาก squash | เสี่ยงตกหล่นขอบเขตหากเลือกฐานผิด จึงตรวจ tree hash เท่ากัน |
| 18 | แก้ข้อพบสำคัญ 6 จุดในชุดเดียว คงสิทธิ์และประวัติ; ไม่ตั้งนโยบายว่าไฟล์เดียวเท่ากับธุรกรรมเดียวเอง | ต้องตรวจซ้ำผลกระทบจากชุดแก้ และยังไม่รับรองการตรวจธุรกรรมซ้ำทุกแบบ |

## รายการที่ยังติดตามหลังการตรวจอิสระ

| รายการ | ขอบเขตที่ยังไม่ปิด |
|---|---|
| M2-R1 | เทสต์ตรงกรณีสอง session ที่ถูกต้องของคนเดียว แต่ให้ grant เพียงบัญชีเดียว ยังไม่ยืนยันครบ |
| R2-C1 | ยังไม่ตรวจทุก callsite ของการปรับ environment ใน HTTPS fixture |
| HTTPS504 / OpenAPI500 | ยังไม่ยืนยันสาเหตุและการแก้จากหลักฐานเหตุจริง ผล smoke ผ่านไม่ถือว่าปิด |
| W-M1 / T18-M1 และ Minor log | คง EF10102 รวม T12-M1, T13-REVIEW-M1, T14-REVIEW-M2, T15-REVIEW-M1, T16-M2 ไม่ปิด warning โดยไม่รู้ query ต้นเหตุ |
| W-M2 / T18-M2 | เทสต์ retry หลังแข่งขันตรวจเพียง409 ยังควรระบุ error code ของข้อจำกัดยอดให้ตรงกรณี |
| W-M3 / T13-M1 / T14-M1 / T17-M2 | บางหน้าจอยังแสดงรหัสสถานะหรือชนิดรายการภาษาอังกฤษ |
| W-M4 / T15-REVIEW-M2 / T16-M1 | ส่วน validator/UI ที่ยังเขียนอัดแน่น คงติดตามด้านการบำรุงรักษา |
| การใช้งานจริง | มือถือจริงและ NAS จริงยังพักไว้ ไม่รับรอง production migration/restore, bank truth, load, RPO/RTO หรือ outage/failover จริง |

ข้อจำกัดเพิ่มเติม: การจำลอง response หายทำผ่าน Playwright หลัง upstream commit ไม่ใช่การตัด TCP จริง การแข่งขันล็อกใช้ PostgreSQL/บริการจริงใน process ไม่ใช่ load test และยังไม่มีนโยบายว่าหลักฐานไฟล์หนึ่งไฟล์เท่ากับธุรกรรมคืนเงินหนึ่งรายการเสมอ จึงไม่กล่าวอ้างว่าตรวจธุรกรรมซ้ำได้ทุกแบบ เอกสารซ้ำข้ามคำขอและการใช้หลักฐานข้ามช่องทางยังใช้ข้อกำหนดที่อนุมัติเดิม

## ผลตรวจทั้งโครงการบน source สุดท้าย

| คำสั่ง | เริ่ม UTC | จบ UTC | Exit |
|---|---|---|---:|
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | 2026-11-03T18:16:13.496894+00:00 | 2026-11-03T18:16:35.335054+00:00 | 0 |
| `dotnet build backend/TPR10.sln --no-restore --verbosity minimal` | 2026-11-03T18:16:35.335379+00:00 | 2026-11-03T18:16:40.399486+00:00 | 0 |
| `dotnet test backend/TPR10.sln --no-build --no-restore --logger console;verbosity=normal` | 2026-11-03T18:16:40.399665+00:00 | 2026-11-03T20:52:40.094110+00:00 | 0 |
| `npm test` | 2026-11-03T18:16:14.853755+00:00 | 2026-11-03T18:16:16.996583+00:00 | 0 |
| `npm run build` | 2026-11-03T18:16:16.996878+00:00 | 2026-11-03T18:16:31.932554+00:00 | 0 |
| `npm run lint` | 2026-11-03T18:16:31.932883+00:00 | 2026-11-03T18:16:35.546423+00:00 | 0 |
| `git diff --check` | 2026-11-03T18:16:35.546627+00:00 | 2026-11-03T18:16:35.568100+00:00 | 0 |

Backend ผ่าน 3578/3578; frontend ผ่าน261/261 ไม่มีการใช้ผล baseline Task17 แทนผลรอบนี้ Source/config/test829ไฟล์ตรงกับ manifest ก่อนรันทั้งหมด งานเดิม14ไฟล์ไม่เปลี่ยน index/commit ตรวจแยกก่อนส่งมอบ

หลักฐานก่อนและหลังแก้เก็บใน `.superpowers/evidence/module-7d-task18/focused-and-review-evidence.tar.gz` ภายในเครื่อง พร้อม SHA256; หลักฐานชุดใหญ่เก็บแยกหลังจบ ไม่รวม archive เข้า Git รายงานนี้ไม่รับรอง production, มือถือจริง หรือ NAS จริง

ข้อตัดสินใจเพิ่มเติม: แก้ F1-I1 ที่พบใหม่เฉพาะจุดแทนพักปัญหาที่ทำให้ผู้ใช้กู้ผลแล้วทำงานต่อไม่ได้ โดยตรวจแยกคนและคง unknown-command safety ผลกระทบหากตีความผิดคือการปรับ UI/เทสต์เพิ่ม การรัน backend คู่ขนานกับ UI รอบแรกหยุดที่ Format ก่อน Build/Test จึงไม่ใช้รอบนั้นอ้างผลผ่าน หลังแก้แล้วรันชุดใหญ่ใหม่ครบเพียงรอบที่รายงานข้างต้น
