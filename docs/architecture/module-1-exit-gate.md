# หลักฐานตรวจรับ Module 1

สถานะ: ผ่าน Exit Gate สำหรับ Module 1 local foundation — Test, Build, Lint และ Code Review ครบ

ฐานงาน: ea48b7c; branch codex/module-1-foundation; ตรวจรอบสุดท้าย 23 กันยายน 2026 เวลาไทย (22 กันยายน UTC)

| รายการ | หลักฐานที่รันแล้ว |
| --- | --- |
| Backend tests | 17 tests ผ่านบน PostgreSQL 17.11 container จริง |
| Web/operations tests | 17 tests ผ่าน |
| Web Lint/Build | ผ่าน; / และ /portal สร้างได้ |
| Migration | integration test apply → 0 → apply ผ่านบน database ชั่วคราว |
| Audit | UPDATE/DELETE/TRUNCATE ของ event และ metadata ถูกปฏิเสธ; audit failure rollback probe |
| Same-origin | GET ผ่านเว็บ 4001: /api/health/live และ /portal ตอบ 200 |
| Nginx | nginx -t ผ่าน; routing ผ่าน loopback 4080 ไป API/portal ตอบ 200 |
| Production boundary | POST JSON ผ่าน Nginx ไป technical probe ตอบ 404 |
| Trust boundary | IPv4/IPv6 loopback รับ forwarded scheme; remote IP อื่นไม่รับ |
| Backend Build/format | Release build 0 warnings/errors; dotnet format --verify-no-changes ผ่าน |
| Development | ผ่าน port 4000: Landing Page, /portal และ API live ตอบ 200 |
| Code Review | Carson ตรวจ ea48b7c..fac45ee ไม่พบ Critical/Important; แก้สอง Minor แล้วและรันชุดตรวจครบอีกครั้ง |

## คำสั่งตรวจรอบสุดท้าย

```bash
npm test
npm run lint
TPR10_API_ORIGIN=http://127.0.0.1:5080 npm run build
dotnet test backend/TPR10.sln -c Release --no-restore
dotnet build backend/TPR10.sln -c Release --no-restore
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
docker compose --env-file backend/.env.foundation.example -f backend/docker-compose.foundation.yml config --quiet
git diff --check
```

ผลทุกคำสั่ง exit 0; test รวม 34 รายการ ไม่มี skip ส่วน nginx -t ใช้ image nginx:1.28-alpine กับ config ที่ render จริงและผ่าน

## ผล Code Review

แก้ breakpoint ของลิงก์พนักงานให้ต่อเนื่องที่ lg และเพิ่ม X-Real-IP ให้ web upstream แล้ว ไม่มีข้อค้างจาก review

Reviewer ไม่รัน test ซ้ำ; หลักฐานข้างต้นมาจาก main agent หลังแก้ review โดยตรง การตรวจ forwarded headers ครอบคลุม scheme และ trust boundary แต่ยังไม่มี assertion end-to-end แยกสำหรับ host/client IP จึงต้องเพิ่มก่อนใช้ข้อมูลเหล่านี้ตัดสินสิทธิ์

## การปรับจากแผน

- ใช้ empty API template และ sln format ชัดเจน; pin EF runtime ให้ตรง Design 10.0.12
- เพิ่มการป้องกัน TRUNCATE และทดสอบทั้ง audit event/metadata
- ใช้ trigger ใน database ชั่วคราวทำ fault injection แทน interceptor
- response 201 ไม่ส่ง Location ไปยัง GET endpoint ที่ยังไม่มี
- ใช้ Node render config แทน envsubst และไม่เขียนทับไฟล์เดิม
- หน้า portal ไม่แสดงหมายเลข Module ให้ผู้ใช้งาน; คู่มือระบุขอบเขตแทน
- ทดสอบ migration rollback ใน disposable container เพื่อไม่ลบข้อมูล Compose volume
- รวม commit ของงานต่อเนื่องหลัง suite ผ่าน; หลักฐาน RED/GREEN ระบุด้านล่าง

สภาพแวดล้อมยังมี npm engine warning จาก Node 20.18.0 กับ eslint-visitor-keys ตอนติดตั้ง dependency แต่ Test/Lint/Build ผ่าน การติดตั้ง SDK รอบนี้อยู่ใน /private/tmp/tpr10-dotnet ซึ่งไม่ใช่ตำแหน่งถาวร ดูคู่มือก่อนเริ่ม session ใหม่

TDD: health/OpenAPI 2 failed → 3 passed; database 2 failed → 2 passed; mutation 8 failed → 9 passed; forwarded headers 2 failed → 3 passed; rewrite 9 failed → 9 passed; operations 8 failed → 8 passed

ขอบเขตหลักฐาน: local development foundation เท่านั้น ยังไม่มี production TLS/hostname configuration, identity หรือ scope enforcement
