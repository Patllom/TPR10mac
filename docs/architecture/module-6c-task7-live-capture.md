# Module 6C Task 7 — หน้าลงเวลาด้วยกล้องสดและ GPS

วันที่ตรวจ: 26 กันยายน 2026 · ฐานงาน `f9f6ad7` · สาขา `codex/module-6c-online-attendance`

## ขอบเขต

เพิ่ม `/portal/attendance` หลัง session protection เดิม พร้อมเมนูใน Portal เฉพาะผู้มี `attendance:record` บน exact Site assignment ไม่มีสิทธิ์จาก Admin โดยอัตโนมัติ ไม่มีการแก้ backend/schema/dependency หรือ Landing Page ใน Task นี้

ผู้ใช้เลือกสถานที่ เปิดกล้อง รับ GPS ถ่ายและยืนยันเข้า–ออก ไม่รับไฟล์จากอัลบั้ม/drop/paste ไม่มี offline queue ภาพและพิกัดอยู่ใน memory เท่านั้น หน้าแสดงเวลาเซิร์ฟเวอร์ล่าสุดและระบุชัดว่าภาพตัวอย่างยังไม่ประทับเวลา การประทับจริงใช้ pipeline ฝั่งเซิร์ฟเวอร์ที่ส่งมอบก่อนหน้า

## พฤติกรรมสำคัญ

- ตรวจ challenge ก่อนถ่าย อายุสูงสุด 120 วินาที ใช้เวลานับแบบ monotonic ไม่ต่ออายุเอง
- ขอ GPS ใหม่ก่อนยืนยัน ตรวจ finite/ช่วงค่า/เวลาไม่เกิน 60 วินาที ไม่รับเวลาอนาคต ความคลาดเคลื่อนเกิน 100 เมตรแสดงคำเตือนแต่ไม่บล็อก
- หนึ่งรอบเปิดและหนึ่งคู่ต่อวันอิงสถานะจาก API; checkout ส่ง version ของรอบเดิม ข้ามวันต้องยืนยันทำงานต่อเนื่อง ไม่เปิดให้แก้ย้อนหลัง
- ลืมลงเวลาแสดงคำอธิบายรอ Module 6D ไม่มีปุ่มคำร้องที่ใช้ไม่ได้หรือช่องแก้ตรง
- ขณะส่งปิดการกดซ้ำ เมื่อไม่ทราบผลเก็บเฉพาะ key/action/ขอบเวลาใน memory แล้วอ่าน operation เดิม ไม่ส่ง mutation ซ้ำและไม่เปลี่ยน key อัตโนมัติ
- กรณีไม่พบ operation ต้องพ้นขอบอายุ challenge แบบเผื่อเวลาตอบกลับก่อน แล้วจึงโหลด current ใหม่และอนุญาตถ่ายใหม่ ไม่ถือว่า 404 ที่มาก่อนหมดอายุแปลว่าคำขอเดิมจะไม่ commit
- ซ่อนแท็บ/ออกจากหน้า/เปลี่ยนบัญชี: ยกเลิกคำขอ หยุด tracks และ revoke object URL ล้างภาพ/GPS; กลับแท็บตรวจ session/current/assignments ใหม่ ผลที่ตอบหลัง generation เปลี่ยนไม่แสดง
- ใช้ parser เฉพาะข้อมูลที่หน้าลงเวลาต้องใช้ ดึงจาก current เพียงรหัสรอบ/version/วันทำงาน ไม่เก็บประวัติรูปหรือ GPS ใน state ของหน้า

## การตรวจโค้ดและข้อวินิจฉัย

ใช้กระบวนการ Superpowers แบบ Native inline และตรวจโดยผู้เขียน ไม่ใช่ independent review ทั้งสาขา ซึ่งยังเป็น Task 9

1. เพิ่ม `capture-view.ts` และ unit tests นอกบัญชีไฟล์ย่อใน brief เพื่อแยกการตรวจ DTO/GPS ออกจาก UI ต้นทุนคือมี parser เฉพาะที่ต้องดูแลเพิ่ม ไม่แทน parser ประวัติ Task 8
2. เมนูต้องอ่าน exact Site assignments ไม่ใช่ `session.permissions` เพราะรายการนั้นมีเฉพาะ system-domain permissions ต้นทุนคือ Portal ต้องอ่าน discovery เพิ่มและซ่อนเมนูเมื่อบริการนี้ล่ม
3. Browser tests ใช้ HTTPS/Next/session/CSRF/scope จริงในระบบแยก แต่กล้อง/GPS เป็นข้อมูลสังเคราะห์ และควบคุมคำตอบ HTTP ของ recording/current เพื่อทดสอบเวลาและความผิดพลาด ต้นทุนคือไม่ใช่หลักฐานกล้องมือถือหรือการบันทึกภาพครบสายผ่าน UI จริง
4. รัน frontend suite ทั้งหมดและ backend regression ของ API/history/CSRF ที่เกี่ยวข้อง ไม่รัน backend ทั้งโครงการใหม่เพราะไม่มีการเปลี่ยน backend ต้นทุนคือไม่ยืนยัน regression ที่ไม่เกี่ยวข้องในรอบนี้ ผล 1,478 tests ของ Task 6 เป็นหลักฐานเก่า ไม่ใช่ผล Task 7
5. พบกรณี uncertain ค้างเมื่อคำขอไม่เคยถึง server ทำ browser regression ให้ล้มเหลวก่อนแก้ ใช้ขอบอายุ challenge จากเวลารับ response พร้อมเผื่อหนึ่งวินาทีและ current revalidation ไม่อาศัยการรออย่างเดียวแล้วสรุปสำเร็จ
6. พบว่า `video.play()` อาจรอไม่สิ้นสุดแม้ได้ stream แล้ว ทำ RED ที่ไม่แสดง error ภายใน 13 วินาทีก่อนเพิ่ม deadline 10 วินาที เมื่อหมดเวลาหยุด tracks และให้เริ่มใหม่ ไม่ปล่อยกล้องค้าง

## หลักฐานการทดสอบ

ผลตรวจโค้ดก่อน commit วันที่ 26 กันยายน 2026 เวลา 15:23 UTC:

| ชุดตรวจ | ผล |
| --- | --- |
| `npm test` | 53/53, ไม่ fail/skip, exit 0 |
| HTTPS browser capture บน dev4000 | 19/19, ไม่ fail/skip, exit 0; รวม TLS/cookie/CSRF/Host acceptance จริง |
| Backend API/OpenAPI/history/delivery/identity/CSRF ที่เกี่ยวข้อง | 173/173, ไม่ fail/skip, exit 0 |
| `npm run lint` และ `npm run build` | exit 0 |
| `dotnet build backend/TPR10.sln --no-restore` | exit 0, ไม่มี warning/error |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | exit 0 |
| `git diff --check` | exit 0 |

Logs ใน workspace ของแผน: `task7-node-final.log`, `task7-lint-final.log`, `task7-build-final.log`, `task7-browser-release.log`, `task7-backend-regression.log`, `task7-backend-build.log`; ผลตรวจหลัง commit จะอยู่ `task-7-tests.log` และ completion line ใน ledger ไม่ใช้การตรวจนี้แทน full backend/dev+production/hardware gate ของ Task 9

ตรวจการแยก state/epoch, cancellation, byte/GPS lifetime, Site capability, pairing/overnight, malformed response, timeout/uncertain และไม่มี browser storage แล้ว ข้อพบสำคัญสองจุดมี regression RED→GREEN ข้างต้น ไม่มีข้อพบสำคัญที่ทราบและยังไม่แก้ในขอบเขต Task นี้ การประเมินนี้เป็น author review ไม่ใช่การอนุมัติ merge ทั้งสาขา

เริ่มด้วย browser RED 9 กรณีเมื่อหน้ายังไม่มี และ unit RED 4 กรณีเมื่อ parser ยังไม่มี จากนั้นเพิ่มกรณี lifecycle/late response ในรอบตรวจขยาย ไม่อ้างว่าทุกกรณีที่เพิ่มภายหลังมี RED แยกกันทั้งหมด

ข้อผิดพลาดของตัวทดสอบที่แก้: locator จับ Next route announcer ร่วมกับ alert ของหน้า, การสร้าง DragEvent ต้องใช้ DataTransfer จริง, locator สำเร็จจับทั้ง GPS/กำลังส่ง และ mock camera ต้องอนุญาตเปลี่ยน descriptor สำหรับกรณีตอบช้า ไม่ผ่อนเงื่อนไข production เพื่อให้ผ่าน

## ข้อจำกัดและงานถัดไป

หน้าประวัติส่วนตัว/หัวหน้า/HR เป็น Task 8; ตรวจทั้งสาขาและมือถือจริง Safari/iOS, Chrome/Android รวม NAS/Production เป็น Task 9 และ gate แยก ไม่มีการรับรองว่าเว็บป้องกัน virtual camera หรือ GPS spoofing ได้

ผู้ใช้อนุญาตหยุด Preview เดิมชั่วคราวเพื่อใช้พอร์ต 4000/4443 กับ fixture ต้องเปิด Preview จาก checkout เดิมกลับหลังทดสอบ ไม่สลับ Preview ไปสาขาใหม่เอง ไม่ push/merge ใน Task นี้
