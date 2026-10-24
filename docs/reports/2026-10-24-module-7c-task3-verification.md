# รายงานตรวจสอบ Module 7C — Task 3

วันที่เริ่ม: 24 ตุลาคม 2569

สถานะ: Task 3 ผ่านการตรวจรับ — เฉพาะธุรกรรมและบัญชีกันวงเงิน ไม่ใช่ Module 7C ทั้งหมด

## ขอบเขต

ธุรกรรมคำสั่งการเงิน การป้องกันคำสั่งซ้ำ และบัญชีกัน/คืน/ใช้วงเงิน ไม่รวม workflow งวดจ่ายหรือ API ของ Task ถัดไป ใช้ฐานโค้ด `5d18c13439d48690899b65874a8cddb8393a5c88` บน branch `codex/module-7b-design` แยกผู้ลงมือ Heisenberg กับผู้ตรวจอิสระ Nash

## หลักฐานปัจจุบัน

- Baseline สิทธิ์การเงินผ่าน 50/50 ไม่มี failed/skip ใช้เวลา 44 วินาที exit 0: `/tmp/tpr10-7c-task3-baseline.log` เป็นผลก่อนแก้ ไม่ใช่ผลรับรอง Task 3
- RED แรก 8 กรณีล้มเหลวตามพฤติกรรมที่ยังไม่มี; GREEN สุดท้ายผ่าน 23/23 ไม่มี failed/skip: `/tmp/tpr10-7c-task3-GREEN-final.log` รายละเอียดการทดลองรอรายงานผู้ลงมือ
- ผู้ตรวจอิสระ Nash อนุมัติทั้งตามแบบและคุณภาพ ช่วง `5d18c13..f9066d3` ไม่พบ Critical/Important
- Full G ครบทุก gate exit 0: Backend 2,689/2,689; Frontend 155/155; ไม่มี failed/skip; Backend Build ไม่มี warning/error; Format, Frontend Build และ Lint ผ่าน

Full G เริ่ม 12:03:50 UTC จบ 13:14:14 UTC วันที่ 24 ตุลาคม 2569 Backend ใช้เวลา 4,195 วินาที หลักฐาน `/tmp/tpr10-7c-task3-G-summary.log` และ log แยกตามคำสั่ง จำนวน Frontend อ่านจาก raw log เพราะ summary parser ยังไม่รองรับสัญลักษณ์ผลลัพธ์ Node 24

Commit โค้ด `f9066d3b1da3f309de2419b1b7e89c8437e3bb1b` มีเฉพาะ executor, ledger และเทสต์ รวม 3 ไฟล์ ตรวจ checksum ตรงกับ source ที่รัน Full G ทุกไฟล์ ผล Full G นี้เกิดก่อนการตรวจอิสระแบบอ่านอย่างเดียว หากแก้โค้ดจาก review ต้องรันใหม่

## สิ่งที่เพิ่มและข้อจำกัดการเชื่อมต่อ

- ล็อกและธุรกรรมร่วม ตรวจสิทธิ์ปัจจุบันก่อน replay และก่อน commit; ล้าง tracked state ทั้งสำเร็จและล้มเหลว
- กัน/คืน/ใช้วงเงินแบบ append-only ตรวจยอดงวดและยอดรวม ไม่ให้ consume ซ้ำเพิ่มยอดจ่าย
- บันทึก participation, ยอดเงิน, ledger, event, audit และ receipt ในธุรกรรมเดียวกัน รวมป้องกัน body จับ ledger error แล้วกลับมาบันทึกบางส่วน
- ผูก digest กับผู้ลงมือ เป้าหมาย action และ command metadata; ผู้เรียกใน Task ถัดไปต้อง hash input ทั้งก้อนรวมหลักฐาน/บัญชี และควบคุม workflow ของตน
- ไม่ต้องแก้ RequestCommandExecutor เพิ่ม เพราะ Task 2 ใช้ shared lock helper แล้ว ไม่มี endpoint หรือ workflow จ่ายเงินจริงใหม่ใน Task 3

## การทดลองที่ไม่ผ่านก่อนแก้

- Compile errors สองรอบแก้ก่อนนับ RED เชิงพฤติกรรม
- GREEN รอบแรก 7 ผ่าน/1 ไม่ผ่าน: fixture ใช้ผู้อนุมัติทดสอบ actor mismatch จึงถูกปฏิเสธจากการแยกหน้าที่ก่อนถึง receipt แก้ fixture เป็นบัญชีอีกบัญชีของผู้จ่ายที่มี grant เอง
- RED เพิ่มกรณีจับ ledger exception แล้วคืน success และกรณี receipt ไม่รวม hold ที่ body เพิ่งเพิ่ม แก้แล้ว focused สุดท้ายผ่าน 23/23 และ Full G ผ่าน

## รายการคงค้าง

- Minor ใหม่ `7C-T3-M1`: เพิ่มเทสต์สร้างงวดใหม่ด้วย `installmentId=null` ตรวจ receipt/version เมื่อเชื่อม CreateAsync ใน Task 4 ปัจจุบันเทสต์ใช้ seeded installment ไม่ใช่ข้อผิดพลาดที่ยืนยันแล้ว
- ข้อจำกัดที่ผู้ตรวจระบุ: ยังไม่รับรอง caller hash ของ input ทั้งก้อน, workflow/หลักฐาน และคำสั่งที่แตะหลายแถวใน Task ถัดไป ผู้ควบคุมตรวจแผนแล้วเป็นขอบเขตการเชื่อมต่ออนาคต Task 3 รับ digest และล็อกหนึ่ง Family/Request/Installment; ผู้เรียกอนาคตต้องรักษาสัญญา hash และลำดับ UUID หากแตะหลายแถว

- คง M2-R1/R2-C1, HTTPS 504 และ Minor เรื่อง log ไม่ปิดรายการเหล่านี้โดยอาศัยผล Task 3
- พักมือถือจริง/NAS จริงตามเดิม ไม่ถือว่าผ่าน
- ไม่เริ่ม Task 4–15 ไม่ push/merge/deploy และไม่ย้ายฐานข้อมูลใช้งานจริง
- รักษาไฟล์ค้างเดิมในงาน HTTPS 504, รายงาน 7B และ E2E ไม่รวมเข้า commit งานนี้
