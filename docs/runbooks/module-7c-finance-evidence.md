# คู่มือหลักฐานการเงิน Module 7C — Task 5

เอกสารนี้อธิบายบริการจัดเก็บและตัวตรวจ PDF ของ Task 5 ยังไม่ใช่คู่มือหน้าจอหรือ API อัปโหลด/ดาวน์โหลด ซึ่งอยู่ใน Task ถัดไป สถานะการตรวจรับให้ดูรายงาน Task 5 แยกต่างหาก

## เตรียมตัวตรวจ PDF

ต้องมี Docker ที่ใช้ Linux และ cgroup v2 พร้อมรองรับ memory, swap, PID และ CPU quota ผู้รัน API ต้องเรียก Docker ได้ การเข้าถึง Docker เป็นสิทธิ์สูง จัดให้เฉพาะเครื่องและบัญชีที่เชื่อถือได้ งานนี้ไม่ได้เพิ่มสิทธิ์หรือ deploy เครื่อง production

สร้าง image จากราก repository ก่อนเปิดใช้บริการ:

```sh
docker build -f backend/src/TPR10.PdfValidator/Dockerfile -t tpr10-pdf-validator:task5 .
docker image inspect --format '{{.Id}}' tpr10-pdf-validator:task5
```

นำค่า `sha256:...` ที่คำสั่งสุดท้ายคืนมาไปตั้ง `FinanceEvidence:Pdf:ImageId` ต้องเป็น ID เต็มของ image ในเครื่อง ไม่ใช้ tag ที่เปลี่ยนได้ ไม่ตั้งเป็น image ของ test fixture ระบบไม่ดาวน์โหลดหรือสร้าง image ระหว่างรับไฟล์

worker รับ bytes ผ่าน stdin คืนผลขนาดจำกัด ไม่มี network ไม่ mount ที่เก็บหลักฐานหรือ Docker socket ทำงาน non-root และ read-only จำกัด memory 512 MiB โดยไม่เพิ่ม swap, CPU 1, PID 64 และเวลาตรวจ 15 วินาที ค่าเริ่มต้นรองรับสองงานพร้อมกัน หากเต็มจะปฏิเสธแทนการสะสมคิวไม่จำกัด การเก็บกวาด container เป็นขั้นแยกที่มีขอบเขตเวลาของตัวเอง

ถ้า runtime/image ใช้ไม่ได้ ตรวจไม่ผ่าน หมดเวลา หรือเก็บกวาด worker ไม่สำเร็จ ให้ถือว่า PDF ยังไม่พร้อมใช้งาน ไม่ปิดการตรวจเพื่อรับไฟล์ และไม่มี fallback parse ใน API หากบริการหยุดรับงานเพราะ cleanup ล้มเหลว ให้ผู้ปฏิบัติตรวจเฉพาะ container ที่เกี่ยวข้อง ไม่ลบ container อื่นโดยใช้คำสั่งกวาดทั้งเครื่อง

กรณียกเลิกระหว่างสร้าง container อาจยังไม่ทราบผลจาก daemon: runner เก็บชื่อที่ตนเป็นเจ้าของใน `PendingCleanupNames` และปฏิเสธงานใหม่ `ReconcileCleanupAsync` ตรวจซ้ำเฉพาะชื่อเหล่านี้ การไม่พบชื่อยังไม่ยืนยันว่าไม่ถูกสร้างภายหลัง ต้องพบ ID จริง ลบ ID นั้นสำเร็จ และยืนยันการหายไปก่อนเปิดรับงานใหม่ หากไม่เคยปรากฏให้คงพักไว้

ข้อจำกัดระยะแรก: รายการ pending อยู่ใน singleton ของ API process ผู้ปฏิบัติต้องเก็บชื่อค้างและสะสางให้ครบก่อน recycle process การ restart API ไม่ใช่หลักฐาน cleanup สำเร็จ บริการเหล่านี้ยังไม่มีหน้าจอหรือ HTTP endpoint สำหรับผู้ปฏิบัติใน Task 5

## ตั้งค่าที่เก็บ

ส่วน `FinanceEvidence` ใน configuration ต้องมี:

| ค่า | ความหมาย |
|---|---|
| `ActiveStorageId` | GUID ที่เก็บสำหรับไฟล์ใหม่ |
| `Roots` | mapping GUID ไป absolute directory ที่ผู้ปฏิบัติอนุญาตและเตรียมไว้ |
| `MaxFileBytes` | ค่าเริ่มต้น 10 MiB ต่อไฟล์ |
| `MaxFiles` | ค่าเริ่มต้น 10 ไฟล์ต่อข้อเสนอ |
| `MaxTotalBytes` | ค่าเริ่มต้น 50 MiB ต่อข้อเสนอ |
| `MaxImagePixels` | ค่าเริ่มต้น 25 ล้านพิกเซลต่อภาพ |

ค่าใน repository ยังไม่ชี้ไปที่เก็บจริง ต้องตั้งให้ตรงเครื่องใช้งาน ไม่ใส่ credentials หรือข้อมูลธนาคารในไฟล์ config ของ repository ที่เก็บต้องเป็นส่วนตัวและไม่อยู่ใต้ static/public directory ไม่ใช้ symlink เป็นทางเข้าที่เก็บ

ไฟล์รองรับ JPEG, PNG และ PDF ที่ผ่านตัวตรวจ ข้อมูลไฟล์ถูกตรวจจริง ไม่เชื่อเพียง MIME หรือชื่อนามสกุล ไม่ใช้ URL สาธารณะ การเตรียมหลักฐานจ่ายต้องมีสิทธิ์ `payment.record` และผ่านข้อห้ามแยกหน้าที่ เมื่อบันทึกสำเร็จจะเก็บประวัติผู้บันทึกจ่ายพร้อมข้อมูลหลักฐานใน transaction เดียวกัน

ตัวตรวจ PDF ระยะแรกยอมรับเฉพาะโครงสร้างที่ตรวจได้ครบ รองรับ stream แบบไม่บีบอัดหรือ Flate ที่จำกัดขนาด ไม่รับ filter/predictor ที่ยังตรวจไม่ได้ รวมถึง PDF ที่มี incremental update, encryption, embedded file หรือ JavaScript/actions ดังนั้น PDF ที่อ่านได้ด้วยโปรแกรมทั่วไปบางไฟล์อาจถูกปฏิเสธด้วย ต้องไม่อ้างว่ารองรับ PDF ทุกชนิด หากถูกปฏิเสธให้เตรียมเอกสารใหม่ในรูปแบบที่รองรับโดยคงเนื้อหาหลักฐาน ไม่ปิดตัวตรวจเพื่อให้ผ่าน

## เปลี่ยนหรือย้ายที่เก็บ

เพิ่ม storage GUID ใหม่พร้อม root ที่อนุญาต แล้วเปลี่ยน `ActiveStorageId` เพื่อรับไฟล์ใหม่ โดยยังคง mapping ของที่เก็บเดิมไว้ การเปลี่ยนค่า active ไม่ย้ายไฟล์เก่าให้อัตโนมัติ

การย้ายไฟล์เดิมใช้ `FinanceEvidenceMigration.CopyAndSwitchAsync` ผ่านผู้ปฏิบัติที่มีสิทธิ์ filesystem: คัดลอก ตรวจขนาดและ SHA-256 แล้วจึงสลับตำแหน่งในฐานข้อมูลแบบ atomic โดยตรวจว่าตำแหน่งเดิมยังตรงกัน ID หลักฐานไม่เปลี่ยน และเก็บไฟล์/แถวตำแหน่งเดิมไว้ หากไฟล์หาย digest ผิด หรือ commit ไม่สำเร็จ ห้ามถือว่าย้ายสำเร็จ

`FinanceEvidenceReconciler` มีหน้าที่รายงานไฟล์ตกค้าง ระยะแรกไม่ลบอัตโนมัติ ห้ามนำรายงานมาใช้ลบไฟล์ที่มีผู้ใช้อ้างอิงโดยไม่ตรวจสอบ

## เตรียมการทดสอบของนักพัฒนา

เทสต์ PDF ต้องใช้ทั้ง worker จริงและ image fixture ที่สร้างไว้ก่อน เทสต์จะล้มเหลวเมื่อ prerequisite หาย ไม่ข้ามเงียบ:

```sh
docker build -f backend/src/TPR10.PdfValidator/Dockerfile -t tpr10-pdf-validator:task5 .
docker build -t tpr10-pdf-fixture:task5 backend/tests/TPR10.Api.IntegrationTests/PdfWorkerFixture
dotnet test backend/tests/TPR10.Api.IntegrationTests --filter 'FullyQualifiedName~FieldAdvanceEvidenceTests|FullyQualifiedName~FinancePdfValidationTests'
```

fixture ใช้จำลองงานค้างและการใช้ทรัพยากรเฉพาะการทดสอบ ห้ามใช้เป็น image validator ของ API ให้รันเทสต์บน Docker สำหรับพัฒนาแยกจากบริการใช้งานจริง

การทดสอบ local directory สองแห่งและ NAS offline แบบจำลองไม่ใช่ผลทดสอบ NAS จริง ยังคงพักมือถือจริง/NAS จริง และติดตาม M2-R1/R2-C1, HTTPS 504 และ Minor เรื่อง log ตามเดิม
