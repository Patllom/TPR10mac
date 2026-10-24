# การแก้ M2 และ PR7-R2

วันที่ 24 ตุลาคม 2569 — **แก้และตรวจรับ M2/PR7-R2 ในขอบเขตที่ระบุแล้ว** ผลสุดท้ายครบเวลา 11:59:15 น. (04:59:15 UTC) ไม่ใช่การอนุมัติ merge หรือ rollout

แยกผู้ลงมือกับผู้ตรวจอิสระครบ ไม่พบ Critical/Important คงข้อเสนอเพิ่มความครอบคลุมเทสต์ Minor สองรายการตามด้านล่าง HTTPS 504 ยังเปิดรอหลักฐานจริง และพักมือถือจริง/NAS จริง

## ขอบเขต

แก้ประสิทธิภาพการอ่านขอบเขตสิทธิ์/ประวัติ (M2) และคำเตือนสีของชุดทดสอบ (PR7-R2) แยกผู้ลงมือกับผู้ตรวจ โดยไม่ merge และไม่เริ่มโมดูลใหม่ งาน HTTPS 504 คงรอหลักฐานเหตุการณ์จริง พักการทดสอบมือถือจริงและ NAS จริง

งานอยู่ใน branch `codex/module-7b-design` บนฐาน `fd70b2166e30841bf27f5450501624081fe18c2a` ผู้ใช้อนุมัติให้ commit/push และอัปเดต PR #7 โดยไม่ merge ชุดส่งมอบแยกเฉพาะ M2/PR7-R2 ส่วนการเปลี่ยนแปลงตัวเก็บหลักฐาน 504 จากรอบก่อนยังเก็บใน working tree ไม่รวมใน commit นี้ ไม่ถือว่าการผ่านชุดทดสอบนี้แก้ 504 แล้ว

## PR7-R2: สาเหตุและแนวทางแก้

ชุดทดสอบส่ง environment ของผู้เรียกไปยัง child process ซึ่งอาจมีทั้ง `NO_COLOR` และ `FORCE_COLOR` อีกทั้ง Playwright รุ่นที่ติดตั้งบังคับ `FORCE_COLOR=1` ให้ worker ทำให้เกิดคำเตือนซ้ำแม้ผู้เรียกตั้งเพียง `NO_COLOR`

แก้โดยสร้างสำเนา environment เฉพาะ child แล้วเอา `NO_COLOR` ที่ขัดกันออก เก็บค่า `FORCE_COLOR` ที่ระบุไว้ รวมถึง `0` และค่าว่าง กรณีมีเพียง `NO_COLOR` จะแปลงเป็น `FORCE_COLOR=0` สำหรับ child โดยตรง ไม่แก้ environment ทั้งเครื่อง ไม่ปิด warnings และไม่เปลี่ยนพฤติกรรมการบังคับสีภายใน Playwright

เทสต์ใหม่เรียก Node subprocess และ Playwright worker จริง ตรวจว่าคำเตือนสีหาย แต่คำเตือนอื่นยังแสดง รวมทั้งตรวจว่า environment ต้นทางไม่ถูกเปลี่ยน RED 7 ล้มเหลว/2 ผ่าน → GREEN 9 ผ่าน ผู้ตรวจอิสระไม่พบ Critical/Important มีข้อสังเกต Minor ว่าเทสต์ย่อยยังไม่ได้ทดสอบการต่อสายของทุกจุดเรียกใน fixture โดยตรง จึงตรวจชุด HTTPS จริงเพิ่มเติม และคงข้อจำกัดความครอบคลุมเทสต์นี้ไว้ ไม่อ้างว่าเป็นข้อบกพร่องของโค้ดที่ยืนยันแล้ว

## M2: หลักฐานก่อนแก้

เทสต์ก่อนแก้ยืนยันว่า scope discovery เพิ่ม queries ตามจำนวน scopes เช่น เส้นทางคำขอใช้ 240 queries ที่ 4 scopes และ 1,168 queries ที่ 20 scopes การตรวจสิทธิ์ซ้ำเป็นงานจำเป็น แต่การอ่านข้อมูลทีละ scope/capability ทำให้เกิดการอ่านซ้ำจำนวนมาก

เป้าหมายคืออ่านเป็นชุดภายในขอบเขตตรวจสิทธิ์เดิม ไม่ใช้ cache สิทธิ์ข้ามคำขอ ไม่ลดการตรวจ session/MFA หรือการตรวจสิทธิ์ท้ายรายการ ไม่ยกเลิก identity lock และยังกรองสิทธิ์ก่อนแบ่งหน้า ผลเวลาและเวลารอ lock ในเครื่องทดสอบเป็นเพียง regression benchmark ไม่ใช่ SLA ระบบจริง

## M2: ผลแก้และหลักฐานเฉพาะส่วน

อ่าน scopes/identities เป็นชุดละ 100 และใช้ grant evidence predicate ร่วมกับเส้นทางตรวจทีละรายการ ลดการอ่านชื่อ/assignment/coverage ของหน้ารายการและข้อมูลประกอบประวัติเป็นชุด โดยคงการกรองข้อมูลและการอ่านสิทธิ์ใหม่ท้ายรายการ

| กรณีเพิ่มขนาด 4 → 20 | ก่อนแก้: queries | หลังแก้: queries | งบเทสต์เฉพาะ fixture |
|---|---:|---:|---:|
| ค้น scope ของ Grant | 184 → 888 | 18 → 18 | ไม่เกิน 24 |
| ค้น scope ของ Policy | 128 → 608 | 18 → 18 | ไม่เกิน 24 |
| ค้น scope ของ Request | 240 → 1,168 | 18 → 18 | ไม่เกิน 24 |
| Grant เพิ่มขนาดหน้า | 109 → 333 | 66 → 66 | ไม่เกิน 80 |
| ประวัติคำขอเพิ่มขนาดหน้า | 33 → 49 | 31 → 31 | ไม่เกิน 40 |

เทสต์กรอง candidates ที่ถูกถอนสิทธิ์เกินหนึ่ง batch ก่อนแบ่งหน้า: Policy/Grant มี 104 candidates ส่วน Request มี 103 (ไม่รวม company scope) หน้า 1–2 คืนเฉพาะ scopes ที่มีสิทธิ์ หน้า 3 ว่าง ไม่เปิดชื่อ scopes ที่ไม่มีสิทธิ์

เทสต์ตรวจ batch เทียบผลตรวจทีละรายการ พร้อมกรณี session/MFA/person/membership/grant ผิดเงื่อนไข รวมการถอนสิทธิ์หรือหมดอายุระหว่างการอ่าน และตรวจข้อมูล/flags ของ Grant list เทียบหน้ารายละเอียดเดิม ทั้ง human และ system history คงลำดับและข้อมูลเดิม

ทดสอบอ่านพร้อมกัน 4 คำขอที่ 4 และ 20 scopes พบผู้รอ advisory lock ใน PostgreSQL จริงครบทั้ง 4 ราย แต่ละ reader ใช้ 18 queries เวลารวมในตัวอย่างประมาณ 37–85 ms และระยะเวลาคำสั่งขอ lock ประมาณ 8–60 ms โดยค่าหลังรวม round-trip/overhead ด้วย **ไม่ใช่ pure lock wait, p95/p99 หรือ SLA** ชุดนี้ไม่ได้ยกเลิก lock เพื่อให้เร็วขึ้น

ประวัติการตรวจไม่ถูกกลบ: RED จับ query amplification ได้ ก่อนถึงแพตช์สุดท้ายมี EF translation failure ซึ่งแก้ลำดับ projection, fault injection 6 กรณีใช้ชื่อตารางผิดซึ่งแก้ในเทสต์ และ extended focused ผ่าน 235/236 โดยเทสต์เดิมตรวจพบ SQL `LIMIT` หายจาก history batch จึงเพิ่ม `Take(size)` โดยไม่ลด assertion หลังแก้รัน affected set ผ่าน 36/36

## ผลผู้ตรวจอิสระและสิ่งที่ติดตาม

- PR7-R2: Pauli ลงมือ / Archimedes ตรวจ — ไม่พบ Critical/Important
- M2: Dewey ลงมือ / Euclid ตรวจ — ข้อกำหนดและคุณภาพโค้ดผ่าน ไม่พบ Critical/Important; checksum backend ตรง 11/11 ไฟล์
- **M2-R1 (Minor):** เพิ่ม regression สำหรับสองบัญชี alias ที่มี session ถูกต้องและเป็น person เดียวกัน แต่มี grant เพียงบัญชีเดียว โค้ดปัจจุบันกรอง account ID ตรงตัว ผู้ตรวจยังไม่พบการข้ามสิทธิ์ จึงคงเป็นข้อเสนอเพิ่มความครอบคลุมเทสต์ ไม่ใช่บั๊กยืนยันแล้ว
- **R2-C1 (Minor):** เพิ่มเทสต์ระดับจุดเรียก environment ใน fixture ทุกทาง เทสต์ใหม่ครอบคลุม helper และ worker จริง แต่ไม่ได้เรียกทุกจุดใน fixture โดยตรง การตรวจ HTTPS จริงประกอบเป็นคนละระดับหลักฐาน
- Catalog/evidence ยังมีต้นทุน CPU/memory ตามจำนวนข้อมูลจริง ไม่ได้ทำให้ทุก endpoint มีต้นทุนคงที่ ไม่ได้แก้ picker/list ทุกตัวนอกขอบเขตนี้ ก่อน rollout ขนาดใหญ่ต้องวัดกับจำนวนโครงการและโหลดจริง

ผู้ตรวจไม่ได้รัน full gates ซ้ำ ผลการตรวจโค้ดไม่ใช้แทนผลรัน Test/Build/Lint ของ controller

## ผลตรวจที่มีแล้ว

| ชุดตรวจ | ผล | เวลา UTC |
|---|---|---|
| `npm test` | 155 ผ่าน / 0 ล้มเหลว / 0 ข้าม | 03:40:20 |
| เครื่องมือ diagnostics รวม child จริง | 26 ผ่าน / 0 ล้มเหลว / 0 ข้าม | 03:40:40 |
| `npm run lint` และ ESLint ฝั่ง infra | exit 0 | 03:40:42–43 |
| `npm run build` | exit 0 | 03:40:52 |
| ESLint เทสต์ environment ใหม่ | exit 0 | ตรวจแยกหลังชุดหลัก |
| Backend Build | exit 0; 0 warnings / 0 errors | 03:55:04 |
| Backend format (`--verify-no-changes`) | exit 0 | 03:55:20 |
| HTTPS Dev: Request / Policy / Grant | 10 / 13 / 11 ผ่าน พร้อม acceptance | 03:55:56–04:01:22 |
| Production rebuild | exit 0 | 04:01:31 |
| HTTPS Production: Request / Policy / Grant | 10 / 13 / 11 ผ่าน พร้อม acceptance | 04:05:32–04:10:27 |
| Backend ทั้ง solution | **2,590 ผ่าน / 0 ล้มเหลว / 0 ข้าม; exit 0** | 04:59:15 |

หลักฐานชุดแรก: `/private/tmp/tpr10-m2-r2-gates-cUf6g7` จาก runner 26187 ซึ่งจบ exit 0 ชุด HTTPS `/private/tmp/tpr10-m2-r2-gates-HOQvqk` จาก runner 85778 จบ exit 0 เช่นกัน รวม 68 case executions และ 6 acceptance ตรวจ log ทั้งหกไม่พบคำเตือนสีเป้าหมาย ไม่เปิด capture504 ไม่ลด certificate validation หรือเพิ่ม retry/timeout ชุด build มีเพียง npm update notice ซึ่งไม่ได้ปรับรุ่นตามข้อความนั้น

Backend ทั้งชุดเริ่ม 03:55:20 UTC และ runner 49094 จบ exit 0 เวลา 04:59:15 UTC ใช้เวลา 1.0652 ชั่วโมงตาม test runner หลักฐาน `/private/tmp/tpr10-m2-r2-gates-p3G1BW` ผลรอบนี้เป็นการรันใหม่จริง ไม่ใช้ยอด 2,567 จากรอบก่อนมาแทน

## คำสั่งตรวจรับหลัก

รันจาก worktree ของ branch ที่ระบุ ใช้ Node 24.19.0, .NET 10 และ PostgreSQL fixture แยก ไม่มีการเชื่อมข้อมูลจริง

```sh
npm test
npm run lint
npm run build
TPR10_HTTPS504_REAL_CHILDREN=1 node --test --test-concurrency=1 infra/nginx/*.test.mjs
dotnet build backend/TPR10.sln --no-restore --verbosity minimal
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'
git diff --check
```

ESLint ฝั่ง infra และเทสต์ environment ใหม่ตรวจแยกผ่านด้วย ชุด HTTPS เรียก `runHttpsSmoke` ของ Request/Policy/Grant ที่พอร์ต 4000 และ 4001 ทั้งสองโหมด โดยล้าง capture/grep/diagnostics flags และ rebuild ก่อน Production ไม่ใช่การเปิดติดตาม 504

ตรวจ checksum หลังชุดทดสอบครบ: backend ตรงกับผู้ตรวจ 11/11 ไฟล์ และ helper/เทสต์/fixture R2 ตรงทั้งสามไฟล์ พอร์ต fixture 4000/4001/4443 ไม่มี listener หลังชุด HTTPS จบ ไม่มีการแก้ source ระหว่าง final gates; แก้เฉพาะรายงานหลังผลออก

## ตรวจซ้ำก่อน commit/push

ผู้ใช้อนุมัติเผยแพร่โดยไม่ merge ในรอบถัดมา จึงตรวจชุด stage แยก 17 ไฟล์ ไม่รวม capture 504 เดิม และรันเพิ่มดังนี้:

- 05:06:30 UTC: Frontend 155/155, diagnostics 26/26, Frontend Build/Lint, infra lint และ diff-check ผ่านใหม่ หลักฐาน `/private/tmp/tpr10-m2-r2-gates-eUF9mN`
- Backend `FieldAdvancePerformanceTests` ผ่าน 24/24 ไม่มีล้มเหลวหรือข้าม ครอบคลุม partial class ในไฟล์ security ด้วย ครั้งแรกใช้ชื่อไฟล์เป็น filter จึงไม่พบเทสต์ ไม่นับครั้งนั้นเป็นผลผ่าน
- 05:10:34 UTC: ส่งออก fixture จาก index ไปโฟลเดอร์ชั่วคราว โดยไม่มี capture integration แล้วทดสอบ HTTPS Production 4001 ฝั่ง Request ผ่าน 10/10 พร้อม TLS/cookie/CSRF acceptance และ cleanup exit 0 หลักฐาน `/private/tmp/tpr10-staged-fixture-pJ56zO/https4001-request.log` ใช้แอป/เทสต์ใน working tree โดยปิด capture ไม่อ้างว่าเป็นการรันทุกอย่างจาก clean checkout
- Backend 11 ไฟล์และ helper/เทสต์ R2 มี checksum ตรงชุดที่ผ่าน full gates เดิม ไม่ได้รัน Backend 2,590 หรือ HTTPS 68 ใหม่ทั้งหมดในรอบเผยแพร่นี้
- Push รวม commit ฐาน `fd70b21` ของเทสต์ PR7-R1 ที่มีอยู่ใน branch แต่ยังไม่เคย push โดยไม่แก้ประวัติ ส่วน capture code และรายงานวินิจฉัย 504 ที่ยังไม่ commit คงอยู่ในเครื่อง

## สรุปการส่งมอบและสิ่งที่ยังเปิด

- ปิดการแก้ query amplification ที่วัดใน M2 และคำเตือนสี PR7-R2 ตามขอบเขต/งบ regression ข้างต้น ไม่อ้างว่าเพิ่มประสิทธิภาพทุก endpoint หรือผ่านโหลด production จริง
- คง M2-R1 และ R2-C1 เป็นข้อเสนอเพิ่มเทสต์ระดับ Minor ไม่พบข้อบกพร่องสำคัญที่ยังค้างจากการตรวจ patch นี้
- HTTPS 504 ยังไม่ทราบสาเหตุและยังไม่ปิด รอหลักฐานเหตุการณ์จริง ไม่ใช้ผล HTTPS ที่ผ่านครั้งนี้เป็นหลักฐานว่าแก้ 504 แล้ว
- มือถือจริงและ NAS จริงยังพักไว้; R1-L1/L2 จากรายงาน 7B เดิมยังเป็นข้อจำกัดการสังเกตที่ไม่ได้แก้ในรอบนี้
- ส่งมอบผ่าน commit/push และอัปเดต PR #7 ตามคำอนุมัติรอบถัดมา โดยไม่ merge; เก็บ capture code ที่ค้างจากรอบก่อนแยกไว้ ไม่ย้อนหรือลบทิ้ง ผลตรวจเต็มข้างต้นรันบน working tree ที่มี capture code แต่ปิด capture ส่วน fixture ที่ stage แยกออกเหลือเพียงการต่อ helper environment สามจุดและ import

รายงานผู้ลงมือ/ผู้ตรวจในเครื่อง: `/private/tmp/tpr10-m2-implementation.md`, `/private/tmp/tpr10-m2-review.md`, `/private/tmp/tpr10-r2-implementation.md`, `/private/tmp/tpr10-r2-review.md` หลักฐานใน `/private/tmp` เป็นไฟล์ชั่วคราว ไม่ใช่ที่เก็บถาวร ผลสรุปที่จำเป็นถูกเก็บในรายงานโครงการฉบับนี้แล้ว
