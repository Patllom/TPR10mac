# รายงาน Module 2 — Task 1: Credentials และ Identity Schema

วันที่: 2026-09-23 (Asia/Bangkok)
ขอบเขต: Task 1 เท่านั้น ยังไม่ใช่ Exit Gate ของ Module 2
Branch: `codex/module-2-identity`
ฐานเริ่มต้น: `6c7f1aef13fd0e2a64dce27366440479b5d74946`

## สิ่งที่เพิ่ม

- Identity schema 13 ตาราง พร้อม migration `20260922174528_AddIdentityFoundation` โดยไม่แก้ migration Module 1
- Argon2id แบบ salted hash, fixed-time comparison, จำกัด format/cost ก่อนคำนวณ และจำกัดงานคำนวณพร้อมกัน 2 งานต่อ process
- รหัสผ่าน 15–128 Unicode scalar values ไม่ trim/normalize และปฏิเสธ UTF-16 ที่ไม่สมบูรณ์
- Local provider คืน internal user ID และสถานะต้องเปลี่ยนรหัสผ่าน ตรวจบัญชี active/lockout ปัจจุบัน แต่ยังไม่เพิ่มตัวนับ login ผิดหรือออก session
- Username trim + FormKC + uppercase invariant; unique normalized username, provider/subject และ role mappings
- FK แบบ restrictive; schema เตรียม hash/expiry/consumption/revocation สำหรับ Task ถัดไป ไม่ใช่การเปิด session/MFA/reset ใช้งานจริง
- ติดตั้ง service ใน DI ไม่มี Login API ใหม่ และไม่แตะหน้า Landing Page/พอร์ต 4000/4001
- NuGet lock files ทั้ง API/tests และเปิดสร้าง lock file ใน build settings โดยรักษา warnings-as-errors เดิม

## หลักฐาน TDD

| ชุด | RED | GREEN ก่อน review |
| --- | --- | --- |
| PasswordTests | 18 ล้มเหลวจากโครงที่ยังไม่ implement | 18 ผ่าน; เพิ่ม independent vector อีก 1 test |
| IdentitySchemaTests | 8 ล้มเหลวจากไม่มี identity schema | 8 ผ่าน |
| LocalIdentityProviderTests | 14 ล้มเหลวจากโครงที่ยังไม่ implement | 14 ผ่าน |
| IdentityRegistrationTests | host ยังไม่มี service | ผ่านใน suite รวม |

ทดสอบ schema บน PostgreSQL disposable database ใน Testcontainers ไม่เชื่อมฐาน production มีทั้ง migrate ฐานว่างและ upgrade จาก Module 1 พร้อมยืนยัน Audit เดิมยังอยู่ และ UPDATE/DELETE/TRUNCATE ถูกปฏิเสธ

เพิ่มค่า Argon2id ที่คำนวณอิสระด้วย Python `cryptography`/OpenSSL เพื่อไม่พึ่ง round-trip ของ hasher ตัวเดียว: password เป็นข้อมูลทดสอบสาธารณะ, salt `bytes(range(16))`, memory 65536 KiB, iterations 3, lanes 1 และผลลัพธ์ 32 bytes

## ผลตรวจ ณ 2026-09-22 17:51 UTC ก่อน review

| คำสั่ง | ผล |
| --- | --- |
| `dotnet test backend/TPR10.sln` | ผ่าน 59/59 |
| `npm test` | ผ่าน 17/17 |
| `dotnet restore backend/TPR10.sln --locked-mode` | ผ่าน |
| `dotnet build backend/TPR10.sln --no-restore` | ผ่าน ไม่มี warning/error |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | ผ่าน |
| `npm run lint` | ผ่าน ไม่มี ESLint warning/error |
| `npm run build` | ผ่าน Next.js production build |
| `git diff --check` | ผ่าน |
| `dotnet list backend/TPR10.sln package --vulnerable --include-transitive` | ไม่พบรายการจาก NuGet sources ณ เวลาตรวจ ไม่ใช่การรับรองว่าไม่มีช่องโหว่ |
| `npm audit --json` | ไม่ผ่าน: dependency เดิม 4 high + 1 critical |

## การตัดสินใจและข้อจำกัด

1. ทำเฉพาะ Task 1 ตามคำถามที่ผู้ใช้ยืนยัน ไม่อ้างว่า Tasks 2–9 ผ่านแล้ว
2. แยก `UsernameNormalizer` และเก็บขอบเขตข้อมูลใน `IdentityOptions`; รองรับ hash profile เดียวที่อนุมัติในแผน หากเปลี่ยน cost ต้องเพิ่มการอ่าน profile เก่า/rehash ไม่แก้ค่าแล้วทำให้บัญชีเดิมเข้าไม่ได้
3. ปรับ test round-trip เดิมจากสมมติว่ามี migration เดียว เป็นเปรียบเทียบรายการ applied migrations กับรายการที่โครงการมีจริง
4. งาน Argon2 ที่เริ่มคำนวณแล้วไม่สามารถยกเลิกในไลบรารี จึงรอจบก่อนคืน slot; API rate limit/lockout เป็น Task 2–3 และต้องพร้อมก่อนเปิด auth endpoints
5. ไม่สร้างผู้ดูแลจริง ไม่ migrate ฐานใช้งาน ไม่ออก cookie ไม่ merge หรือ push
6. ข้อกำหนด Security owner และ email delivery production ยังคงเป็น gate ของ Module 2/5 ตามแผน

## ความเสี่ยงจาก dependency เดิม

`npm audit` รายงาน `next` ระดับ critical และ `@next/eslint-plugin-next`, `eslint-config-next`, `glob`, `postcss` ระดับ high แนะนำให้แยกงานประเมิน/อัปเกรดก่อนนำระบบไป production ไม่รัน `npm audit fix --force` ใน Task นี้เพราะเสนอเปลี่ยน major version และไม่อยู่ในขอบเขตฐาน identity ฝั่ง backend

Node ปัจจุบัน 20.18.0 ทำให้เกิด engine warning กับ `eslint-visitor-keys` แม้ Test/Build/Lint ผ่าน ควรจัดการพร้อมงานอัปเดต tooling

## แหล่งตรวจ dependency ใหม่

- [NuGet: Konscious.Security.Cryptography.Argon2 1.3.1](https://www.nuget.org/packages/Konscious.Security.Cryptography.Argon2/1.3.1) และ package nuspec ยืนยัน license MIT; ติดตั้ง/ทดสอบจริงกับ .NET 10
- [Source และ license ผู้ผลิต](https://github.com/kmaragon/Konscious.Security.Cryptography)

## Code Review

Reviewer อิสระ Plato ตรวจช่วง `6c7f1ae..296c880` แบบ read-only และสรุปว่ารับ Task 1 ได้ ไม่พบ Critical/Important ไม่ใช่การรับทั้ง Module 2

ข้อ Minor ที่ยังไม่แก้ตามวิธี Native ของ Superpowers:

1. เพิ่ม malformed-hash tests ที่มี prefix/ความยาวถูกต้อง แต่ Base64 หรือ canonical padding ผิด เพื่อให้ตรวจถึง validation ชั้นใน ไม่หยุดที่ length guard
2. เพิ่ม negative tests สำหรับ hash length, session stage/expiry และ unique MFA factor ที่ยังไม่ revoked; implementation มี constraints แล้ว แต่ regression coverage ยังไม่ครบกลุ่มนี้

ไม่ยกระดับสองข้อนี้เป็น blocker ของ Task 1 เพราะ reviewer ตรวจพบว่า implementation มีการป้องกันอยู่และยังไม่มี auth endpoint เปิดใช้งาน แต่ต้องติดตามก่อนการรับ security gate

### คำวินิจฉัยของผู้พัฒนาต่อเรื่องที่ reviewer เว้นไว้

| เรื่อง | คำวินิจฉัยและผลหากผิด |
| --- | --- |
| Cookie เก่า/role ถูกถอด | ต้องพิสูจน์ Task 3/6 ก่อนเปิด protected API; schema รอบนี้ไม่รับรอง revocation ถ้าข้ามจะเสี่ยงใช้สิทธิ์เก่า |
| CSRF/Origin/forwarded host | ต้องพิสูจน์ Task 2/3; ถ้าข้ามก่อนเปิด mutation จะเสี่ยง request ปลอม |
| Reset/recovery/TOTP แข่งกัน | ต้องพิสูจน์ atomic consumption ใน Task 5/7; ถ้าข้ามเสี่ยงใช้หลักฐานซ้ำ |
| MFA/forced password change | ต้อง enforce stage ใน Task 4/5/7; ถ้าข้ามเสี่ยงข้ามขั้นยืนยันตัวตน |
| Next cache/return URL | ต้องพิสูจน์ Task 8; ถ้าข้ามเสี่ยงรั่ว session/redirect ภายนอก |
| Failed attempts/rate limit/account เปลี่ยนระหว่าง login | ต้องออกแบบ transaction และตรวจซ้ำตอนออก session ใน Task 3; provider อ่าน credential รอบนี้อย่างเดียวไม่รับรองการแข่งขัน ถ้าข้ามเสี่ยง brute force/stale credential |
| Account lifecycle/actor/audit transaction | ต้องพิสูจน์เมื่อมี account/role mutation ใน Task 4/6/7; ถ้าข้ามเสี่ยง mutation ไม่มี audit แม้ trigger เดิมยังอยู่ |
| Production/benchmark/keys/delivery/dependency | ไม่ผ่าน production gate ในรอบนี้; ต้องแก้ dependency และรับ policy/operation แยกก่อน deploy มิฉะนั้นเสี่ยงตามรายการที่ยังไม่ตรวจ |

บันทึกผล verification รอบสุดท้ายหลัง review เมื่อ 2026-09-22 17:58 UTC: backend 59/59, Node 17/17, backend build และ dotnet format, Next production build และ ESLint ผ่านทั้งหมด ไม่มีการแก้ production code ตาม review และไม่มีการ merge/push

ขั้นถัดไป: Task 2 — Pre-auth CSRF และ HTTPS transport โดยใช้ worktree/ledger เดิมสำหรับแผนนี้ต่อได้ ไม่ต้องทำ Task 1 ซ้ำ
