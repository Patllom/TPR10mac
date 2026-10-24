# รายงานตรวจสอบ Module 7C — Task 5

วันที่เริ่ม: 24 ตุลาคม 2569

สถานะ: Task 5 ผ่านการตรวจรับเมื่อ 25 ตุลาคม 2569 โค้ด `4774d821c38b60bb06868461cc79e745cab9c2e5` ผ่านผู้ตรวจอิสระและ Test/Build/Format/Lint ครบ ยังไม่ push/merge

## ขอบเขต

หลักฐานการเงินส่วนตัว ตรวจสิทธิ์ก่อนอ่านและเชื่อมรายการ ตรวจชนิด/ขนาด/เนื้อหาไฟล์ ย้ายที่เก็บแบบคัดลอก–ตรวจสอบ–สลับตำแหน่ง และรายงานไฟล์ตกค้างโดยไม่ลบอัตโนมัติ ไม่รวมการจ่ายเงินจริงหรือ API/UI ของ Task ถัดไป

ฐาน `ce88e044fb3e5b819420d09ba7eaf3a69f16d2d3` branch `codex/module-7b-design` ใช้ผู้ลงมือ Popper ต่อจากการค้นคว้าของ Rawls และผู้ตรวจอิสระหลังโค้ดพร้อม ตามกระบวนการ Superpowers/TDD

## หลักฐานปัจจุบัน

- Baseline โครงสร้างข้อมูลเดิมผ่าน 26/26 ไม่มี failed/skipped ใช้เวลา 17 วินาที exit 0: `/tmp/tpr10-7c-task5-baseline.log`
- มีหลักฐาน RED/GREEN ผู้ตรวจอิสระ และ Test/Build/Format/Lint ชุดรวมหลังแก้ครบ ผล baseline ไม่ได้นำมาแทนผลหลังแก้
- ผลเฉพาะส่วนก่อน review ผ่าน 69/69 ไม่มี failed/skipped (`/tmp/tpr10-7c-task5-focused-final.log`) รวมตัวตรวจ PDF และ fixture ทรัพยากรจริง ยังไม่ใช่ผลชุดรวม
- ชุดรวมรอบแรกพบ OpenAPI HTTP 500 หนึ่งกรณีและ Format ไม่ผ่าน จึงหยุดรอบนั้น ไม่อ้างผ่าน สาเหตุ 500 ยังไม่ยืนยัน; เทสต์เจาะ Identity 58/58 และ OpenAPI 138/138 ผ่าน ไม่ถือว่าปัญหาถูกแก้แล้ว
- ผู้ตรวจ Leibniz พบ Important 2 ข้อ: unknown PDF content operator และ late-create cleanup ทั้งสองข้อแก้แล้วด้วย regression และผู้ตรวจยืนยัน ADDRESSED รอบ 1 ไม่มีปัญหาใหม่ใน diff รอบแก้ (`task-5-review-round1.md` ใน SDD workspace)
- หลังแก้ focused ผ่าน 74/74 ไม่มี failed/skipped ใช้เวลา 1m20s; worker image rebuild, solution Build, Format และ diff check ผ่านทั้งหมด (`/tmp/tpr10-task5-r1-final-summary.jsonl`)
- ชุดรวมหลัง review (`/tmp/tpr10-task5-approved-G-*.log`) ผ่านทั้ง 7 gate บนโค้ด 24 ไฟล์ ตรวจ SHA-256 ของ working tree และเนื้อหาใน commit ตรงกับรอบแก้ที่ผู้ตรวจยืนยันทุกไฟล์ ไม่พบไฟล์ขาดหรือไฟล์นอกขอบเขตใน commit
- การรองรับ PDF ต้องมี parser ตรวจโครงสร้างและตรวจ license ไม่ใช้เฉพาะ magic bytes อ้างว่าปลอดภัย

## ผล Verification หลัง review

ตรวจวันที่ 25 ตุลาคม 2569 เวลาไทย ชุด Backend เริ่ม 00:00:57 จบ 01:12:27 และ Format จบ 01:12:43 ทุก gate มี `source_unchanged: true` ใน summary

| คำสั่ง | ผล | exit |
|---|---|---|
| `dotnet build backend/TPR10.sln --no-restore` | ผ่าน 0 warnings / 0 errors | 0 |
| `dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'` | 2,815 ผ่าน ไม่มี failed/skipped | 0 |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | ผ่าน | 0 |
| `npm test` | 155 ผ่าน ไม่มี failed/skipped/cancelled | 0 |
| `npm run build` | ผ่าน | 0 |
| `npm run lint` | ผ่าน | 0 |
| `git diff --check` | ผ่าน | 0 |

หลักฐานคำสั่ง/เวลา/exit อยู่ใน `/tmp/tpr10-task5-approved-G-backend-summary.jsonl` และ `/tmp/tpr10-task5-approved-G-frontend-summary.jsonl` ภายในเครื่อง ไม่ใช่หลักฐาน NAS หรือมือถือจริง Worker image ที่ทดสอบคือ `sha256:d6a4cceb1313830fb8a65f67537edcc9dbbebcd4cdb8bf00a93d66f5e8483149` ไม่มีการเปลี่ยน image ระหว่าง verification

## ข้อตกลงทางเทคนิคระหว่างลงมือ

ตารางตำแหน่งเดิมมี `StorageKey` แต่ไม่มีคอลัมน์ storage ID แยก จึงใช้รูปแบบ `v1:<storage-guid-N>:<object-guid-N>` ที่ตรวจรูปแบบอย่างเคร่งครัด และจับคู่ storage ID กับ root ที่อนุญาตใน configuration ไม่บันทึก absolute path หรือชื่อไฟล์จากผู้ใช้ ไม่เพิ่ม migration ใน Task นี้ การย้ายต้องตรวจตำแหน่งปัจจุบันอีกครั้งก่อนสลับ และคงทั้งแถวเดิมและไฟล์เดิมไว้

## ข้อจำกัดที่คงไว้

- M2-R1/R2-C1, HTTPS 504 และ Minor เรื่อง log ยังติดตามตามเดิม
- OpenAPI HTTP 500 เกิดหนึ่งครั้งในรอบแรก ยังไม่มี root cause และไม่อ้างว่าแก้แล้ว รอบ focused และชุดรวมหลัง review ไม่เกิดซ้ำ ให้เก็บ log เหตุการณ์จริงหากเกิดอีก ไม่ถือเป็นปัญหาเดียวกับ HTTPS 504 โดยไม่มีหลักฐาน
- พักมือถือจริง/NAS จริง การทดสอบ local folder หรือจำลอง NAS offline ไม่ใช่ผลทดสอบอุปกรณ์จริง
- ไม่เริ่ม Task 6–15 ไม่ push/merge/deploy หรือย้ายฐานข้อมูลใช้งานจริง
- รักษาไฟล์งานค้างเดิมของรายงาน 7B, HTTPS 504 และ E2E ไม่รวมใน commit นี้

## การอนุมัติและขอบเขตตัวตรวจ PDF

ผู้ลงมือตรวจ PdfPig 0.1.16 (Apache-2.0) จาก source tag ทางการ พบว่า `XrefStreamParser.GetObjectNumbers` สร้างรายการตาม `/Size` หรือ `/Index` ก่อนการตรวจหลังเปิดไฟล์ และ Flate decoder ไม่มีขอบเขตผลคลายข้อมูลในเส้นทางที่ตรวจ จึงยังยืนยันไม่ได้ว่าการจำกัดไฟล์อัปโหลด 10 MiB จะจำกัดทรัพยากรระหว่าง parse ได้เพียงพอ นี่เป็นความเสี่ยงจากการอ่านโค้ด ไม่ใช่การยืนยันช่องโหว่ที่ทดลองโจมตีแล้ว

ผู้ใช้อนุมัติเพิ่มตัวตรวจ PDF แยก process พร้อมขอบเขตหน่วยความจำและเวลาแล้ว รายละเอียดการทำงานอยู่ในส่วนเพิ่มของ Task 5: container แยก ไม่มีเครือข่าย จำกัดทรัพยากรและผลลัพธ์ มี deadline และ cleanup เฉพาะงาน หากตรวจไม่ได้ให้ปฏิเสธ ไม่มี fallback ตรวจใน API ต้องทดสอบการบังคับขอบเขตจริงก่อนอ้างผลสำเร็จ

แหล่งตรวจ: https://github.com/UglyToad/PdfPig/blob/v0.1.16/src/UglyToad.PdfPig/Parser/FileStructure/XrefStreamParser.cs และ https://github.com/UglyToad/PdfPig/blob/v0.1.16/LICENSE

Popper ลงมือจากฐาน `ce88e04` ต่อจากงานค้นคว้าของ Rawls และ Leibniz ตรวจแยกคน รอบแก้เพิ่ม RED ที่ล้มเหลวจริง 4 กรณี ก่อน GREEN 5 กรณีเฉพาะ regression และชุดเฉพาะรวม 74 กรณี การตรวจรอบแก้ปิดทั้งสอง finding แล้ว ไม่มีการใช้ baseline แทนผลหลังแก้

## รายการตรวจรับที่ต้องมีหลักฐาน

ตัวตรวจ PDF ต้องมี Docker runtime ที่รองรับการบังคับขอบเขตทรัพยากรและ image ที่สร้างไว้ล่วงหน้า การเรียก Docker จาก host เป็นขอบเขตสิทธิ์สูง จึงต้องจัดผู้ปฏิบัติและเครื่องรันที่เชื่อถือได้ ไม่ mount Docker socket เข้า worker และไม่เปิด endpoint สั่งรัน image ตามข้อมูลผู้ใช้ งานนี้ยังไม่ได้ติดตั้งหรือเปลี่ยนสิทธิ์เครื่อง production

การตั้ง memory อย่างเดียวไม่ได้ปิด swap ต้องกำหนด memory+swap ให้เท่ากันตาม [เอกสารขอบเขตทรัพยากรของ Docker](https://docs.docker.com/engine/containers/resource_constraints) และต้องมีผลทดสอบจริงเพิ่มจากการตรวจ configuration

| ขอบเขต | หลักฐานที่ต้องได้ | สถานะ |
|---|---|---|
| รูป/PDF และชนิดไฟล์ปลอม | ไฟล์ปกติผ่าน ไฟล์ผิดโครงสร้างหรือเนื้อหาต้องห้ามถูกปฏิเสธ | ผ่าน focused และ review ภายใน subset ที่ระบุ |
| ขอบเขต PDF worker | memory/time/cancellation จริง; unknown create คง ownership และปิดรับงานจนสะสาง | ผ่าน focused และ review มีข้อจำกัด lifecycle ตามคู่มือ |
| สิทธิ์อ่านและผูกหลักฐาน | ถอนสิทธิ์/ข้ามคำขอ/ID ซ้ำไม่ผ่าน | ผ่าน focused และ review |
| ที่เก็บและการย้าย | digest ตรวจจริง สลับแบบ atomic คง ID และต้นฉบับ | ผ่าน focused และ review |
| ข้อผิดพลาด | ไฟล์หาย/commit ล้มเหลว/NAS จำลอง offline ไม่ทำลายประวัติ | ผ่าน focused และ review |
| Verification | Test, Build, Format, Lint และ diff check ครบ | ผ่านครบ 7 gate |
| Code review | ผู้ตรวจคนละคน ตรวจ spec และคุณภาพ พร้อมตรวจรอบแก้ | ผ่าน ไม่มี Important/Critical ค้างในขอบเขต Task 5 |

ข้อจำกัดใช้งานที่ต้องอ่าน: PDF เป็น subset ที่ตรวจได้ ไม่ใช่ทุก PDF; ที่เก็บรองรับ Linux/macOS และปฏิเสธ platform ที่ไม่รองรับ; pending container ownership อยู่ใน process ต้องเก็บชื่อและ reconcile ก่อน recycle; HTTP attachment headers จะตรวจใน Task 12 เมื่อมี route จริง ดู [คู่มือหลักฐานการเงิน](../runbooks/module-7c-finance-evidence.md)
