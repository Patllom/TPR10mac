# หลักฐานตรวจรับ Module 1

สถานะ: อยู่ระหว่าง Code Review และการตรวจรอบสุดท้าย ยังไม่ปิด Exit Gate

ฐานงาน: ea48b7c; branch codex/module-1-foundation; วันที่ทดสอบ 22 กันยายน 2026

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

TDD: health/OpenAPI 2 failed → 3 passed; database 2 failed → 2 passed; mutation 8 failed → 9 passed; forwarded headers 2 failed → 3 passed; rewrite 9 failed → 9 passed; operations 8 failed → 8 passed

ขอบเขตหลักฐาน: local development foundation เท่านั้น ยังไม่มี production TLS/hostname configuration, identity หรือ scope enforcement
