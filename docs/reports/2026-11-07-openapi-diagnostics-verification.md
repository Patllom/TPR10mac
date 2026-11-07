# ผลตรวจการเก็บหลักฐาน OpenAPI 500

เพิ่ม middleware เฉพาะ endpoint ที่ MapOpenApi กำหนด metadata ไว้ เก็บ Event5001 OpenApiGenerationFailed พร้อมเวลาUTC correlationID ชนิดexception method stack และสาเหตุภายในแบบจำกัดขนาด โดยไม่เก็บข้อความexception/Data/request secrets ในรายการใหม่ ส่งexceptionเดิมต่อให้ตัวจัดการกลาง

ผู้ลงมือ Poincare ผู้ตรวจอิสระ Hilbert ตรวจ Program, middleware, tests และคู่มือ ไม่พบข้อบกพร่องสำคัญ

## TDD และการตรวจ

- RED: ก่อนเพิ่ม middleware เทสต์diagnostics ล้มเหลว1 ผ่าน4 เนื่องจากยังไม่มีeventใหม่ ทั้งที่HTTP500ถูกจำลองสำเร็จ
- GREEN: diagnostics5/5ผ่าน
- ชุดเกี่ยวข้อง OpenAPI/Correlation:149/149ผ่าน ไม่มีfailed/skipped
- Backend Build:0warnings/errors; Formatทั้งsolutionผ่าน (รอบแรก sandbox ปฏิเสธ named pipe; รันด้วยสิทธิ์ที่เหมาะสมผ่าน)
- Frontend Build และ Lint ผ่าน
- log: `/private/tmp/openapi-diagnostics-{red,green,focused,build,format-full-escalated,web-build,web-lint}.log`

คู่มือ: `docs/runbooks/openapi-diagnostics.md`

## ข้อจำกัด

ผลนี้พิสูจน์เครื่องมือวินิจฉัยจากexceptionจำลอง ไม่ใช่การยืนยันสาเหตุของ500เดิม จึงยังไม่ปิดประเด็น ไม่รันbackendทั้งโครงการซ้ำ

ขอบเขตจับexceptionที่หลุดจากendpointOpenAPI ไม่ครอบคลุมproxy500หรือการส่งstatus500เองโดยไม่มีexception Logของframeworkเดิมยังคงเดิมและอาจมีข้อความexception ผู้ดูแลต้องจำกัดสิทธิ์อ่านและตรวจข้อมูลลับก่อนส่งต่อ

ผู้ตรวจระบุช่องว่างเทสต์ที่ไม่ขัดขวางส่งมอบ: frame/symbol limits, logger providerที่throw, responseที่เริ่มส่งแล้ว ยังไม่มีเทสต์เฉพาะทุกกรณี; emitted stackถูกจำกัดแต่การอ่านstackยังสร้างframesก่อนตัด

คงรายการติดตามอื่นทั้งหมด พักมือถือจริง/NASจริง ไม่commit/push/mergeในรอบนี้
