# เก็บหลักฐาน OpenAPI 500

สถานะ: เพิ่มเครื่องมือวินิจฉัย ไม่ใช่การยืนยันสาเหตุหรือปิด OpenAPI500 เดิม

เมื่อ endpoint OpenAPI โยน exception ให้ค้น log category `TPR10.Api.Diagnostics.OpenApiDiagnosticsMiddleware` และ EventId `5001` ชื่อ `OpenApiGenerationFailed` ใน stdout/backend log ที่ระบบเก็บอยู่

## ข้อมูลที่เก็บ

- TimestampUtc: เวลา UTC
- CorrelationId: ตรงกับ header `X-Correlation-ID` ของคำขอ/ผลตอบกลับ
- Endpoint: รูปแบบเส้นทางคงที่ `/api/openapi/{documentName}.json`
- ExceptionType และ StackTrace: ชนิด exception กับชื่อ type/method ของ stack โดยไม่มี path ไฟล์หรือค่าพารามิเตอร์
- ExceptionCauses: JSON ของ exception หลักและสาเหตุภายใน รวม AggregateException พร้อม Index/ParentIndex
- CausesTruncated: แจ้งเมื่อเกินขีดจำกัด8 exceptions; แต่ละ stackไม่เกิน32 frames และชื่อsymbolจำกัด256ตัวอักษร

รายการใหม่ไม่เก็บ exception message/Data/ToString(), query, request body, cookies, authorization headers หรือชื่อเอกสารจาก input ผู้เรียก รายการ log ของ framework เดิมยังคงอยู่และอาจมีข้อมูล exception แบบเดิม ต้องปฏิบัติตามการจำกัดสิทธิ์อ่าน log ของระบบ ไม่ควรส่ง log ทั้งก้อนโดยไม่ตรวจข้อมูลลับ

## วิธีตรวจเมื่อเกิดจริง

1. จดเวลา UTC และสถานะ HTTP พร้อม correlation ID จาก response เก็บ commit/build ที่ใช้งานและสภาพแวดล้อมด้วย
2. ค้น Event5001 ใน backend ด้วย correlation ID เดียวกัน แล้วเก็บเฉพาะรายการที่เกี่ยวข้องในพื้นที่จำกัดสิทธิ์
3. ถ้าไม่มีรายการนี้ ให้ตรวจ proxy/access log และที่มาของ500 เพราะ500ที่ถูกส่งกลับเองโดยไม่มี exception หรือเกิดนอก endpoint OpenAPI ไม่เข้าเงื่อนไขนี้
4. ใช้ชนิด exception และ stack สร้างกรณีทำซ้ำก่อนแก้สาเหตุ รันเทสต์ให้เห็นล้มเหลวแล้วจึงแก้ ห้ามถือว่า diagnostics ผ่านหมายถึงปัญหาเดิมถูกแก้

ข้อมูลยังออกผ่านระบบ ILogger ปัจจุบัน ไม่มีการเพิ่มไฟล์ถาวรหรือระบบส่ง log ภายนอก ผู้ดูแลต้องเก็บ stdout/container log ตามระยะเวลาที่ต้องการก่อนหมุนหรือลบ container

Middleware ส่ง exception เดิมต่อให้ตัวจัดการกลาง ไม่เปลี่ยน response เป็น stack trace ไม่ retry คำขอ และไม่ปิด global logging หากผู้ให้บริการ log ล้มเหลวจะยังส่ง exception เดิมต่อ

คงรายการติดตามอื่น และพักมือถือจริง/NASจริงตามเดิม
