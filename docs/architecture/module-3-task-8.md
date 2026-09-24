# Module 3 — Task 8: เลือกพื้นที่และจัดการโครงสร้าง/การมอบหมาย

## ขอบเขต

หน้า Portal เชื่อมไปยังตัวเลือกพื้นที่ หน้าจัดการโครงสร้างองค์กร และหน้าจัดการการมอบหมาย ตามสิทธิ์ของ session ที่ตรวจจากเซิร์ฟเวอร์ ไม่ให้สิทธิ์ธุรกิจแก่ Administrator โดยปริยาย ไม่เปลี่ยน API หรือฐานข้อมูล Production ในงานนี้

หน้าข้อมูลตามพื้นที่เป็นเครื่องมือพิสูจน์สิทธิ์ ไม่ใช่โมดูลธุรกิจจริง เปิดเฉพาะ Next development หรือ server-only `TPR10_SCOPE_TEST_UI=true` ซึ่ง harness ใช้กับ API environment Testing เท่านั้น **ห้ามตั้ง flag นี้ใน deployment จริง** เมื่อปิด flag ใน production หน้า technical ตอบ 404 แต่ selector และ management ยังทำงาน API Production ยังคงไม่ลงทะเบียน technical routes ตาม Task 6–7

ยังไม่รวม whole-branch review, runbook และ Exit Gate ของ Task 9 ไม่ push/merge อัตโนมัติ

## พฤติกรรมที่ส่งมอบ

- `ScopeKey` ระบุ Workspace/Project/Site แบบ exact รวม null; UUID และลำดับ segments ต้องถูกต้อง Site ที่ไม่มี Project ใช้ไม่ได้ ไม่รับ URL ภายนอก
- Server discovery ใช้ origin จาก configuration เท่านั้น ไม่เชื่อ Host ที่ผู้ใช้ส่งมา ส่งเฉพาะ session cookie ที่ตรวจรูปแบบแล้ว ใช้ no-store, redirect:error และ timeout 5 วินาที
- Selector แสดงชื่อ/ระดับ/สิทธิ์เฉพาะพื้นที่ที่ได้รับมอบหมาย รองรับหน้าละ 100 พื้นที่และข้อความไทยเมื่อไม่มี assignment ไม่มีการตัดรายการส่วนเกินเงียบ ๆ
- Organization form สร้าง/แก้ไข Workspace, Project, Site, Department; แก้ไขส่ง expectedVersion และเหตุผล การปิดพื้นที่ใช้ lifecycle API เดิม ไม่เปลี่ยนสิทธิ์บนหน้าจอแบบ optimistic
- Assignment form ใช้ minimal options endpoints ไม่บังคับเพิ่ม users:manage; เลือกผู้ใช้/บทบาทธุรกิจ ระบุ UUID ของพื้นที่ สร้าง/replace/revoke พร้อม expectedVersion เหตุผลและประวัติ ปิดตัวเลือกบัญชีตนเองและตรวจปฏิเสธที่ API จริงด้วย
- Technical panel อ่าน/สร้าง/แก้ไขรายการ ส่ง expectedVersion และส่งออก JSON ทดสอบสูงสุด 100 แถว ฟิลด์ restricted ถูกละจาก DTO สำหรับผู้ไม่มีสิทธิ์ ไม่ใช่แค่ซ่อนด้วย CSS; การแก้ไขแยกไม่ส่งฟิลด์/ล้างค่า/ตั้งค่าใหม่
- `authMutation` และ hook รองรับ POST/PATCH แบบ explicit โดยคง POST เดิม ตรวจ method/path ด้วย anchored allowlist ก่อนขอ CSRF ไม่มี retry อัตโนมัติ ฟอร์มเป็น controlled input ปิดก่อน hydration และระหว่างส่ง ป้องกันส่งซ้ำด้วย lock
- แยก 401→login, 403→สิทธิ์/MFA, 404→พื้นที่ไม่พร้อม, 409→โหลดเวอร์ชันใหม่, 429→รอ และ 503→บริการไม่พร้อม ไม่ถือ API ล่มเป็น logout
- Query ใช้ AbortController/request generation และล้างข้อมูลเดิมก่อนโหลด การเปลี่ยนพื้นที่ใช้ full navigation ไม่คง scoped client cache; pagehide ซ่อนเนื้อหา/ยกเลิก query, กลับจาก history หรือกลับเข้าแท็บตรวจใหม่

## การทดสอบและข้อมูลทดสอบ

ทุก E2E ใช้ PostgreSQL/container/network/CA ชั่วคราวและบัญชีสังเคราะห์ ไม่ใช้ข้อมูลจริง ไม่บันทึก trace/video และไม่เปลี่ยน trust store ของเครื่อง `NODE_EXTRA_CA_CERTS` มีผลเฉพาะโปรเซส Next/Playwright ที่ harness สร้าง ไม่ปิดการตรวจ TLS

- Node: exact scope/path/parser, origin/JSON/status, pagination, no-store/redirect/timeout, method/path allowlist, CSRF ล้มแล้วไม่ส่ง mutation และ auth เดิมยัง POST
- Browser: ไม่มี assignment, Administrator ไม่มี business bypass, บทบาท A ไม่ใช้ใน B, restricted omission, logout/back/เปลี่ยนบัญชี, malformed deep link, response A ช้ากว่า navigation B, pagination 101 พื้นที่, keyboard/mobile
- Browser ฟอร์ม: Organization create/update/deactivate, assignment create/replace/revoke/history/self-grant API 403 และไม่มี organization permission, record POST/PATCH/version conflict 409, duplicate submit, ก่อน JavaScript พร้อม, query 503 ล้างข้อมูลโดยไม่ออกจากระบบ
- Production boundary: technical 404 เมื่อปิด flag ขณะที่ selector/management ยังใช้งานได้; API Production boundary อยู่ในชุด Backend เดิมที่รัน regression เต็ม
- Identity E2E เดิมยังใช้ default harness spec และรันตรวจอีกครั้ง

## ข้อวินิจฉัยระหว่างทำ

1. ทำเฉพาะ Task 8 ตามคำขอ ไม่เริ่ม Task 9/push/merge — ยังไม่ใช่การรับรองทั้ง Module พร้อม Production
2. ใช้ spec/plan ที่อนุมัติแล้วและ worktree/dependency เดิม ไม่ brainstorm ใหม่หรือเปลี่ยน main — หากแผนล้าสมัยต้องเปิดประเด็น ไม่แก้ขอบเขตเงียบ ๆ
3. Assignment manager ใช้ UUID จากผู้ดูแลโครงสร้างกับ minimal options — ไม่ยกระดับ users:manage/organization:manage เพื่อทำ picker; แลกกับขั้นตอนคัดลอก UUID
4. เพิ่ม server-scopes/useScopeQuery/ScopeFrame และแยก ScopeSeed เฉพาะ fixture — เพิ่มไฟล์เพื่อแยก server boundary/cache/test seed ไม่เพิ่ม dependency หรือ production seed endpoint
5. ใช้ anchor เพื่อ full navigation และยกเว้นเฉพาะ lint rule next/no-html-link-for-pages ในไฟล์ที่จำเป็น — ทิ้ง state ทุกครั้ง แลกกับ document navigation
6. ตรวจอิสระเฉพาะ Task 8 เพิ่มหนึ่งรอบตาม quality gate ของผู้ใช้ ไม่แทน whole-branch review ใน Task 9 — มีต้นทุน reviewer เพิ่ม

## Code Review

Ramanujan ตรวจ tracked diff และ untracked source/tests แบบ fresh-context/read-only ไม่พบ Critical; พบ Important 1 ข้อและ Minor 3 ข้อ ผู้พัฒนาตรวจเทียบ API แล้ววินิจฉัยก่อนแก้ดังนี้:

- I1 คง Important และแก้แล้ว: การ์ดแสดงชื่อบทบาท/role ID ที่ใช้เป็น fallback ได้ และปุ่มถอนระบุบทบาท — RED หา Staff/Approver จากรายการไม่ได้ → GREEN ตรวจถอน Approver แล้ว Staff ยังอยู่
- M1 ยกระดับเป็น Important และแก้แล้ว: no-op replacement ไม่เรียก revoke session ที่ API แต่ข้อความเดิมอ้างว่าทำแล้ว ผู้ดูแลอาจเข้าใจสถานะ security ผิด — RED ปุ่มยังเปิด → GREEN ป้องกัน no-op ที่ปุ่มและ submit handler รวมกรณี requestSubmit โดยไม่มี replacement request ไม่เปลี่ยน backend semantics
- M3 ยกระดับเป็น Important ด้านหลักฐานและเพิ่มแล้ว: restricted DOM/export หลัง logout/back/บัญชีเปลี่ยน, delayed list/export และ headers no-store ของ scoped HTML/discovery/record/export — GREEN ทั้ง dev/prod และ mutation test ปิด visibility listener ชั่วคราวทำให้ RED เพราะข้อมูลยังมองเห็น แล้วคืนโค้ดและผ่านทั้งชุด ไม่ถือการขาด test เดิมเป็นหลักฐานว่ารั่วแล้ว
- M2 คง Minor และเลื่อนไว้: UUID ที่พิมพ์ a–f เป็นตัวใหญ่ผ่าน validation แต่ technical deep link อาจไม่พบเพราะเทียบ canonical discovery path แบบ case-sensitive; ลิงก์ที่สร้างจาก selector เป็นรูปแบบ API ปกติ ไม่เปิดสิทธิ์เพิ่ม ควร normalize พร้อม mixed-case regression ในงานถัดไป

### ขอบเขตที่ผู้ตรวจเว้นไว้และข้อวินิจฉัยของผู้พัฒนา

7. Review Focus 1: ไม่รับรอง repository/HTTP isolation ของ Tasks 5–6 ใหม่โดย reviewer — ใช้ full Backend regression ของผู้พัฒนาและเก็บ whole-branch review ไว้ Task 9; ถ้าทดสอบไม่ครอบคลุมยังมีความเสี่ยงข้ามพื้นที่
8. Review Focus 2: ไม่รับรอง MFA/session matrix ของ Tasks 2/4 ใหม่โดย reviewer — full regression ยังต้องผ่าน ไม่ถือ browser login บางกรณีแทนทั้ง matrix; ถ้าข้ามอาจพลาด lifecycle regression
9. Review Focus 3: ไม่รับรอง controlled commit races ใหม่โดย reviewer — ผู้พัฒนารัน Backend ทั้งชุดที่มี barriers เดิม; ไม่อ้างว่า reviewer ทดลอง races เอง
10. Review Focus 4: ไม่รับรอง audit/fault rollback ใหม่โดย reviewer — ผู้พัฒนารันชุด fault เดิม ไม่เปลี่ยน transaction API ใน Task 8; ไม่ใช้ UI success เป็นหลักฐาน rollback
11. Review Focus 5: ยอมรับช่องว่าง M3 เป็น Important และเพิ่ม acceptance ในงานนี้ — ไม่เลื่อนไป Task 9 เพราะเป็นความเสี่ยงหลักของ Task 8
12. Runtime verification: reviewer อ่าน source เท่านั้น — ผล Test/Build/Lint/E2E เป็นการรันของผู้พัฒนา ไม่อ้าง independent full rerun; ตรวจ untracked files ก่อน commit เพิ่มด้วย staged diff check
13. Production boundary: browser ใช้ API Testing เพื่อ fixture — ต้องแยกหลักฐาน Next production flag-off ออกจาก API Production tests ใน Backend; รวมกันจึงตรงแผน ไม่อ้าง browser พิสูจน์ API Production
14. Deployment flag: ยังไม่มีหลักฐาน environment จริง — ห้ามตั้ง TPR10_SCOPE_TEST_UI ใน deployment; การตรวจตั้งค่าจริงอยู่ใน gate ก่อนเปิดใช้งาน ไม่ถือผล fixture เป็น production sign-off
15. UX เพิ่มเติม: dedicated detail page, write-only update workflow, search/filter และ accessibility ทั้งระบบไม่ได้รับรอง — ไม่เพิ่มฟีเจอร์ที่ไม่มี acceptance ใน Task 8; keyboard/mobile เป็นเพียงกรณีที่รัน ไม่ใช่ WCAG certification
16. Rulings ที่ reviewer ยอมรับ: manual UUID, full navigation, targeted lint disable และ helper แยกไฟล์คงตามข้อ 3–5 — ต้นทุน UX/performance ตามที่ระบุ ไม่ใช่การเพิ่มสิทธิ์
17. Task 9/owner sign-off/capacity/deployment: ยังไม่รับรอง — ไม่ push/merge หรือประกาศ Module 3 production-ready จาก Task 8
18. Visibility ใน headless Firefox: ส่ง visibilitychange/สถานะ hidden แบบควบคุมใน browser เพราะการสลับแท็บระดับ OS ไม่แน่นอนใน headless; การเปลี่ยนบัญชีผ่านอีกแท็บใช้ UI/session/API จริง — เป็นหลักฐานของ lifecycle handler ไม่ใช่การรับรอง BFCache/OS tab behavior ทุก browser ต้องตรวจ manual เพิ่มก่อน production sign-off

แก้ review หนึ่งรอบด้วย TDD ตามกระบวนการ Superpowers ไม่มีการส่ง re-review; Minor M2 ค้างหนึ่งข้อ ข้อค้างจาก Tasks 1–7 ไม่ได้ถูกปิดโดยรายงานนี้

## ผลตรวจสอบ

ผลวันที่ 24 กันยายน 2026: Test, Build, Lint, format และ E2E ผ่านครบ ไม่มี Critical/Important ที่ค้างหลังแก้ review; มี Minor M2 หนึ่งข้อที่ระบุด้านบน

- Baseline Node 28/28; helpers ใหม่ 5 กรณี RED → ชุด Node สุดท้าย 33/33 ไม่มี skipped
- Browser เริ่มต้น 6 กรณี RED ก่อน UI → 6/6 GREEN; ขยาย CRUD/version/no-JS/double-submit แล้วผ่าน 7/7
- Review I1/M1: RED 2 กรณีตรงสาเหตุ → แก้และขยาย privacy เป็น 9/9; mutation test ของ visibility RED ตามคาด คืนโค้ดแล้ว dev/prod ผ่านทั้งชุด
- Browser dev พอร์ต 4000: 9/9; production พอร์ต 4001: 9/9
- Production flag-off boundary: 2/2; Identity regression: 13/13 ทั้งหมดผ่าน HTTPS พร้อม CA, cookie flags, CSRF และ hostile Host checks
- Backend ทั้งชุด: 783/783 ไม่มี skipped ใช้เวลา 17 นาที 8 วินาที รวม Production API boundary, race และ fault regression เดิม ไม่มีการแก้ backend production code ใน Task 8
- Backend/fixture Build: 0 warnings, 0 errors; `dotnet format --verify-no-changes --no-restore` ผ่านทั้ง solution และ fixture ซึ่งแยกจาก solution
- Next build และ ESLint `--max-warnings 0` ผ่าน; ไม่มีการเปลี่ยน production code หลัง final dev→build→prod เหลือเพียงรายงาน/สถานะแผน

คำสั่งตรวจใช้ Node 24.19.0 และ .NET SDK 10.0.401:

```sh
npm test
npm run lint
node infra/nginx/smoke-identity-https.mjs 4000 --e2e --spec tests/e2e/scopes.spec.ts
npm run build
node infra/nginx/smoke-identity-https.mjs 4001 --e2e --spec tests/e2e/scopes.spec.ts
node infra/nginx/smoke-identity-https.mjs 4001 --e2e --spec tests/e2e/scopes-boundary.spec.ts
node infra/nginx/smoke-identity-https.mjs 4001 --e2e
dotnet test backend/TPR10.sln --no-restore
dotnet build backend/TPR10.sln --no-restore
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
dotnet build backend/tests/TPR10.E2E.Fixture --no-restore
dotnet format backend/tests/TPR10.E2E.Fixture/TPR10.E2E.Fixture.csproj --verify-no-changes --no-restore
```

ลำดับ frontend เป็น dev 4000 → build → production 4001 ไม่มี dev เขียน `.next` ระหว่าง production acceptance Logs ชั่วคราว `/private/tmp/tpr10-m3t8-*.log` ไม่รวมใน Git

## งานถัดไป

Task 9 เมื่อผู้ใช้สั่ง: ตรวจ OpenAPI/ทั้ง branch, runbook และ Exit Gate พร้อมประเมิน Minor ค้างจากรายงาน Tasks 1–8 ก่อนตัดสินใจนำขึ้นใช้งาน ไม่เริ่มอัตโนมัติจากงานนี้
