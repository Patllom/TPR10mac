# รายงานตรวจรับ Module 2 — Task 3: Login, Session และ Logout

วันที่ตรวจ: 23 กันยายน 2026 · branch `codex/module-2-identity` · ฐาน Task 3 `305482e4d70cf1365c07659debe9783314aa161a`

## สถานะ

Implementation และ verification ก่อน review ผ่านแล้ว กำลังรอ Code Review อิสระ จึงยังไม่ถือว่าส่งมอบ Task 3 และยังไม่ใช่ความพร้อมของ Module 2 ทั้งหมด

## สิ่งที่เพิ่ม

- Login ผ่าน local identity provider และ Argon2id จริง; ไม่มี self-registration หรือ seed endpoint
- Session token random 32 bytes เก็บเฉพาะ SHA-256; host-only Secure/HttpOnly cookie; response ไม่ cache
- ตรวจ account active, security version, revocation, idle 30 นาที, absolute 8 ชั่วโมง และ permission ปัจจุบัน
- Restricted stage สำหรับเปลี่ยนรหัสผ่าน/MFA ไม่มี business permission; ยังไม่ทำขั้นพิสูจน์ MFA ใน Task นี้
- Consume pre-auth เมื่อ Login และผูก CSRF ใหม่กับ session; session อื่นและ token ก่อนเปลี่ยนใช้ไม่ได้
- Logout พร้อม audit ใน transaction; บันทึก audit ไม่ได้จะ rollback และไม่แจ้งสำเร็จ
- Rotation ภายในฝั่ง server รักษา absolute expiry; revoke-user เพิ่ม security version ครอบคลุม session ที่เกิดพร้อมกัน
- Lockout 5 ครั้ง/15 นาที พัก 15 นาที ใช้ identifier bucket ทั้งชื่อที่มีและไม่มีบัญชี พร้อม additive migration และเพดาน 10,000 entries

## หลักฐาน TDD

| ชุด | RED ที่เห็นจริง | หลังแก้ |
| --- | --- | --- |
| Session ชุดแรก 4 กรณี | Login 404 | ผ่าน 4 |
| Stage, idle renewal, DB unavailable | ล้มเหลว 5 กรณี | ผ่านใน suite |
| Login lockout, actor และ failure audit | ล้มเหลว 4 กรณี | ผ่านใน suite |
| Rotation | NotImplementedException | cookie/CSRF เดิมใช้ไม่ได้ และ absolute expiry ไม่เพิ่ม |
| Assurance เปลี่ยนระหว่างใช้งาน | 3 กรณีได้ 200 แทน 401 | ผ่านทั้ง 3 |
| Revoke-user แข่งกับ issuance | session ที่เกิดภายหลังยังใช้ได้ | security version ทำให้ถูกปฏิเสธ |

เพิ่มเติมมี tests concurrency, lockout ข้าม host restart, capacity/reclamation, malformed cookie/JSON, dummy password work ของชื่อที่ไม่มีบัญชี, application logs, audit rollback และ service ไม่ commit เอง

## Verification ก่อน review

| คำสั่ง | ผล |
| --- | --- |
| `dotnet test backend/TPR10.sln --verbosity quiet` | 141 ผ่าน, 0 ล้มเหลว |
| `dotnet build backend/TPR10.sln --no-restore --verbosity quiet` | ผ่าน, 0 warnings/errors |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | ผ่าน |
| `npm test` | 23 ผ่าน |
| `npm run lint` | ผ่าน ไม่มี warnings/errors |
| `npm run build` | ผ่าน |
| `node infra/nginx/smoke-identity-https.mjs 4001` และ `4000` | ผ่านทั้งคู่ |
| `git diff --check` | ผ่าน |

ใช้ PostgreSQL Testcontainers แยกและ HTTPS test host; TLS smoke ตรวจ proxy/CA/cookie/CSRF จริงทั้งสองพอร์ต ไม่เปลี่ยน trust store เครื่อง และไม่ใช้ฐานข้อมูลผู้ใช้ TLS smoke เดิมยังไม่ใช่ browser Login flow ซึ่งอยู่ Task 8

## ข้อตัดสินใจและข้อจำกัด

1. ทำเฉพาะ Task 3; Tasks 4–9 และ Minor เดิม Task 1–2 ไม่ขยายในรอบนี้ หากตีความผิดต้องเพิ่มขอบเขตตามคำขอ ไม่อ้างว่า UI/RBAC/MFA พร้อมแล้ว
2. Session stage เป็นข้อจำกัด ไม่ใช่สิทธิ์; Task 6 ต้องตรวจ business permission/MFA freshness อีกชั้น มิฉะนั้นเสี่ยงข้ามขั้นยืนยัน
3. Login/Logout เป็นเจ้าของ transaction; service เปลี่ยน tracked entities เท่านั้น `RotateAsync` เป็น trusted internal API ที่ caller ต้องพิสูจน์ transition/MFA และ audit ก่อน commit มิฉะนั้นเสี่ยงยกระดับโดยไม่มี proof
4. Rotation ไม่ต่อ absolute expiry; `RevokeUserAsync` เพิ่ม security version เองพร้อม revoke rows เพื่อปิดช่องว่าง concurrent issuance; caller ต้องรับมือ optimistic concurrency แบบ fail closed
5. เพิ่มตาราง identifier lockout แบบ bounded ทั้งชื่อที่มีและไม่มีบัญชีเพื่อไม่ให้ 429 บอกการมีบัญชี และให้ counter อยู่ข้าม restart; capacity/นโยบายต้องทบทวนก่อน production ไม่เช่นนั้นอาจล็อกผู้ใช้หรือรับโหลดไม่พอ
6. Permission อ่านใหม่ทุกคำขอ แต่ mutation เปลี่ยน account/role ต้องใช้ revoke/version/rotate ใน transaction ตาม Tasks 4–7 มิฉะนั้น session เดิมอาจไม่ถูกยกเลิกตามนโยบาย
7. ปรับ tests valid-CSRF ให้ใช้ unknown route แทน Login ที่เปิดจริงแล้ว ไม่ลด assertion ด้าน CSRF
8. DB outage รองรับ Npgsql ที่ EF ห่อด้วย InvalidOperationException; ตอบ generic 503 และ no-store ไม่ catch ข้อผิดพลาดทุกชนิด
9. Review ตัดช่วงจากฐาน Task 3 ไม่ตรวจ Tasks 1–2 ซ้ำทั้งก้อน แต่ยังรัน regression ทั้งระบบ; เก็บ worktree/ledger ไว้ต่อ Tasks 4–9 ไม่มี merge/push

## รายการเดิมก่อน production

- Task 1 Minor: negative tests ของ malformed/canonical password hash และ constraints บางชุด
- Task 2 Minor: ตรวจโหลด PFX/password/private key ให้ล้มตั้งแต่ startup แทน lazy resolution
- ต้องอนุมัติ security owner, hostname/TLS, key backup/rotation, capacity/distributed limiting และแก้ dependency vulnerabilities เดิมก่อน deploy

คู่มือการใช้งานและสัญญาผู้เรียก service อยู่ใน [คู่มือ Module 2](../runbooks/module-2-identity.md)
