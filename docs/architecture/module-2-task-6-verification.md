# รายงาน Module 2 Task 6 — สิทธิ์ การเพิกถอน และ Audit

23 กันยายน 2026; branch `codex/module-2-identity`, BASE `585f08e` ไม่ merge/push ไม่ใช่การอนุมัติ production

## สิ่งที่เพิ่ม

- Named permission ตรวจ session, restricted stage, capability จากฐานข้อมูล และ MFA อายุไม่เกิน15นาที
- เปิด users/roles/permissions และ operator MFA recovery ภายใต้ policy; `/api/v1` ตั้ง no-store ก่อน authorization
- GET identity probe และ POST technical probe เฉพาะ Testing/Development ต้อง `system:probe`+MFA ไม่มี anonymous+CSRF bypass
- การเปลี่ยน grants/roles และ admin sign-out เพิ่ม security version/revoke affected sessions ใน transaction เดียวกับ Audit และตรวจ actor ซ้ำก่อน mutation
- ป้องกันทั้ง admin class คนสุดท้ายและ admin ที่มี users:manage/roles:manage คนสุดท้าย; PATCH role เปลี่ยนได้เฉพาะชื่อ
- `SecurityAuditRequest` มี actor/acting role/scope/action/target/outcome พร้อม correlation และเวลาจาก server; คง overload เดิมและจำกัด metadata keys
- Migration `20260923033537_ExpandIdentityAudit` เพิ่ม nullable columns ไม่ UPDATE audit เก่า และเติม catalog ที่ขาดในฐานที่ bootstrap แล้วโดยไม่คืน grants

## TDD และผลตรวจ

| ชุด | หลักฐาน |
| --- | --- |
| Permission/MFA matrix และ anonymous mutation | 8RED:404หรือ201 → 8GREEN |
| Role lifecycle/revocation/last-admin/operator binding/rollback | 9RED:404 → GREEN |
| Structured audit/sensitive metadata | 4RED:NotImplemented → GREEN; รวมcore21ผ่าน |
| Stale actor snapshot, PATCH last-effective-admin, probe actor/role | 6RED → 12GREENรวมvalidation/rollbackเดิม |
| Upgrade catalog | RED:1แทน6 → GREENในfullsuite |
| Last admin class เมื่อ operator เป็น staff | RED:204แทน409 → GREEN |
| Full regression รอบแรก | 237ผ่าน/3Malformed_cookieล้มเหลว:503แทน401; แก้ handler ให้ทำงานเฉพาะ named permission → focused36ผ่าน |
| Full backend หลังแก้ | `dotnet test backend/TPR10.sln --verbosity minimal`:253/253ผ่าน ไม่มีskipped รวม concurrent grant removal |
| Backend build/format | `dotnet build backend/TPR10.sln --no-restore --verbosity minimal`:0warnings/errors; `dotnet format backend/TPR10.sln --verify-no-changes --no-restore`:ผ่าน |
| เว็บ | `npm test`:23/23; `npm run lint`, `npm run build`:ผ่าน |
| HTTPSจริง | smoke4000และ4001ผ่าน CA trust, cookie flags, CSRF403/anonymous401/authorized201, hostile Host; ไม่เปลี่ยน system trust |

Tests ใช้ PostgreSQL แยกต่อ test ไม่มี production data เพิ่ม regression rejection ทุก admin route ก่อน bind JSON, immutable UPDATE/DELETE/TRUNCATE หลัง upgrade และ race ถอด grants สองผู้ดูแลต้องสำเร็จ1/ปฏิเสธ1 การทดสอบ behavior เดิมที่ผ่านทันทีเป็น regression ไม่อ้าง RED ใหม่

## ข้อตัดสินใจและผลหากผิด

1. ทำเฉพาะTask6 inline ใน worktreeเดิม ไม่redo1–5 เก็บledgerต่อ7–9 ไม่push/merge — หากผิดขอบเขตส่งมอบคลาดเคลื่อน
2. API เลือก acting role ที่ให้ capability จริงแบบ deterministic ไม่รับ role จากclient; scope null จนModule3 — หากผิดauditอาจระบุผู้ใช้อำนาจหรือscopeเกินหลักฐาน
3. PATCHไม่เปลี่ยนroleclass ป้องกันทั้งlast-admin-classและeffective-management — หากผิดอาจจำกัดconfigurationเกินไป แต่ไม่เปิดทางล็อกผู้ดูแลออกทั้งหมด
4. Catalog upgrade ไม่regrant; ผู้มีroles:manage+MFAอนุมัติcapabilityใหม่เอง — หากผิดdeploymentต้องมีขั้นตอนเพิ่มแทนได้รับสิทธิ์โดยเงียบ
5. Auditเก่าคอลัมน์ใหม่null ไม่อนุมานย้อนหลัง; metadataallowlistไม่ใช่เครื่องตรวจsecretในข้อความอิสระ — หากผิดประวัติอาจถูกตีความเกินจริงหรือoperatorใส่secretในreason จึงต้องทำตามrunbook
6. AccountProvisioning direct call เดิมเป็นtrustedinternalAPI; HTTP DIใช้fresh-sessionguardเสมอ ทางเรียกใหม่ต้องไม่bypassguard — หากผิดผู้เรียกภายในอาจข้ามsession/MFA
7. Customauthorizationresponseใช้เฉพาะnamedpermission; auth/sessionเดิมยังปฏิเสธmalformedcookie401โดยไม่ใช้DB — หากผิดoutagebehaviorเปลี่ยน
8. TLSsmokeใช้seeded session/factorเฉพาะฐานทดสอบ ไม่มีseedHTTP ไม่อ้างlogin/TOTPจริงในsmoke; APIintegrationใช้login/enroll/confirmจริง — หากผิดจะประเมินหลักฐานE2Eเกินจริง
9. คงsharedidentitytransactionlockตามTasks4–5; throughput/distributeddeployment/directSQLนอกlifecycleต้องproductiongate — หากผิดเสี่ยงcapacityหรือสิทธิ์ไม่สอดคล้อง

## Code Review และขอบเขตที่ยังไม่ผ่าน

รอ Code Review อิสระก่อนปิด Task6 ยังไม่อ้างว่าส่งมอบครบ

Tasks7 reset/forced-change, Task8 browser UI, Task9 OpenAPI/ExitGate และ policy approval โดยSecurityowner ยังไม่เสร็จ ข้อค้างMinorเดิมของTasks1–5คงตามรายงานเดิม

อ้างอิง framework ที่ตรวจแก้namespace: [Microsoft — IAuthorizationMiddlewareResultHandler](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.authorization.iauthorizationmiddlewareresulthandler?view=aspnetcore-10.0)
