# Module 6C — Task 4: บันทึกเวลาเข้า–ออกแบบครบถ้วนและไม่ซ้ำ

วันที่ตรวจ: 26 กันยายน 2026

สถานะ: ผ่านเกณฑ์เทคนิค Task 4 แล้ว ยังไม่ใช่การส่งมอบ Module 6C ทั้งระยะ และยังไม่ push/merge

## ขอบเขตที่เพิ่ม

- `AttendanceRecorder.RecordAsync` เชื่อม challenge/operation ของ Task 3 กับการเตรียมรูปของ 6B และข้อมูลวัน/เหตุการณ์ของ Task 1
- `AttendancePairing.WorkDate` ใช้เขตเวลา `Asia/Bangkok` นับหนึ่งคู่ต่อคนต่อวันทั่วระบบ และมีรอบเปิดได้ไม่เกินหนึ่งรอบ
- เวลาออกต้องหลังเวลาเข้า ตรวจ version ของวัน หากข้ามวันต้องยืนยันทำงานต่อเนื่อง วันของคู่ยังเป็นวันที่เข้า ไม่ตั้งเพดานชั่วโมงเพิ่ม
- ปิดคู่แล้วเปิดใหม่วันเดิมไม่ได้ เมื่อปิดคู่ข้ามวันแล้วจึงเปิดคู่ของวันปัจจุบันได้ ไม่มีช่องทางกรอกเวลาย้อนหลังหรือข้ามขั้นคำร้อง 6D
- Site ของเหตุการณ์เข้าและออกแยกกัน ต้องมีสิทธิ์ตรง Site นั้น รวมกรณีต่าง Workspace ไม่ขยายสิทธิ์จาก Site ตอนเข้า
- วันทำงานเก็บต้นสังกัดตอนเข้า ส่วนรูปเก็บต้นสังกัด ณ เหตุการณ์ตามสัญญา 6B การย้ายสังกัดระหว่างเตรียมรูปไม่เปลี่ยน snapshot ที่ตรึงไว้

## ลำดับการบันทึก

1. ตรวจผู้ใช้/session และสิทธิ์ปัจจุบัน อ่านภาพแบบจำกัดขนาดและคำนวณ SHA256 จาก bytes จริงนอก transaction
2. เปิด transaction สั้น ล็อก identity ก่อนพนักงาน ตรวจ pairing และตรึงเวลาเซิร์ฟเวอร์ระดับ microsecond, วัน, ต้นสังกัด และ operation
3. ถ้าพบ operation เดิม ตรวจ replay ก่อน pairing เพื่อให้คำขอที่สำเร็จแล้วคืน receipt เดิมได้ ไม่ถูกปฏิเสธเพราะวันถูกเปิดหรือปิดไปแล้ว
4. จองที่เก็บและเตรียมภาพประทับเวลาโดยไม่ถือ transaction ของผู้เรียก งานไฟล์และ native image processing อยู่นอก identity/employee lock
5. เปิด transaction ใหม่ ตรวจผู้ใช้/session/capability/exact Site, challenge, lease/fence, snapshot และ pairing/version อีกครั้ง
6. บันทึกวัน เหตุการณ์ การเผยแพร่รูป binding, audit, receipt และการใช้ challenge พร้อมกัน แล้วจึงตอบสำเร็จ

ไม่มี route สำหรับอัปโหลดหรือเผยแพร่หลักฐานทั่วไปเพิ่มใน Task นี้ ยังเป็นบริการภายในที่ Task 6 จะเรียกหลังตรวจ HTTP/CSRF/multipart ตามแผน

## เมื่อผิดพลาดหรือส่งซ้ำ

- คำขอใหม่สำเร็จคืน 201; คำขอเดิมที่สำเร็จแล้วคืน 200 พร้อม receipt เดิม แม้ challenge หมดอายุ
- key เดิมแต่เปลี่ยนรูปหรือ metadata ที่มีรูปแบบถูกต้องคืน 409 ไม่สร้างเหตุการณ์ซ้ำ ไม่เปลี่ยน key ให้อัตโนมัติ
- คำขอเดิมที่ยังถือ lease ให้ผล conflict ตาม Task 3; เมื่อ reclaim ได้จะคงเวลา ต้นสังกัด วัน และ evidence เดิม
- หากที่เก็บหรือ audit/ฐานข้อมูลล้มเหลว ไม่มีวัน/เหตุการณ์ที่สำเร็จครึ่งเดียว อาจเหลือรูป Reserved/Prepared ให้ reconciliation ตรวจ แต่ยังเปิดอ่าน full/thumbnail ไม่ได้และไม่ลบอัตโนมัติ
- หากรูปเตรียมเสร็จแต่ lease หมด ให้ 409; challenge หมดให้ 400 ต้องถ่ายใหม่ ไม่มีการต่ออายุเงียบ ๆ
- รูปเกินขนาดให้ 413; รูปที่ decode ไม่ได้ให้ 415; dependency ล้มเหลวให้ 503 โดยไม่เผยข้อมูลพาธภายใน

## หลักฐาน TDD และตรวจโค้ด

- RED 12 กรณีล้มเหลวเพราะยังไม่มี `AttendanceRecorder` ก่อนเพิ่ม implementation แล้ว GREEN 12/12
- ขยายเป็น 25 กรณี Task 4: หลายคำขอพร้อมกันทั้งเข้าและออก, replay หลังขาดการตอบกลับ, payload เปลี่ยน, ข้ามวัน, version เก่า, ต่าง Workspace, audit/storage failure, ย้ายสังกัดและเพิกถอนสิทธิ์ระหว่างเตรียมภาพ
- เที่ยงคืนระหว่างเตรียมภาพ: อ่าน bytes จากไฟล์จริงและเทียบภาพประทับ instant เดิม ตรวจ receipt เป็นวันเดิม ไม่ใช้เวลาหลังประมวลผลภาพ
- จำลองล้มเหลวตรง boundary ของการเตรียมและ commit ด้วย audit trigger ในฐานข้อมูลทดสอบจริง เมื่อ retry ต้องได้ evidence ID/เวลาเดิมและเผยแพร่เพียงหนึ่งครั้ง ไม่อ้างว่าเป็นการทดลอง kill process หรือ NAS จริง
- ตรวจ final deadline หลังภาพ Prepared โดยใช้ audit decorator เฉพาะ test ที่ยังเขียน audit จริง เพื่อให้พิสูจน์ด่านของ Recorder โดยตรง ไม่ใช่ผ่านเพราะ EvidenceWriter ปฏิเสธก่อน
- ตรวจโดยผู้เขียน: ลำดับ lock, ไม่มี I/O ใต้ caller transaction, current grants ก่อน commit, การ attach operation ใหม่ที่อ่านแบบไม่ติดตาม, การ rollback และการไม่เปิดเผยภาพที่ยังไม่ commit
- การตรวจทั้งสาขาโดยผู้ตรวจอิสระยังอยู่ Task 9 ตามแนวทาง Native ไม่ถือว่า Task นี้ผ่าน independent review หรือพร้อม merge

ข้อผิดพลาดของ fixture ที่แก้: ตาราง write target ไม่ได้มีแถวเริ่มต้น จึงเพิ่ม target ของที่เก็บทดสอบอย่างชัดเจน; audit decorator ต้องส่งต่อ overload เดิมของ interface ด้วย ไม่มีการผ่อน constraints ของระบบจริง

## ผลยืนยัน

| รายการ | ผล |
| --- | --- |
| Task 4 ร่วมกับ challenge/operation/schema Tasks 1–3 | ผ่าน 127/127 ไม่มี failed/skipped ใช้เวลา 50 วินาที |
| Backend ทั้งโครงการ | ผ่าน 1,417/1,417 ไม่มี failed/skipped ใช้เวลา 34.8010 นาที |
| `npm test` | ผ่าน 43/43 |
| `npm run lint` | ผ่าน |
| `npm run build` | ผ่าน |
| `dotnet build backend/TPR10.sln --no-restore` | ผ่าน 0 warning/error |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | ผ่าน |
| `git diff --check` | ผ่าน |

หลักฐานดิบในพื้นที่ทดสอบที่ Git ไม่ติดตามของแผน 6C: `task4-red.log`, `task4-focused.log`, `task4-backend-full.log` และ ledger `progress.md`

Fingerprint backend ก่อนและหลังรอบทั้งโครงการตรงกัน: `ac86e7c4c3307962c1c5f2ea1a7890a8be230a5ce51554a058126e48693c97b6` ไม่มีการแก้โค้ดระหว่างรอบยืนยัน

## การตัดสินใจและงานถัดไป

- เพิ่ม DI registration และ fixture ที่ใช้ PostgreSQL/โฟลเดอร์/ภาพจริงนอกเหนือจากรายชื่อไฟล์ใน brief เพื่อทดสอบการเชื่อมระบบจริง ต้นทุนคือ helper ที่ต้องดูแลเพิ่ม ไม่เพิ่ม dependency หรือ schema
- เพิ่ม optional DI และ audit decorator เฉพาะ test เพื่อฉีดความล้มเหลวตรง boundary โดยไม่เพิ่ม test hook ใน production
- ตรวจ replay ก่อน pairing และใช้เวลาเดิมตรวจ metadata ของ retry เพื่อรักษาสัญญา idempotency; challenge/lease ยังใช้นาฬิกาปัจจุบัน หากสัญญานี้เปลี่ยนต้องทบทวนทั้ง Task 3 และ Task 4
- ไม่แก้ checkout หลักหรือ Preview ไม่เปลี่ยนพอร์ต dev 4000 / production-build 4001 / HTTPS 4443
- ถัดไป Task 5: การอ่านรายการ/ประวัติตามสิทธิ์; Tasks 6–9 จึงเชื่อม API, หน้ากล้อง และตรวจรับรวม ยังไม่มีหน้าลงเวลาใหม่ให้ใช้จาก Task นี้
- การทดสอบมือถือจริง NAS จริง และ Production gate ยังไม่ผ่านจากงานนี้
