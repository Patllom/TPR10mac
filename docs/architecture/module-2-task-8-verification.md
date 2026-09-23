# หลักฐาน Task 8: Landing → Login → Portal

สถานะ: ส่งมอบ Task 8 เมื่อ 2026-09-23 ผ่าน TDD, review อิสระหนึ่งรอบ และ Test/Build/Lint ครบ มี Minor ที่เปิดเผย 1 ข้อ; ยังไม่ผ่าน Security Exit Gate ของ Module 2

## ขอบเขต

- Public Landing อยู่ `/` ลิงก์พนักงานไป `/login`; ภายในใช้ `/portal` บน origin เดียว
- API เป็น session authority; Next อ่าน session ฝั่ง server จาก origin ที่ตั้งค่าเท่านั้น ไม่ตาม redirect ไม่ cache และส่งเฉพาะ session cookie
- แยก 401 ออกจากบริการล่ม: 401 ไป Login ส่วน outage แสดงหน้าบริการไม่พร้อมโดยไม่แสดงข้อมูลบัญชี
- Password-change และ MFA มาก่อน returnTo; รับเฉพาะ path ภายใน Portal รวม fallback สำหรับ malformed/repeated query
- ฟอร์มภาษาไทย มี label/focus/สถานะรอ ปิด input/button ก่อน hydration และป้องกัน submit ซ้ำ
- MFA enrollment/manual key/URI, challenge และ recovery; recovery code แสดงครั้งเดียว ไม่ใช้บริการ QR ภายนอก
- Reset รับ token จาก fragment เข้า memory แล้วล้าง URL รวมกรณีเปิด fragment ใหม่ในเอกสารเดิม ไม่เก็บ token ใน storage/query/log
- Account มี logout, logout-all ของตัวเอง, เปลี่ยนรหัสผ่านและ optional MFA สำหรับ staff
- เพิ่ม `POST /api/v1/auth/logout-all` แยกจาก admin endpoint เดิม ผูกผู้ใช้จาก session ไม่รับ target จาก body; revalidate/revoke/security-version/audit ใน transaction เดียว

## การอัปเกรดที่อนุมัติ

ผู้ใช้อนุมัติให้อัปเกรด framework และ compatibility ก่อนทำ Task 8 ต่อ:

- Next.js 14.2.35 → 15.5.26, React/React DOM และ types → 19.3.0
- eslint-config-next → 15.5.26; lucide-react → 0.468.0 ซึ่งรองรับ React 19
- Override เฉพาะ PostCSS ภายใน Next เป็น 8.5.28 เนื่องจาก audit หลังอัปเกรดยังพบรุ่นเก่า; ตรวจ CSS ผ่าน production build
- เปลี่ยน `next lint` ที่ deprecated เป็น ESLint CLI ไม่ลดกฎและใช้ `--max-warnings 0`
- `.nvmrc` ใช้ Node 22.23.2; ไม่แก้ Node ระดับเครื่อง ใช้ runtime แยกสำหรับ verification
- Next เพิ่ม `target: ES2017` และ generated route types ตาม compatibility ของรุ่นใหม่

อ้างอิง [คู่มืออัปเกรด Next 15](https://nextjs.org/docs/app/guides/upgrading/version-15) และ [ประกาศแพตช์ AVIF](https://github.com/vercel/next.js/security/advisories/GHSA-2xp9-vwfh-vxw4) การไม่มี advisory ใน audit ไม่ใช่การรับรองว่าไม่มีช่องโหว่ทุกชนิด

## TDD และผลที่ตรวจแล้ว

- Baseline Node 23/23; audit เดิม 5 packages (Critical 1, High 4) → หลังอัปเกรด/override audit 0
- Anonymous browser RED: `/portal` ไม่ redirect → GREEN หลังเพิ่ม server guard/Login
- Auth helper 5 tests เริ่มจาก module ยังไม่มี → ผ่าน 5/5; repeated returnTo เพิ่ม case ที่ทำให้ `startsWith` ล้ม → แก้ type guard แล้วผ่าน
- Self logout-all: 3 RED เพราะ endpoint 404 และ 1 CSRF protection เดิมผ่าน → 4/4 GREEN รวม cross-account isolation และ audit rollback
- Browser flow เริ่ม 5 fail/5 pass: หน้า account/MFA/change/reset ยังไม่มี และ selector alert ชน Next route announcer; แก้ selector ให้เจาะ form
- Hydration regression: no-JavaScript test พิสูจน์ input ยัง enabled → disable จน handler พร้อม; browser logout race ผ่านหลังแก้
- Reset replay เปิด fragment ซ้ำในหน้าเดิมพิสูจน์ว่า effect ครั้งเดียวไม่รับลิงก์ใหม่ → เพิ่ม hashchange handler
- Backend full regression 309/309, 0 skipped; .NET build 0 warnings/errors และ format ผ่าน
- Node 28/28 และ lint ผ่านระหว่างพัฒนา; Next production build ผ่าน หน้า auth/portal เป็น dynamic
- ก่อน review: E2E 13/13 ผ่านทั้ง 4000 (41.6 วินาที) และ 4001 (28.2 วินาที), TLS smoke ทั้งคู่ผ่าน; Node28/Lint/Build/Audit0 ผ่านหลังแก้ล่าสุด
- เคยพบ dev hydration ไม่พร้อมหนึ่งครั้งหลัง 12 tests; เพิ่ม diagnostic แล้วรอบถัดไปผ่าน 13/13 พบ asset abort ระหว่าง navigation เท่านั้น ยังไม่ยืนยันสาเหตุของความไม่สม่ำเสมอ และไม่ถือการ rerun ผ่านเป็นการพิสูจน์ root cause
- Review อิสระเสร็จหนึ่งรอบ: Critical 0 / Important 0 / Minor 1
- หลัง review: backend 309/309, 0 skipped (4 นาที 2 วินาที); .NET build 0 warnings/errors และ format ผ่าน; Node 28/28, Lint, Next Build, Audit 0, production E2E 13/13 (28.4 วินาที) พร้อม TLS smoke ผ่านทั้งหมด ไม่มี production code เปลี่ยนหลัง review
- ปิด ledger ผ่าน `task-done` ด้วย E2E production 13/13 (28.2 วินาที) และ HTTPS acceptance อีกครั้ง; implementation commit `af6293a` ยังไม่ push/merge เก็บ worktree สำหรับ Task 9

## ข้อวินิจฉัยและต้นทุนหากผิด

1. ทำเฉพาะ Task 8 + framework upgrade ที่อนุมัติ ไม่ push/merge หรือทำ Task 9; เก็บ worktree/ledger ต่อ — ถ้าขอบเขตต่างจากผู้ใช้ จะยังไม่ส่งมอบทั้งโมดูล
2. ใช้ Next 15 patch ล่าสุดที่ตรวจพบแทนข้ามไป 16 พร้อม React 19 ตาม App Router — ต้องติดตาม maintenance และแพตช์ต่อไป
3. PostCSS override เฉพาะ dependency ของ Next — อาจมี compatibility นอก CSS ที่ build นี้ใช้ ต้องทวนเมื่ออัปเกรด Next ครั้งหน้า
4. Docker image ของ Playwright ดาวน์โหลดล้มจาก DNS จึงใช้ Firefox runtime บน host พร้อม policy นำเข้า CA เฉพาะ disposable profile ไม่แก้ macOS trust และไม่ ignore TLS — ยังไม่ใช่หลักฐาน Chrome/WebKit
5. SSR fixture เรียก API ผ่าน configured loopback HTTPS proxy พร้อม CA เฉพาะ Node process ไม่เปิด API port — deployment จริงต้องกำหนด private origin/trusted hop ถูกต้อง ห้าม derive จาก incoming Host
6. E2E ใช้ test-only executable สร้าง Argon2 hash และ seed เฉพาะ PostgreSQL container ใหม่ ไม่มี seed endpoint ใน API — fixture ไม่ใช่กระบวนการ provision ผู้ใช้ production
7. E2E เพิ่ม CSRF rate budget เฉพาะ fixture เป็น 1000/IP และ 10000/global เพื่อตรวจหลาย flow ในเวลาสั้น; default/API integration เดิมไม่เปลี่ยน — E2E ไม่พิสูจน์ rate-limit production
8. Self logout-all แยกจาก admin policy และอนุญาต restricted stage เพื่อให้ผู้ใช้ยุติ session ได้ — ไม่ใช้แทน admin revoke และไม่เพิ่มสิทธิ์ business
9. ใช้ full navigation หลัง mutation เปลี่ยน session และไม่ใช้ client router cache สำหรับลิงก์ภายใน Portal; private view ซ่อน snapshot เมื่อ pagehide และ reload เมื่อกลับจาก bfcache — ไม่ใช่ push notification เพิกถอนหน้าเปิดค้างแบบ realtime
10. Reset email production ยังปิดจน Module 5; UI ไม่อ้างว่าส่งอีเมลแล้ว — ผู้ใช้จริงต้องพึ่งช่องทางผู้ดูแลที่ยืนยันตัวตน
11. ยังใช้ policy/transaction locks ที่อนุมัติสำหรับพัฒนา ไม่รับรอง load/distributed rate limiting/key backup หรือความพร้อม production — ต้องผ่าน security owner/Task 9

## Code Review

ผู้ตรวจ Einstein (`01a0cd74-7430-7b82-b9fd-ec8c3ce441a1`) ตรวจแบบ fresh context/read-only หนึ่งรอบ ช่วง `cc22bd1..af6293a` รวม dependency และ interaction กับ API เดิม ผล Critical 0 / Important 0 / Minor 1; ไม่ตรวจซ้ำรอบสอง

ผู้ตรวจรัน helper 5/5 และ `git diff --check` เอง ส่วน full backend/Node/Build/Lint/Audit/E2E เป็นหลักฐานที่ผู้ทำหลักรัน ไม่อ้างว่าผู้ตรวจรันทั้งหมดซ้ำ

### Minor ที่เลื่อนไปแก้แยก

เมื่อ session หมดอายุ Next server fetch รับ 401 แต่ไม่ได้ส่ง Set-Cookie ลบ cookie กลับ browser เมื่อกด login ครั้งแรก CSRF issuer จะลบ cookie เก่าและตอบ 403 โดยยังไม่ส่ง login POST ผู้ใช้ต้องกดอีกครั้ง จึงเป็น UX ไม่ใช่การข้าม authentication ตรวจโค้ด `auth-client.ts`/`session-fetch.ts` สอดคล้องกับ finding; ผู้ตรวจจำลอง transport ได้ครั้งแรก 403/POST 0 และครั้งถัดไป 200/POST 1 ยังไม่ใช่ browser regression ของกรณีนี้

ตามกติกา Minor จดไว้ไม่แก้ในรอบนี้ งานถัดไปควรเพิ่ม regression login หลัง expiry และจัดการ stale-cookie recovery โดยไม่ retry mutation อัตโนมัติ

### ข้อที่ review ไม่รับรองและผลหากวินิจฉัยผิด

- Tasks 1–7 ตรวจเฉพาะ interaction ไม่ทวน concurrency/replay matrix และ Minor เดิมทั้งหมด — ถ้าสมมติฐานผิดอาจตกหล่น regression ข้าม task จึงคง full backend suite
- Task 9/OpenAPI/Security Exit Gate รวม deployment/trusted proxy/TLS, provisioning, capacity/Argon2, distributed limiting, key backup/rotation และ operational policy ยังไม่รับรอง — ถ้าข้ามอาจ deploy ก่อนพร้อม
- Module 5 email/delivery/retry/dedup/exactly-once และ Module 3 organization scope ไม่อยู่ Task 8 — ถ้าสื่อสารผิดผู้ใช้อาจคาดหวังระบบส่งเมลหรือจำกัดองค์กรที่ยังไม่พร้อม
- Chrome/WebKit และ browser-history ทุกสภาวะนอก Firefox ไม่ได้ตรวจ — ถ้าพฤติกรรมต่างต้องเพิ่ม acceptance บน browser เป้าหมาย
- Dev hydration ที่ไม่สม่ำเสมอยังไม่มี root cause — rerun ผ่านไม่รับรองว่าไม่มีปัญหา อาจพบซ้ำระหว่างพัฒนา
- Audit 0 และ build ไม่รับรองไม่มีช่องโหว่ทั้งหมดหรือ compatibility ทุกกรณีของ override — ต้องติดตาม advisory และทวนเมื่ออัปเกรด

ไม่มี Critical/Important ที่ค้าง และไม่มีการแก้ production code หลัง review; เอกสารฉบับส่งมอบปรับโดยผู้ทำหลัก ไม่ได้อ้างการตรวจอิสระรอบที่สอง
