# รายงานการตรวจ Module 6A — ทะเบียนบุคลากรและสิทธิ์

วันที่: 25 กันยายน 2026 (Asia/Bangkok)

สถานะ: **ตรวจ Exit Gate ครบและแก้ประเด็น Code Review แล้ว — ส่งมอบเฉพาะ Module 6A**

ปิดการตรวจทั้งชุด: 2026-09-25 01:31:59 UTC (08:31:59 Asia/Bangkok) ไม่มีการเปลี่ยนโค้ดระหว่างรอบ backend สุดท้าย

Commit ส่งมอบโค้ด Task6: `a5108e3` — `feat: add attendance directory portal and verified exit gate` (25ไฟล์) รายงานบรรทัดนี้เป็นการบันทึก SHA หลัง commit ไม่เปลี่ยนโค้ดที่ผ่านการตรวจ

สาขา `codex/module-6a-directory-access` แยกจาก main ที่ `b8b9c0f` งาน Tasks 1–5 อยู่ใน commits `fc2c7b2`, `3927a3f`, `6a37bfa`, `1fc25bf`, `65605f2` ส่วน Task 6 ส่งมอบพร้อมรายงานฉบับนี้ ไม่มี push/merge/deploy ในงานนี้

## สิ่งที่ส่งมอบในขอบเขต 6A

- ตารางต้นสังกัดหลัก สายบังคับบัญชา และการมอบหมาย HR พร้อมช่วงเวลาไม่ซ้อน ประวัติไม่ถูกแก้ย้อนหลัง และ migration ที่ไม่ลบข้อมูลเงียบ ๆ
- สิทธิ์ใหม่มอบอย่างชัดเจน ไม่เพิ่มสิทธิ์ให้ Admin อัตโนมัติ ป้องกันเพิ่มอำนาจตนเองทั้งผ่านบทบาทและความสัมพันธ์
- API จัดการทะเบียน 12 operations และ API แสดงคำแนะนำสิทธิ์ 1 operation พร้อม version, transaction, audit และถอน session ที่ได้รับผล
- การตรวจสิทธิ์แยกพนักงาน/หัวหน้า/HR และตัวคัดเลือกเส้นทางหัวหน้า→HR โดยไม่อนุมัติคำร้องในขั้นนี้
- หน้า `/portal/admin/attendance-directory` พร้อมค้นหา/แบ่งหน้า ประวัติอ่านอย่างเดียว และการจัดการ session/คำตอบ API ที่มาช้า
- [คู่มือภาษาไทย](../runbooks/module-6a-directory.md) และสัญญา OpenAPI ทั้ง Development/Production

ยังไม่มีการถ่ายรูป กล้อง GPS ที่เก็บรูป/NAS การลงเวลา หรือคำร้องแก้ไขเวลา งานเหล่านี้อยู่ใน 6B–6D

## หลักฐานที่ตรวจแล้ว

| การตรวจ | ผลจริงล่าสุด |
| --- | --- |
| Tasks 2–5 focused tests | 51 / 48 / 57 / 33 ผ่านตามลำดับ |
| OpenAPI ทุกโมดูล | 124 ผ่าน ไม่มีข้าม |
| Node tests | 40 ผ่าน ไม่มีข้าม |
| หน้าใหม่ HTTPS dev 4000 | 7 ผ่านสองรอบล่าสุด พร้อม TLS/cookie/CSRF/Host acceptance |
| Backend ทั้งชุดล่าสุด | 997/997 ผ่าน ไม่มีล้มเหลว/ข้าม ใช้เวลา22.4618นาที; ก่อนหน้านี้993/993ผ่าน และมีรอบ996/997ที่บันทึก failure ไว้ด้านล่าง |
| Build/Lint/format รอบปิดงาน | Backend/API/fixture build ไม่มี warning/error; format verify และ frontend build/lint exit0 |
| HTTPS regression Module 2–3 | identity13/13 และ scopes11/11 ทั้ง4000/4001; production boundary2/2 |
| หน้าใหม่ production-build 4001 | 7/7 ผ่านสองรอบล่าสุด พร้อม TLS/cookie/CSRF/Host acceptance |
| Dependency audit | npm และ NuGet รวม transitive ไม่พบช่องโหว่จากแหล่งข้อมูลปัจจุบัน |
| ผู้ตรวจอิสระทั้งสาขา | Critical0 Important2 Minor0; แก้ I1 และพิสูจน์ coverage I2 แล้ว full gate หลังแก้ผ่าน997/997 |

หลักฐานรอบสุดท้าย: `/private/tmp/module6a-final-clean-rerun.log` (backend997), `module6a-delivery-node.log` (Node40), `module6a-delivery-lint.log`, `module6a-last-build.log`, `module6a-after-review-{backend,fixture}-{build,format}.log`, `module6a-delivery-{npm,dotnet}-audit.log` และ HTTPS `module6a-last-*.log` (scopesใช้ไฟล์ `module6a-last-scopes-prod-rerun.log`) ทุกคำสั่งสุดท้าย exit0 ผล dev identity/scopes อยู่ใน `module6a-final-identity-dev-rerun.log` และ `module6a-final-scopes-dev.log`

คำสั่ง backend รอบสุดท้ายใช้ `dotnet test backend/TPR10.sln --artifacts-path /private/tmp/tpr10-module6a-final-after-review --no-build --logger 'console;verbosity=normal'` หลัง build binary เดียวกันและไม่แก้ backend/tests ระหว่างรัน; คำสั่งทั้งหมดอยู่ท้าย Implementation Plan ไม่มีการเลือกข้าม security tests

## ข้อผิดพลาดที่พบและวิธีพิสูจน์

1. Version รายการใหม่กลับเป็น1ทำให้คำขอเก่าทับข้อมูลได้หลัง replace: tests membership/reporting คาด409แต่ได้200 ก่อนแก้ ใช้ version เพิ่มต่อเนื่องข้ามประวัติภายใต้ lock แล้วผ่าน
2. EF downgrade commit แยก migration ทำให้ trigger ประวัติอาจถูกถอนก่อน migration เก่าปฏิเสธ: เพิ่ม preflight ปฏิเสธใน migration ประวัติด้วย ทดสอบว่าประวัติ migration และการป้องกันยังอยู่
3. หน้าใหม่ยังไม่มีจริง: HTTPS RED ไม่พบหน้า/ข้อความปฏิเสธก่อนเพิ่ม UI จากนั้นตรวจด้วย browser และฐานข้อมูลจริง
4. CSS ของฟอร์มลูกใช้ `visibility: visible` ทับกรอบที่ซ่อนเมื่อ session ตอบ503: E2E เห็นร่างทั้งที่ควรซ่อน ก่อนแก้เป็นการซ่อนด้วย `display` เฉพาะกรณีผิดพลาดและสืบทอด visibility ของกรอบ แล้วผ่าน
5. Test เดิมคาดรายการ API เฉพาะ Module 2–3: เพิ่มรายการ 13 operations ของ6Aแบบระบุชัด โดยคง assertion routes/cookie/security เดิมทั้งหมด และ OpenAPI124ผ่าน
6. ชุด identity E2E เดิม reload เมื่อ URL เปลี่ยนแต่เอกสาร login ยังโหลดไม่เสร็จ: เพิ่มการรอ heading และ load ตามตัวอย่างเดิม โดยไม่เพิ่ม timeout ไม่ลด assertion ตรวจ session หมดอายุ
7. Review พบการโหลดใหม่ล้าง error ก่อนตรวจสิทธิ์สำเร็จ และ error400เดิมบัง403ใหม่: browser tests ใหม่ล้มเหลวทั้งสองกรณีก่อนแก้ ใช้สถานะกู้คืนแยกและให้ความผิดพลาดด้านสิทธิ์/ความพร้อมมีลำดับสูงกว่า ปล่อยร่างเมื่อคำขอที่จำเป็นสำเร็จครบเท่านั้น
8. Review พบช่องว่างการทดสอบ race: เพิ่ม4กรณีควบคุมคำขอทะเบียนก่อน lock แล้วปิด Workspace/Department/บัญชีหรือ logout ผ่าน API จริงก่อนปล่อยคำขอ ยืนยันปฏิเสธ ไม่มีผลข้างเคียงบางส่วน ทดสอบเดิมผ่าน4; ทดลองถอดการตรวจหลังlockชั่วคราวทำให้ล้มเหลว4 แล้วคืนโค้ดเดิมผ่าน4 ไม่มีการอ้างว่าเจาะผ่านระบบเดิมได้

ผู้ตรวจอิสระ Ampere ตรวจทั้งสาขารวม Task6 ที่ยังไม่commit: Critical0, Important2, Minor0 ดำเนินการแก้/เพิ่มหลักฐานในรอบเดียว ไม่ใช้การตรวจซ้ำแทน test

ความไม่เสถียรที่พบระหว่างตรวจ: browser dev เคย timeout ในการเปิด login และการกดย้อนกลับหลัง logout ขณะเอกสารยังไม่พร้อม เพิ่มการรอหน้า login พร้อมใช้งานและตรวจ heading/input หลังย้อนกลับโดยไม่เพิ่ม timeout; production เคยได้ร่างว่างหนึ่งครั้ง จึงเพิ่ม assertion ค่าร่างก่อนจำลอง outage และขณะซ่อนเพื่อระบุตำแหน่ง แล้วรอบถัดไปผ่าน ไม่อ้างว่าพิสูจน์สาเหตุของเหตุการณ์ร่างว่างครั้งนั้นแล้ว ต้องติดตามหากเกิดซ้ำ ไม่มีการเปิด retries หรือข้าม test

การตรวจ regression ซ้ำยังพบ `scopes.spec.ts` กรณีผู้ดูแลสร้างโครงสร้าง timeout ที่ข้อความสำเร็จ โดย snapshot พบช่องรหัสว่างแต่ชื่อยังอยู่ ทั้งที่รอบก่อนผ่าน11/11 ไม่มีการแก้ production/test ของหน้านี้เพื่อกลบผล; รันซ้ำทั้งชุดและบันทึกผลล่าสุดแยกจากความล้มเหลวครั้งนั้น

Backend รอบหลัง Review ล้มเหลว1กรณี: `AssignmentApiTests.Forged_or_incomplete_body_is_denied_and_audited(field: "actorId")` ล้มที่ `RoleAuthorizationTests.AdminAsync` ตอน login คาด200แต่ได้403 ก่อนทดสอบ requestปลอมและก่อนยืนยันMFA ไม่ใช่การรับ requestปลอมสำเร็จ เมื่อรันแยกทั้ง3กรณีผ่านโดยไม่มีการเปลี่ยนโค้ด ยังไม่มีหลักฐานยืนยันสาเหตุของ setup failure ครั้งนั้น ผลรอบนี้996ผ่าน1ล้มเหลวไม่ถูกนับเป็น full gate ผ่าน; รันทั้งชุดใหม่โดยไม่แก้โค้ดแล้วผ่าน997/997 รวมทั้ง3กรณีดังกล่าว ต้องติดตามความไม่เสถียรแยกจากการส่งมอบฟีเจอร์

คำเตือนที่ไม่ใช่ผล audit ช่องโหว่: `npm ci` มี deprecation จาก dependency เดิม (`inflight`, `rimraf`, `glob`, `eslint`, `@humanwhocodes/config-array`, `@humanwhocodes/object-schema`) และ Playwright แจ้ง `NO_COLOR` ถูกแทนด้วย `FORCE_COLOR` ไม่อ้างว่า output ทั้งหมดไม่มี warning และไม่อัปเกรด dependency นอกขอบเขตเพื่อซ่อนคำเตือน

## ข้อวินิจฉัยระหว่างพัฒนาและผลกระทบ

| ข้อวินิจฉัย | เหตุผลและต้นทุน/ความเสี่ยง |
| --- | --- |
| เริ่มเฉพาะ Task1 ตามคำสั่งแรก แล้วขยายครบ6Aตามคำสั่งใหม่ | คำสั่งล่าสุดเป็นหลัก ไม่ขยายไป6B–6Dหรือ deploy |
| คัดลอกเอกสารที่อนุมัติเข้า worktree ไม่แก้ไฟล์เดิมใน main | รักษางานผู้ใช้; ต้องจัดการเอกสารซ้ำเมื่อรวมสาขาภายหลัง |
| Downgrade ปฏิเสธเมื่อมีประวัติหรือ grant ใหม่ทั้ง7รายการ | ป้องกันสูญหาย; ย้อนรุ่นต้องวางแผนข้อมูลก่อน |
| รองรับการเขียนทะเบียนเฉพาะ READ COMMITTED | ต้องเห็นข้อมูลหลังรอ lock; ผู้เรียก isolation สูงกว่าต้องออกแบบทางเลือก |
| ปรับ assertions จำนวน catalog จาก12เป็น19 โดยรักษารหัสเก่า | ตรงสัญญาใหม่; test ต้องตาม catalog เมื่อมีการเปลี่ยนโดยตั้งใจ |
| Review Task1 แยกเรื่อง graph/สิทธิ์/API/UI ไว้ในTasks2–6 | เป็นขอบเขตชั่วคราว ไม่ใช่ยกเว้นจากการตรวจรอบจบ |
| นับความยาวเหตุผลเป็น Unicode scalars และปฏิเสธ UTF-16 เสีย/อักขระควบคุม | ตรง PostgreSQL; client ต้องไม่นับ emoji เป็นสองตัวโดยอัตโนมัติ |
| เพิ่ม migration ประวัติใหม่แทนเขียนทับ Task1 | ฐานข้อมูลที่เคยอัปเกรดแล้วไปต่อได้; มี migration เพิ่มหนึ่งขั้น |
| ตัดเวลาเป็นความละเอียด microsecond และช่วงว่างตอบ409 | ไม่แต่งเวลา; ผู้ใช้ต้องตรวจและลองใหม่เมื่อเหตุการณ์อยู่จังหวะเดียวกัน |
| คง constructor เดิมที่ legacy tests ใช้และเชื่อม helper ด้วย scoped dependencies เดิม | ลดการเปลี่ยน interface; จุดประกอบ dependency บางจุดยังต้องดูแลชัดเจน |
| แยก list/options เป็น partial service และใช้ DirectoryQueries ร่วม | ลดขนาดไฟล์; ต้องเรียก query ภายใต้ transaction/lock ที่กำหนด |
| เปลี่ยนต้นสังกัดแล้วสิ้นสุดสายทั้งของตัวเองและลูกทีมที่เกี่ยวข้อง | ไม่คืนอำนาจเก่าโดยไม่ตั้งใจ; ต้องมอบหมายหัวหน้าใหม่อย่างชัดเจน |
| ตรวจอำนาจจากความสัมพันธ์ก่อน–หลัง แม้ยังไม่มี capability ธุรกิจ | ป้องกันสิทธิ์แฝง; เข้มกว่าการดูสิทธิ์ที่ใช้งานได้ขณะนั้นอย่างเดียว |
| Version ใช้ค่าสูงสุดในประวัติ+1 ไม่กลับเป็น1หลัง replace | ป้องกัน ABA/lost update; client ต้องใช้ค่าจาก server, ค่าสูงสุดเต็มชนิดข้อมูลตอบ409 |
| ใช้ HTTPS fixture ร่วมแต่คง allowlist entrypoint เดิมและเพิ่ม entrypointชื่อคงที่ | ไม่ทำซ้ำการดูแล CA/container; ต้องตรวจ regression ทุกชุดเดิม |
| ทดสอบ backend ทั้งชุดด้วย artifacts ชั่วคราวแยก | ไม่ให้ build ของ HTTPS เปลี่ยน DLL ระหว่างทดสอบ; ใช้พื้นที่ชั่วคราวเพิ่ม |
| เพิ่ม contract 13routes ในชุดนับ API เดิม | ตรวจว่ามีครบและไม่มี routes เกิน; ต้องดูแลรายการสัญญาทดสอบร่วม |
| Review ทั้งสาขารวม Task6 ที่ยังไม่commit ระหว่างรอ gate | brief ต้องการ review ก่อนcommit; ผู้ตรวจต้องรวม tracked/untracked ทั้งหมด และยังปิดงานไม่ได้จน gateครบ |
| ประเด็น race เป็นช่องว่างหลักฐาน ไม่ใช่ runtime defect ที่พบแล้ว | ใช้ mutation control พิสูจน์ test ก่อนคืนโค้ดเดิม; ห้ามทิ้งโค้ดทดลองใน commit |
| สิ่งที่ reviewer ไม่ตัดสิน: รายงาน/ผล fullsuite | ตรวจจริงหลังแก้ก่อนปิดงาน; ใช้เวลาเพิ่ม ไม่ถือผลบางส่วนแทนทั้งหมด |
| สิ่งที่ reviewer ไม่ตัดสิน: กล้อง/ที่เก็บ/ลงเวลา/คำร้อง | คงขอบเขต6B–6D; 6Aยังใช้ลงเวลาไม่ได้ |
| สิ่งที่ reviewer ไม่ตัดสิน: DBA และการสมคบกัน | คงขอบเขต application พร้อม governance/audit; ไม่รับรองการป้องกันผู้มีสิทธิ์ฐานข้อมูลโดยตรง |
| สิ่งที่ reviewer ไม่ตัดสิน: throughput ของ lock | ต้อง benchmark ก่อนProduction; อาจเป็นคอขวด |
| Reviewer ยอมรับ version มากกว่า1หลังreplace | คงการแก้ABA; clientห้ามสมมติเลขรุ่น |
| Reviewer ยอมรับการรอหน้าใน expiry test | คง assertion/timeout; ยังต้องเฝ้าระวังความไม่เสถียรของ browser |
| รอหน้า login พร้อมก่อนย้อนกลับใน browser test | ตรวจการใช้งานจริงไม่ดู URL อย่างเดียว; ไม่เพิ่ม timeout และยังต้องติดตาม navigation timing |
| ร่างว่างหนึ่งครั้งที่ไม่เกิดซ้ำหลังเพิ่มจุดตรวจค่า | คง assertions และรายงานความไม่แน่นอน; ยังไม่ยืนยันสาเหตุและต้องติดตามหากเกิดซ้ำ |
| Backend setup login403ครั้งเดียวที่รันแยกไม่เกิดซ้ำ | ไม่เปลี่ยน CSRF/login และไม่อ้างว่าแก้สาเหตุแล้ว; ต้องรันทั้งชุดใหม่ ใช้เวลาเพิ่มและยังมีความเสี่ยงความไม่เสถียร |

## ข้อจำกัดและการรับช่วง

- ยังไม่ยืนยันทุก browser/OS จาก Firefox Playwright เพียงอย่างเดียว การส่ง focus/pagehide บางกรณีเป็นการส่ง event แบบควบคุมเพื่อให้ทดสอบซ้ำได้
- ไม่มีการรับรอง immediate remote revocation ทุกหน้าต่างเมื่อทั้ง broadcast/storage ถูกปิด ต้องตรวจซ้ำเมื่อกลับ focus และ API ยังคงเป็น authority
- identity lock รวมต้องประเมิน throughput ก่อน Production; DBA โดยตรงและผู้จัดการหลายคนสมคบกันยังต้องใช้ governance/audit
- ประวัติก่อนมีทะเบียนไม่มี snapshot ที่พิสูจน์ได้ จึงไม่แต่งประวัติย้อนหลังเพื่อให้ผ่านสิทธิ์
- Preview เดิมเปิด API/proxy และ Next dev4000 คืนแล้ว ใช้ฐานข้อมูลเดิมไม่แก้หรือลบ; API readiness ผ่าน ยังเป็น main เดิมไม่ใช่6Aที่ยังไม่ได้ merge
- ไม่มี Minor ที่ reviewer ฝากไว้; ความไม่เสถียร browser ระหว่างตรวจระบุไว้ด้านบน ไม่อ้างว่าการผ่านรอบล่าสุดลบประวัติ failure
