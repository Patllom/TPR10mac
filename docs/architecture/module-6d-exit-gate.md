# Module 6D — รายงานรับงานรวม Task 8

สถานะ 18 ตุลาคม 2026: **ผ่านเกณฑ์รับงานทางเทคนิคในเครื่องของ Task 8** ผู้ใช้อนุมัติขอบเขตเพิ่มเติมทั้งสามข้อแล้ว แก้ Important 5 ข้อพร้อม regression; backend1,677/1,677, Node74/74 และ HTTPS dev91/91 + production-build93/93 ผ่านครบ ไม่มี skip ใช้หลบ acceptance ยังไม่push/merge และยังไม่อนุมัติ Production

ฐานที่ผู้ตรวจอิสระตรวจคือ `dc07cba..25ecb42` บนสาขา `codex/module-6d-corrections` Tasks1–7 มี commit แล้ว รายงานนี้รวมการกู้คืนและการแก้ข้อค้นพบใน Task8 ที่ตรวจ RED→GREEN และชุดเต็มภายหลัง ไม่อ้างว่าผู้ตรวจอิสระได้ตรวจ diff หลังแก้ซ้ำ

## สิ่งที่ตรวจแล้วใน Task 8

| รายการ | ผลรอบนี้ |
| --- | --- |
| กู้คืนคำร้อง/เวลา/รูปสังเคราะห์ | ผ่าน 1/1 ด้วย PostgreSQL และไฟล์ local จริง |
| Node tests | ผ่าน 74/74 ไม่มีข้าม |
| Backend Build | ผ่าน ไม่มี warning/error |
| Backend format verify | ผ่าน |
| Frontend Lint / production Build | ผ่าน |
| Backend กลุ่มคำร้อง | 196กรณีอยู่ในชุดเต็ม รวม positive regression ของการส่งกลับวันเป้าหมายเดิม |
| HTTPS หน้าคำร้อง dev4000 | ผ่าน 19/19 รวมเศษวินาทีและ late response ข้ามบัญชี |
| Backend ทั้งโครงการ | ผ่าน1,677/1,677 ครบ inventory ไม่มีข้าม; รวมสามshards419กรณีและshard420กรณีที่ตรวจใหม่หลังแก้รายการ API โดย binary ระบบไม่เปลี่ยน |
| HTTPS matrix ทั้ง dev และ production-build | ผ่าน184/184 จาก15ชุด: dev91 และ production-build93 รวมชุดขอบเขตสิทธิ์เพิ่มเติม2กรณีฝั่งproduction |
| ตรวจทั้งสาขาโดยผู้ตรวจอิสระ | พบ Important5ข้อ ไม่มี Critical ที่ยืนยันได้ ไม่มี Minor ค้าง; ผู้ทำหลักแก้ด้วย TDD และชุดเต็มผ่านแล้ว |

หลักฐานรอบนี้เป็น log ชั่วคราวใน `/private/tmp` ไม่อยู่ใน Git: `tpr10-task8-backend-final-results.json`, `tpr10-task8-matrix-complete-results.json`, `tpr10-task8-build-inventory-fix.log`, `tpr10-task8-format-inventory-fix.log`, `tpr10-task8-node-after-fixture.log`, `tpr10-task8-lint-after-fixture.log`, `tpr10-task8-production-final-build.log` รายการผลรวมชี้ log ทุก shard/suite และเก็บรอบล้มเหลวแยก ไม่เขียนทับ

### การกู้คืนพิสูจน์อะไร

`CorrectionRecoveryTests.Restore_preserves_original_evidence_revisions_two_approvers_and_effective_times` สร้างคู่ลงเวลาและภาพสังเคราะห์ผ่านบริการจริง ส่งคำร้องแล้วส่งกลับ แก้ฉบับใหม่ ผ่านหัวหน้าและ HR ก่อน `pg_dump`/`pg_restore` ไปฐานข้อมูลทดสอบใหม่ ตรวจว่า:

- ฐานข้อมูลอย่างเดียวเมื่อไม่มีไฟล์รูปอ่านไม่ได้ (503); คืนไฟล์แล้ว checksum และ full/thumbnail/download ทั้งเข้า–ออกตรงเดิม
- original events ไม่เปลี่ยน, effective times ตรงค่าที่อนุมัติ, adjustment ชี้วัน/คำร้อง/ฉบับถูกต้อง
- เก็บสอง revisions, สอง route snapshots, เหตุผลส่งกลับและผู้อนุมัติทั้งสองคนครบ
- เจ้าของอ่านได้แบบ no-store; anonymous ถูกปฏิเสธ 401

เป็น acceptance characterization ของระบบเดิม ไม่ใช่ RED ของ feature ใหม่ รอบแรกติด xUnit2031 และรอบถัดมา assertion ใช้ชื่อขั้นผิดตัวพิมพ์ แก้ assertion เป็น `Supervisor`/`Hr` ให้ตรงสัญญาจริงแล้วผ่าน ไม่แก้ระบบเพื่อให้เข้ากับ test ที่ผิด ไม่ใช่การทดสอบ process-kill, deployment ข้ามเครื่อง หรือ NAS จริง

## ข้อค้นพบจาก Code Review และผลแก้

ผู้ตรวจอิสระ Tesla ตรวจทั้งสาขาโดยไม่แก้ไฟล์ ผู้ทำหลักตรวจเทียบ code paths และ Spec แล้ว คงระดับ Important ทั้ง5ข้อ จากนั้นทำ reproduction และแก้ด้วย regression RED→GREEN ตามตารางด้านล่าง โดยไม่ส่งตรวจทั้งสาขาซ้ำ ผลชุดเต็มผ่านแล้วตามตารางหลักฐาน ไม่ใช้เพียงผลเฉพาะกลุ่ม

ผู้ใช้อนุมัติขอบเขตเพิ่มเติมแล้ว ผลแก้ล่าสุดคือ:

| ข้อ | หลักฐานก่อนแก้ | ผลแก้และขอบเขต |
| --- | --- | --- |
| 1 | เปลี่ยนวันไปเรื่องหมดอายุและวันที่พ้นกำหนดได้ 200 แทน409/400 | ตรวจประวัติวันเป้าหมายรวม revisions ใน transaction เดิม และตรวจ deadline ตอน save/submit; final approval ตรวจ conflict อีกครั้ง ไม่ต่ออายุเพราะ save; วันเดิมที่เคยส่งแล้วใช้รอบ Returned เดิมได้ |
| 2 | HR อนุมัติเปิดรอบที่มี CheckOut สดได้200แทน409 | ปฏิเสธใน transaction ก่อนปรับ effective day; ไม่มี adjustment ค้าง หลักฐานไม่เปลี่ยน HR ส่งกลับให้ระบุเวลาออกได้ ยังแก้ค่าเวลาออกแบบไม่ลบทิ้งได้ |
| 3 | คิวจัดเส้นทางได้400 | เพิ่ม `stage=routing` และ `WorkflowRouting` เฉพาะ pending ภายใต้ directory-manage/MFA/current HR unit จำกัดก่อนนับ/แบ่งหน้า; ไม่เพิ่มรูป/GPSหรือประวัติทั่วไป |
| 4 | GET ตัวเลือก Site ย้อนหลังได้404; UI ไม่มีปุ่ม | เพิ่ม `/corrections/sites?at=...&page=1&pageSize=100` เฉพาะเจ้าของที่มี CanRecord ปัจจุบัน ตรวจ retained assignment และ membership ที่เวลานั้น; UI ตรวจบัญชีหลังรับข้อมูลและทิ้ง late response |
| 5 | แก้เหตุผลแล้ววินาทีหาย; แม้แก้ขั้นแรกยังสูญ microseconds | คง timestamp string เดิมเมื่อช่องเวลาไม่ได้เปลี่ยน ทั้ง payload และเวลาที่ส่งค้น Site; browser regression ใช้เศษวินาที6หลักผ่าน |

กรณีทดสอบ `admin-only` เดิมยังคง role จัด directory ที่ตอนนี้ได้รับอนุมัติให้จัดเส้นทางได้ จึงปรับ fixture ให้ถอนทั้งสิทธิ์ HR approval และ directory โดยยังยืนยัน404 ไม่ผ่อน assertion; positive test แยกพิสูจน์ routing grant และ negative tests ตรวจ MFA/หน่วยงาน/capability

วิธีใช้สถานที่ย้อนหลัง: กรอกเวลาเข้า–ออก แล้วกด **โหลดสถานที่ตามเวลาที่เสนอ** ก่อนเลือก Site เข้า/ออก ชื่อ Site ที่แสดงใช้ชื่อปัจจุบันจากทะเบียน ไม่อ้างว่าเป็นชื่อ ณ วันเกิดเหตุ การเลือกไม่ได้แทนการตรวจสิทธิ์ฝั่ง API

จุดอ้างอิง: `CorrectionDraftService.cs:61`, `CorrectionAdjustmentWriter.cs:62`, `RecordingModelConfiguration.cs:33`, `CorrectionQueries.cs:141`, `CorrectionPortal.tsx:61`, `CorrectionForm.tsx:5,19` ณฐาน `25ecb42`

แผน Task 8 เดิมระบุไม่เพิ่ม API ธุรกิจใหม่ จึงเคยหยุดขออนุมัติส่วนนี้ตามกติกา `executing-plans` และ `receiving-code-review` ผู้ใช้อนุมัติแนวทางทั้งสามข้อแล้ว จึงดำเนินการตามขอบเขตจำกัดข้างต้น ไม่ต้องขออนุมัติซ้ำ

## ขั้นถัดไป

1. เก็บงาน6Dบนสาขาปัจจุบัน ให้ผู้ใช้เลือกว่าจะ push/เปิดPR หรือเก็บสาขาไว้ก่อน ไม่mergeอัตโนมัติ
2. ทดลอง workflow ด้วยบัญชีทดสอบ: พนักงานส่งคำร้อง → หัวหน้าส่งกลับ/อนุมัติ → HRอนุมัติ และทดสอบคิวจัดเส้นทางตามสิทธิ์
3. เปิดตรวจมือถือจริง/NAS และข้อกำหนด Production เมื่อพร้อม รายการเหล่านี้ยังไม่ผ่านจากผลในเครื่อง

## ผลล้มเหลวระหว่างตรวจชุดเต็ม

- `IdentityOpenApiTests.Enumerates_every_versioned_operation_and_cookie_flags` ขาด GET `/api/v1/attendance/corrections/sites` ในรายการที่คาดหวัง แก้ literal ให้ตรง endpoint ที่อนุมัติ โดยยังเปรียบเทียบรายการทั้งหมดแบบ exact ไม่ผ่อนการตรวจ cookie/security; เก็บ log เดิมและรัน shard ที่เกี่ยวข้องใหม่ก่อนสรุป
- HTTPS dev capture รอบแรกผ่าน26/27 แต่ test ยังค้นคำว่า `Module 6D` จากข้อความเดิมที่ระบุยังไม่เปิดใช้งาน Task7 เปลี่ยนข้อความเป็นให้ไปเมนูคำร้องแล้ว จึงแก้ assertion ให้ตรวจข้อความปัจจุบันแบบ exact และคงขั้นตอนยืนยันข้ามวัน/ห้าม offline เดิมครบ รอบถัดมาผ่าน27/27 ไม่มีเพิ่ม retry หรือ timeout
- HTTPS production capture รอบแรกหลังแก้ข้อความผ่าน26/27 แต่ `silent account switch before challenge` timeout ก่อนคลิกกล้อง เพราะ fake session ฝั่ง browser ขัดกับ cookie ที่ SSR อ่านจริง ทดสอบชั่วคราว dispatch focus พิสูจน์ reloadอย่างน้อย2ครั้งได้ แล้วถอด diagnostic ออกและย้ายจังหวะ fake account ไปขณะตอบ challenge ก่อนตรวจ sameAccount/เปิดกล้อง กรณีถาวรยังตรวจข้อความบัญชีเปลี่ยนและ camera tracks เป็นศูนย์ ไม่แก้ authentication จริงและไม่ผ่อน timeout
- ตัวรันชั่วคราวเรียก dev ซ้ำหลัง production แล้วลืม build ใหม่ก่อนชุด history ทำให้ `.next/BUILD_ID` หายและ Next เริ่มไม่ได้ก่อนเริ่ม browser tests แก้ลำดับโดย build ใหม่ก่อนตรวจสองชุดสุดท้าย เป็นความผิดพลาดของการจัดลำดับการตรวจ ไม่ใช้เป็นหลักฐานว่าแอปล้มเหลวหรือว่าต้องเพิ่ม timeout
- ไม่ถือรอบแรกทั้งสองรายการว่าผ่าน ผลสุดท้ายต้องรวม coverage ครบจากชุดที่ไม่ได้รับผลกระทบและชุดที่แก้ assertion แล้ว โดยไม่เปลี่ยนโค้ดระบบระหว่างรัน

## การตัดสินใจประกอบการส่งมอบ

- เปลี่ยนวันได้โดยยึด effective time แต่ไม่เปลี่ยน snapshot/original event ใช้ transaction marker ที่ trigger ควบคุมแทน `xmin` เพื่อรองรับ EF savepoint และใช้ employee lock ตอนบริการอนุมัติ หากผิดจะกระทบ atomicity จึงตรวจทั้ง SQL และบริการจริง
- Fixture ของ schema เก่ายังคง assertion หลักฐานเดิม ปรับเฉพาะ seed ให้มี live event จริง/pin migration และยกเว้นคอลัมน์ transaction marker ใหม่ ไม่ปิดข้อบังคับระบบเพื่อให้ทดสอบผ่าน
- Historical Site ใช้ช่วง assignment ที่เก็บจริง แยก current CanRecord ไม่สมมติว่ามีประวัติ capability; anchor ของคำร้องใหม่ยึดวันเสนอแรก และตรวจ deadline อีกครั้งตอน submit หากต้องการประวัติ capability ต้องออกแบบเพิ่มภายหลัง
- Route ที่หาไม่ได้ยังเป็น pending เดิม ไม่เพิ่มทางข้ามสถานะ เก็บเหตุผล reroute เต็มใน immutable receipt และอ่านผ่านสิทธิ์รายละเอียด หากเปลี่ยนโครงเก็บภายหลังต้องคงประวัติทั้งหมด
- ใช้ approved adjustment เป็นที่มาของ correction-only day แทนการสร้างภาพ/event ย้อนหลังปลอม เพิ่ม migration ใหม่ไม่แก้ migration เก่า และทดสอบ checkout จริงกับ rollback
- Workflow read จำกัดขั้น pending ที่ได้รับสิทธิ์ ส่วน history6Aไม่เปิดร่างส่วนตัว; ประวัติ revisions/routes และ comparison อยู่ในรายละเอียดเท่านั้น ไม่เพิ่มใน receipt/list รูปและ GPS ไม่ได้สิทธิ์เพิ่มตาม workflow
- หน้า history รองรับ correction-only โดยแสดงว่าไม่มีหลักฐานถ่ายสด; effective Site มาจาก adjustment ล่าสุด ยกเว้น checkout สดภายหลังใช้ Site ของ event ไม่ทำให้ original evidence เปลี่ยน
- Mutation HTTP ตอบเพียง `{id,commandId}` เพื่อไม่แถมสิทธิ์อ่านหลังอนุมัติ คง state JSON0–5ตามสัญญาเดิมและปฏิเสธค่าอื่น; เปรียบเทียบ baseline เฉพาะ version ตรง ไม่แต่งข้อมูลเดิมที่ไม่ได้เก็บ
- เพิ่ม identity fence หลัง CSRF และก่อนใช้ผลตอบกลับ เพื่อไม่ให้บัญชีใหม่รับข้อมูลบัญชีเดิม ไม่ replay mutation อัตโนมัติ
- ผล Task7 ที่เครื่องหลับถูกยกเลิกและรันครบใหม่ด้วย `caffeinate -i` ไม่แก้ระบบ authentication จากสมมติฐานเรื่องเวลา; Task8 ใช้แนวเดียวกันและนับ inventory จริงทั้งหมด
- การกู้คืนเป็นการพิสูจน์พฤติกรรมเดิมด้วย negative control ไม่อ้าง RED ของ feature ใหม่ ผู้ตรวจอิสระไม่ได้ตรวจไฟล์กู้คืนที่เพิ่มนอก HEAD ผู้ทำหลักจึงรับผิดชอบหลักฐานชุดนี้แยกต่างหาก
- คงระดับ Important ทั้ง5ข้อและขออนุมัติการขยาย API/read policy ก่อนลงมือ ผู้ใช้อนุมัติแล้ว จึงเพิ่มคิว pending ของหน่วยงานที่ได้รับสิทธิ์และ Site ย้อนหลังของตนเท่านั้น ไม่เป็น admin bypass
- Fixture `admin-only` ต้องถอน directory grant ที่ได้รับนโยบายใหม่ด้วย ยังคงคาด404และมี positive/negative ของ routing แยก เพื่อไม่ซ่อนการเพิ่มสิทธิ์โดยไม่ได้ตั้งใจ

## ข้อจำกัดและ Production

- มือถือจริงและ NAS จริงยังพักตามคำสั่ง ไม่ถือว่าผ่านจากการทดสอบ local
- timeout เดิมของ HTTPS scopes ยังไม่มี causal fix แม้รอบก่อนผ่าน ต้องคงบันทึกความเสี่ยงและตรวจ matrix ใหม่
- ผู้ตรวจเว้นการตัดสิน Task 8 recovery เพราะอยู่นอก HEAD ที่ตรวจ: ใช้หลักฐานทดสอบของผู้ทำหลักข้างต้น ไม่อ้างว่า reviewer ตรวจไฟล์ใหม่นี้แล้ว
- load, deployment restore, อุปกรณ์จริงและการรับรองจากเจ้าของข้อมูลยังไม่ตรวจครบ จึงยังไม่อนุมัติ Production
- ยังต้องแต่งตั้งผู้รับผิดชอบภาพ/GPS และอนุมัตินโยบาย retention, capacity, backup/restore; ระยะแรกยังไม่ลบหลักฐานอัตโนมัติ ไม่เก็บภาพจริงหรือข้อมูลลับใน Git
