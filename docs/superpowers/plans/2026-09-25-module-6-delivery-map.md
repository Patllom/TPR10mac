# Module 6 — แผนภาพรวมการส่งมอบและขอบเขตแผนย่อย

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans only after the relevant written implementation plan has been approved. เอกสารนี้เป็นแผนลำดับงาน ไม่ใช่คำสั่งให้ข้ามแผนย่อยที่ยังไม่เขียน

**Goal:** ส่งมอบการลงเวลาออนไลน์พร้อมภาพ/GPS และคำร้องแก้ไขสองขั้น โดยไม่ทำ Module 4–5 เต็มชุด

**Architecture:** ใช้ modular monolith เดิม แยกขอบเขตบุคลากร หลักฐาน ลงเวลา และคำร้อง แต่ละขอบเขตมี plan/review/exit gate ของตน ไม่ให้การผ่านแผนแรกหมายถึง Module 6 เสร็จ

**Tech Stack:** Next.js/TypeScript, ASP.NET Core/.NET 10, PostgreSQL, HTTPS และ local-folder/NAS ที่ mount บนเซิร์ฟเวอร์

**Spec:** [Design Spec ที่ผู้ใช้อนุมัติ](../specs/2026-09-25-module-6-attendance-design.md)

สถานะวันที่25กันยายน2026: 6AครบTasks1–6และรวมPR #2 เข้าmainบนGitHubแล้วที่ `27a184e` ดูผลตรวจใน `docs/architecture/module-6a-exit-gate.md`; [แผน6B](2026-09-25-module-6b-evidence-storage.md) จัดทำแล้วและรอผู้ใช้อนุมัติ ยังไม่เริ่มimplementation6B ส่วน6C–6Dยังไม่มีtask-level implementation plan

## Global Constraints

- กล้องสดทั้งเข้า/ออก ภาพมีเวลาจากเซิร์ฟเวอร์; GPS ต้องมีแต่ไม่มี geofence
- วันละหนึ่งคู่ต่อ user/workDate ทั่วระบบ นับวันไทยตามเวลาเข้า ออกข้ามวันและต่าง Site/Workspace ได้เฉพาะมี exact assignment
- หัวหน้าดู GPS ลูกทีมโดยตรงแต่ไม่มีรูป; HR ตามหน่วยงานดูได้ทั้งคู่; own data ดูได้ทั้งคู่; Admin ไม่ bypass
- หัวหน้าและ HR คนละคน อนุมัติฉบับเดียวกันตามลำดับ; ส่งกลับเริ่มใหม่และมี 7 วันต่อรอบ; original evidence ไม่เปลี่ยน
- ไม่ลบหลักฐานอัตโนมัติ เปลี่ยน storage ไม่ทำให้รหัสรูปเก่าเปลี่ยนหรือถอดต้นทางก่อนย้ายสำเร็จ
- ไม่ปิด MFA/CSRF/TLS; Test/Build/Lint และ Code Review ก่อนอ้างผ่านแต่ละระยะ
- Dev4000 / production-build4001 / HTTPS preview4443; รักษา Landing Page และ Module1–3

## ลำดับและเกณฑ์ส่งมอบ

| ระยะ | ผลส่งมอบที่ทดสอบได้ | ต้องมีมาก่อน | เกณฑ์ผ่านและสิ่งที่ยังไม่ได้ |
| --- | --- | --- | --- |
| **6A บุคลากรและสิทธิ์** | ต้นสังกัด สายบังคับบัญชา HR assignments, API/หน้าจัดการ, ตัวตัดสินสิทธิ์และเส้นทางอนุมัติ | main รวม Module3 | constraints, no self-grant, MFA/revocation, current-vs-history และ route tests ผ่าน; ยังลงเวลา/ดูภาพ/ส่งคำร้องไม่ได้ |
| **6B รูปและที่เก็บ** | image processing/stamping, immutable evidence ID, local/NAS adapter, switch write target, resumable migration, scoped reads | 6A policy + snapshot contract | ใช้ข้อมูลสังเคราะห์ทดสอบเจ้าของ/หัวหน้า/HR, crash/checksum/cutover/orphan, real NAS drill แยกจากmock; ยังไม่รับรอง attendance pairing |
| **6C ลงเวลาออนไลน์** | Camera/GPS UI, challenge/idempotency, event/day schema, เข้า–ออกและประวัติตามสิทธิ์ | 6A + 6B | หนึ่งคู่/วัน หนึ่งรอบเปิด/คน, ข้ามวัน/ต่างSite, snapshot/revocation/privacy/mobile ผ่าน; เวลาขาดยังแก้ไม่ได้จน6D |
| **6D คำร้องและรับงานรวม** | revisions/deadlines, queue หัวหน้า→HR, ส่งกลับ/หมดอายุ, effective adjustments และ integration exit gate | 6A + 6B + 6C | ลำดับสองขั้น race/revocation/deadline/หลักฐานเดิม/กู้คืน และทดสอบครบ Module6 |

ให้ merge ระยะที่ตรวจผ่านแล้วตามคำสั่งผู้ใช้ก่อนสร้าง branch ระยะถัดไปจาก main ห้ามซ่อน dependency ด้วยการเปิดหลาย branch ที่คิดว่าเป็นอิสระ ทั้งสี่ระยะต่อกันตามตาราง

## สัญญาระหว่างแผน

- 6A ส่ง `EmploymentSnapshot`, `AttendanceReadDecision`, `AttendanceRouteDecision`, `AttendanceAccess` และ `AttendanceRouteResolver` ตาม signature ในแผน 6A; ห้าม6B/6Cคัดลอก authorization ด้วยเงื่อนไขอีกชุด
- 6B ส่ง evidence ID ที่ยังไม่เผยแพร่จน business commit, stamped metadata และ read gate; ไม่รับพาธจาก client ไม่เก็บ URL เป็น foreign key ต้องกำหนด image library/version/license และ font ที่รองรับภาษาไทยจากแหล่งทางการในแผน6Bก่อนเพิ่ม dependency
- 6C เป็นเจ้าของ `AttendanceDay/Event`, employee-level lock, idempotency และ canonical server timestamp; 6B ต้องประทับ instant ที่6Cตรึงไว้ ไม่เรียกนาฬิกาใหม่หลังอัปโหลด
- 6D ใช้ lock owner เดียวกับ6C เปลี่ยน effective values เท่านั้น ไม่แก้ original event/image; route snapshot เก็บประวัติแต่ตรวจ current grants ก่อน action ทุกครั้ง
- ใช้ลำดับ lock ที่ไม่ขัดกับ identity lock `7241002` เดิม ห้าม filesystem I/O อยู่ใน transaction ที่ถือ identity lockเป็นเวลานาน
- URI สำหรับภาพ thumbnail/download ต้องผ่าน read gate เดียวกัน ไม่มี signed URL ระยะยาวหรือ browser cache ที่ถอนสิทธิ์ไม่ได้

## Review Focus

1. เปลี่ยนต้นสังกัด/หัวหน้าระหว่างรอและอ่านย้อนหลัง — เพิ่ม temporal policy tests ใน6A และ route-revalidation tests ใน6D
2. NAS สำเร็จแต่ DB/audit fail หรือ crashหลังcopy — เพิ่ม failure injection ทุกboundary ใน6B และ atomic attendance tests ใน6C
3. เที่ยงคืนคนละSiteกับ retry หลังtimeout — เพิ่ม clock/constraint/idempotency tests ใน6C ไม่ใช่แค่UIhappy path
4. หัวหน้าได้ URL รูป/late responseจาก HR อีกบัญชี — เพิ่ม directAPI/DOM/cache tests ใน6B/6C
5. HR ส่งกลับแล้วแก้ข้ามกำหนด/คำร้องแข่งกับcheckoutจริง — เพิ่ม revision-deadline/serialized commit tests ใน6D

## แผนที่ความครบถ้วนกับ Spec

| Spec | เจ้าของ implementation |
| --- | --- |
| R01,R02,R04 — กล้อง/GPS/ประทับเวลา | 6B image pipeline + 6C capture |
| R03,R13 — เปลี่ยนที่เก็บ/ไม่ลบ | 6B |
| R05,R06,R07 — หนึ่งคู่/ข้ามวัน/ต่างSite | 6C |
| R08–R12 — คำร้อง/สายอนุมัติ/ส่งกลับ/7วัน | 6A directory/route + 6D workflow |
| R14,R15 — รูป/GPS/ไม่มีAdminbypass | 6A policy + 6B read gate + 6C projection + 6D queue |
| §10 transaction/orphan/idempotency | 6B + 6C + 6D |
| §14–15 operations/security/acceptance | ทุกแผนตรวจส่วนของตน; 6Dตรวจทั้งระบบ |

## การส่งมอบเอกสารในรอบนี้

- [Implementation Plan 6A — บุคลากรและสิทธิ์](2026-09-25-module-6a-directory-access.md) มีไฟล์ สัญญา DTO, RED/GREEN, commands และ exit gate พร้อมให้ตรวจ
- [Implementation Plan 6B — หลักฐานภาพและที่เก็บ](2026-09-25-module-6b-evidence-storage.md) ล็อก image stack และstorage recovery protocol แบ่ง8Tasks รอผู้ใช้ตรวจอนุมัติ; แผน6Cจะล็อกcapture payloadและevent transaction; แผน6Dจะล็อก revision/state transitions หลังมี event contractจริง
- ยังไม่มีการอนุมัติแผนใดจากการอนุมัติ Spec เพียงครั้งเดียว ต้องอนุมัติแผนก่อน execution แต่ไม่ย้อนถามข้อกำหนดที่ยืนยันแล้ว
- วิธีทำงาน Native ที่ใช้ในModuleก่อนหน้ายังคงใช้ได้: ผู้ทำหลักทำTDDและหนึ่ง fresh whole-branch review ต่อระยะ; ผู้ใช้เปลี่ยนเป็น subagent-driven ได้ก่อนเริ่ม
- ไม่รวมการแก้ปัญหา Preview หรือบัญชีทดสอบในแผนนี้ ไม่แตะไฟล์ pre-merge-backup และไม่อัปเกรด dependency โดยไม่มีเหตุผล
