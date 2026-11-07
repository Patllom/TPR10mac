# รายงาน Module 7D Task 2 — กติกาจำนวนวันแก้ไข

สถานะ: ผ่านการตรวจรับ Task 2 — Test, Build, Lint/Format และผู้ตรวจอิสระครบ

## ขอบเขต

เพิ่มจำนวนวันแก้ไขแบบ optional ในกติกาบริษัท/โครงการ รักษา JSON และ hash ของ snapshot เก่า เพิ่ม resolver สำหรับกติกาที่มีผลตอนส่งกลับ และหน้าตั้งค่า ไม่รวม workflow ส่งกลับของ Task ถัดไป

ฐานก่อนเริ่ม `f071d2de6edb15e13d5baaf6def6e1f7ecfca7b7` ใช้ worktree เดิม แยกผู้ลงมือกับผู้ตรวจ ผู้ประสานงานรัน Test/Build/Lint ครบก่อนแจ้งเสร็จและ commit

## หลักฐานก่อนเริ่ม

ตรวจ hash 709 paths ตรงกับ source ที่ Backend 3,083 เทสต์ผ่านใน Task 1 จึงใช้ผลนั้นเป็น baseline เท่านั้น ไม่ใช่ผลรับรอง Task 2 งานค้าง 14 paths คงเดิม

## ผลตรวจ

ผู้ลงมือ Rawls; ผู้ตรวจอิสระ Nash ให้ Spec PASS และ Quality APPROVED ไม่พบประเด็นสำคัญใหม่ โดยอ่าน diff และตรวจหลักฐานจริงแยกจากผู้ลงมือ

เพิ่มจำนวนวันแก้ไขแบบ nullable โดยละ null จาก JSON เพื่อคงรูปแบบและ hash เดิม ส่งค่าครบผ่านร่าง การอนุมัติ ประวัติ และ API; resolver ใช้กติกาปัจจุบันที่อนุมัติแล้ว พร้อมคืนค่า/revision/hash โดยต้องเรียกภายใน transaction ของผู้มีสิทธิ์ ไม่เพิ่ม endpoint หรือสิทธิ์ใหม่

หน้าตั้งค่าเพิ่มการแสดงยังไม่ตั้งค่า ตั้ง/ล้างค่า เปรียบเทียบฉบับ และห้ามแก้ฉบับรอตรวจ เพิ่ม policy-view.ts สองบรรทัดเพื่อ validation และแสดงความต่าง ซึ่งเป็นส่วนจำเป็นของ Task นี้

TDD: RED จริงจากรับ 0/-1 และค่าหายผ่าน lifecycle, client ไม่ปฏิเสธค่าและไม่แสดงความต่าง, downgrade ไม่ป้องกันค่าที่มีอยู่ หลังแก้ focused PostgreSQL ผ่าน 20 กรณี client 21 กรณี UI จริงบน Firefox 1 กรณี และ migration regression 3 กรณี

Migration เพิ่ม nullable integer/CHECK โดยไม่ backfill; Down ล็อกและป้องกันทิ้งค่าปัจจุบันหรือหลักฐานที่เคยอนุมัติ ปรับเทสต์ downgrade เดิมให้ query เฉพาะคอลัมน์ใน schema หลัง downgrade โดยคง assertion provenance/hash/SQLSTATE เดิม

ชุดตรวจรวมสุดท้าย: Backend ผ่าน 3,103 เทสต์ทั้งหมด ไม่มี failure; Frontend ผ่าน 178 เทสต์ Build ทั้งสองส่วนและ Lint/Format/diff check ผ่านครบ รวมทั้ง UI จริงบน Firefox 1 กรณีที่ตรวจแยกไว้

ประวัติที่ไม่ใช่ behavioral RED: decimal-scale fixture mismatch, Chromium ไม่มี binary, fixture header ขาด UTF-8 และ format whitespace ครั้งแรกถูกแก้แล้ว เทสต์ regression เดิมพบ query คอลัมน์หลัง downgrade จึงปรับ projection และรันกลุ่มนั้นซ้ำผ่าน ไม่ใช้ผลล้มเหลวเหล่านี้แทน RED ของฟีเจอร์

ยังมี warning EF Query[10102] จาก query identity เดิม จัดอยู่ใน Minor log ที่ติดตาม ไม่อ้างว่า output ไม่มี warning

ข้อจำกัด: การคำนวณ deadline ตามวันกรุงเทพและบันทึก window ลงรอบแบบ atomic เป็น Task ถัดไป ผลนี้รับรอง resolver/window และสัญญาการเรียก ไม่ใช่ workflow ส่งกลับที่ทำครบแล้ว

## รายการคงติดตาม

คง M2-R1/R2-C1, HTTPS 504, Minor log, OpenAPI 500, T13-M1/T14-M1 พักมือถือจริงและ NAS จริง ไม่ push/merge/deploy/migrate ฐานจริง

## รอบแก้หลังตรวจเต็ม

ชุด Backend รอบแรกพบ `FieldAdvanceRequestSchemaTests.Migration_preserves_7A` ล้มเหลวจริง: JSON ทั้งแถวต่างเพราะคอลัมน์ nullable ใหม่ จึงหยุดรอบนั้น (exit 130) และไม่ใช้เป็นผลผ่าน

ผู้ลงมือแก้เฉพาะเทสต์: เทียบข้อมูลเดิมทุก field/row โดยแยกคอลัมน์ใหม่ พร้อมตรวจชนิด nullable ไม่มี default และทุกค่าเป็น null หลัง migration มี runtime RED ก่อนแก้ และกลุ่ม schema ผ่าน 45/45 หลังแก้ ผู้ตรวจอิสระรอบเฉพาะจุดให้ PASS ไม่ลดการตรวจข้อมูลเดิม ไม่มี source ฝั่ง frontend เปลี่ยน จึงคงผล Frontend 178/Build/Lint ที่ผ่านแล้ว ส่วน Backend รันใหม่ทั้งชุด

## ผลตรวจรับสุดท้าย

Backend รอบใหม่จบ 2026-10-31 13:27:04 UTC (20:27:04 เวลาไทย) ผ่าน 3,103/3,103 รวมเทสต์ Task 2 ใหม่ 20 กรณี และเทสต์ migration ที่แก้ผ่านในชุดเต็มแล้ว source hash คงเดิมตลอดรัน ผลนี้แทนรอบก่อนที่ยกเลิกโดยไม่ลบประวัติ failure

Frontend ผ่าน 178/178, UI จริง Firefox 1/1 แยกจาก npm test; หลังผล Frontend เปลี่ยนเฉพาะเทสต์ Backend หนึ่งไฟล์ จึงใช้ผลเดิมได้ ไม่อ้างว่ารัน Frontend ใหม่หลังรอบแก้

| คำสั่ง | ผล | เวลาตรวจเสร็จ UTC |
|---|---|---|
| `dotnet build backend/TPR10.sln --no-restore --verbosity minimal` | ผ่าน (exit 0) | 2026-10-31T11:47:03.408389+00:00 |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | ผ่าน (exit 0) | 2026-10-31T11:47:20.977342+00:00 |
| `dotnet test backend/TPR10.sln --no-build --no-restore --logger console;verbosity=normal` | ผ่าน (exit 0) | 2026-10-31T13:27:04.171247+00:00 |
| `npm test` | ผ่าน (exit 0) | 2026-10-31T11:05:52.709286+00:00 |
| `npm run build` | ผ่าน (exit 0) | 2026-10-31T11:06:02.116760+00:00 |
| `npm run lint` | ผ่าน (exit 0) | 2026-10-31T11:06:03.691699+00:00 |
| `git diff --check` | ผ่าน (exit 0) | 2026-10-31T11:06:03.759364+00:00 |

หลักฐาน local: `/tmp/7d-t2-final2-backend-summary.jsonl`, `/tmp/7d-t2-final2-test.log`, `/tmp/7d-t2-final-frontend-summary.jsonl`, `/tmp/7d-t2-ui-firefox-green.log` และ `/tmp/7d-t2-worker-commands.jsonl` ผู้ตรวจเฉพาะรอบแก้ให้ Spec PASS และ Quality APPROVED โดยไม่พบประเด็นค้างใหม่

commit เฉพาะไฟล์ Task 2 ที่ตรวจแล้ว 20 ไฟล์และรายงาน/แผน 2 ไฟล์ สงวนไฟล์ค้างเดิม 14 paths ทั้งหมด ไม่รวม Task 3, push, merge, deploy หรือ migration ฐานจริง
