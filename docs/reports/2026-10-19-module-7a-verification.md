# รายงานตรวจรับ Module 7A

อัปเดต 24 ตุลาคม 2569: M2 แก้ query amplification และผ่านการตรวจอิสระ/Test/Build/Lint ใหม่แล้ว ดู [รายงาน M2 และ PR7-R2](2026-10-24-m2-pr7-r2-verification.md) คงข้อเสนอเพิ่มเทสต์ M2-R1 และข้อจำกัดการวัดโหลดจริง ไม่ใช่การอนุมัติ rollout ข้อความด้านล่างเป็นประวัติผลตรวจเดิม

สถานะ ณ 22 ตุลาคม 2569: Task 10 ผ่านการตรวจรับเพื่อส่งมอบใน branch แล้ว แก้ Minor เดิมทั้งสองเรื่องและข้อสำคัญจากรีวิวครบ ไม่มี Critical/Important ค้าง มี Minor ใหม่สองเรื่องที่ตัดสินเก็บเป็นงานติดตามตามรายละเอียดด้านล่าง ไม่ใช่การรับรองเปิดใช้งานจริง ชื่อไฟล์อ้างวันที่แผน ไม่ใช่วันที่รันเทสต์

## ขอบเขต

ตรวจระบบกติกาและสิทธิ์เงินทดรองออกสนาม ได้แก่ฉบับกติกาบริษัทและโครงการ การอนุมัติแยกผู้แก้ การมอบหมายสิทธิ์สามคน สิทธิ์ปัจจุบัน ประวัติ และหน้าจอไทย ไม่รวมคำขอ จ่าย เคลียร์ คืนเงินจริงใน 7B–7D

ใช้ branch `codex/module-7a-policy-permissions` ฐาน Module `d657c16408ae52cb1ba43d991a438ea5c0daf895` ฐาน Task 10 `dcf2ae0ab440c79d9877b67f57d4f961cc1f79ef` แยกผู้ลงมือและผู้ตรวจ ไม่มีการ push หรือ merge ในขอบเขตงานนี้

โค้ด/เทสต์/runbook ที่ตรวจและเทียบ hash ครบอยู่ใน commit `d0262ea` (`test: verify field advance policy delivery`) จำนวน21ไฟล์ เอกสารสรุปสถานะแยก commit ภายหลังโดยไม่เปลี่ยนโค้ดที่ทดสอบ เก็บ branch/worktree ไว้ให้ผู้ใช้เลือกขั้นตอนถัดไป ไม่ล้างหลักฐาน SDD

## เกณฑ์ตรวจรับ

ตารางนี้ระบุชุดหลักฐานที่ตรวจประกอบ full suite และ whole-branch review / scoped re-review แล้ว ผลมีข้อจำกัดและงานติดตามที่ระบุด้านล่าง

| เกณฑ์ | พฤติกรรมที่ตรวจ | ชุดหลักฐานหลัก |
|---|---|---|
| A01 | ผู้ร่วมแก้และบัญชีอื่นของบุคคลเดียวกันอนุมัติฉบับตนไม่ได้ | FieldAdvancePolicyTests, policy E2E |
| A02 | ขอบวงเงินและลำดับครบ ไม่เพิ่มอำนาจจากยอดที่เกินกติกา | FieldAdvanceRulesTests, FieldAdvanceReadTests |
| A03 | ฐานโครงการตรึงจนผ่านอนุมัติและ snapshot เก่าไม่เปลี่ยน | FieldAdvanceResolverTests, FieldAdvancePolicyTests |
| A04 | หมวด อัตรา หน่วย และประเภทหลักฐานตรงข้อกำหนด | FieldAdvanceRulesTests, policy E2E |
| A05 | ผู้เสนอ ผู้ตรวจ และผู้รับเป็นสามบุคคล | FieldAdvanceGrantTests, FieldAdvanceBootstrapTests, grant E2E |
| A06 | การขยายสิทธิ์ผ่านอนุมัติ ไม่อ้อม role/account/scope เดิม | FieldAdvanceBypassTests, FieldAdvanceDeliveryTests |
| A07 | admin/สมาชิกทีมไม่รับสิทธิ์อัตโนมัติ ไม่อ่านต่างขอบเขต | FieldAdvanceAccessTests, FieldAdvanceReadTests, grant HTTP/E2E |
| A08 | ถอน/หมดอายุ/ปิดขอบเขตไม่คงสิทธิ์เก่า ไม่ล้างประวัติบุคคล | FieldAdvanceBypassTests, FieldAdvanceDeliveryTests, grant E2E |
| A09 | คำสั่งซ้ำและอนุมัติพร้อมกันไม่สร้างผลซ้ำ | FieldAdvanceGrantRaceTests, FieldAdvancePolicyRaceTests, FieldAdvanceAtomicityTests |
| A10 | ข้อมูล audit และ receipt ย้อนคืนพร้อมกันเมื่อผิดพลาด | FieldAdvanceAtomicityTests, GrantRaceTests, PolicyRaceTests, BootstrapTests |
| A11 | หน้าจอไทยและสถานะตามเวลา รวมต่ออายุจริง | Node tests, policy/grant E2E |
| A12 | บัญชี MFA โครงสร้าง สิทธิ์และลงเวลาเดิมไม่เสีย | Backend ทั้ง solution, Node ทั้งชุด, HTTPS regression แยกโหมด |

## งานที่ตรวจครบใน Task 10

- แก้ Minor รายละเอียดสิทธิ์ไม่รีเฟรชตามเวลาแม้ version เดิม และเพิ่ม E2E ที่เปลี่ยนวันสิ้นสุดจริง พร้อมรักษาสิทธิ์เดิม ผ่านทั้ง Dev และ Production
- ตรวจ replay ข้ามขอบเขต สิทธิ์ที่ถูกถอน alias บุคคล และการปิดแล้วเปิดบริษัท/โครงการ
- ทดสอบ migration จากฐานเดิม สำรองและกู้คืนฐานทิ้งได้ ย้อน migration เมื่อว่าง และปฏิเสธการย้อนเมื่อมีประวัติการเงิน
- ตรวจ Task 10 โดย Huygens และตรวจทั้ง branch/ชุดแก้โดย Banach แยกจากผู้ลงมือ Euler; ปิด Important I1–I3 และ Minor lineage จากรีวิว พร้อมเก็บ residual ตามคำตัดสินด้านล่าง

## ประวัติผลระหว่างดำเนินการ — ไม่ใช้แทนผลฉบับสุดท้าย

- ผู้ตรวจ Task 10 รับรองชุดแก้เริ่มต้นห้าไฟล์แล้ว แต่ผู้ตรวจทั้ง branch พบ Important สามเรื่อง: replay ต้องตรวจอำนาจเหนือข้อมูลเป้าหมายปัจจุบัน, ห้ามผูกร่างโครงการกับฐานบริษัทเก่าที่ไม่มีสิทธิ์อ้างอิงเพื่ออ่านข้อมูล, และหน้า Policy ที่เปิดค้างต้องตรวจสิทธิ์ซ้ำตามเวลา ขณะนี้ส่งผู้ลงมือแก้พร้อมเทสต์แล้ว
- Minor เพิ่มเติมจากการตรวจทั้ง branch คือความซับซ้อนการเดินประวัติสายฉบับและจำนวน query ต่อรายการ ยังไม่ถือว่าปิดจากผลตรวจเฉพาะ Task ก่อนหน้า
- ก่อนชุดแก้จากรีวิว: Node 108 ผ่าน; backend ใหม่ 14 และ invariants เดิม 106 ผ่าน; HTTPS Production รวม 112 ผ่าน; Build และ Lint ของผู้ลงมือผ่าน ผลเหล่านี้ต้องไม่ใช้แทนการตรวจฉบับแก้สุดท้าย
- Dev scopes ครั้งแรกผ่าน 10/11 โดย timeout ขณะเปิด `/login`; รันทวนทั้งชุดโดยไม่เปลี่ยนโค้ดหรือ timeout ผ่าน 11/11 แต่ยังไม่ทราบสาเหตุแน่ชัด ไม่อ้างว่าแก้ root cause แล้ว
- Dev capture ผ่าน 26/27 โดยรอปุ่มถ่ายภาพไม่พบ และ snapshot แสดงข้อมูลบริการไม่ถูกต้อง ตรวจพบ fixture สร้างเวลาเริ่มกับเวลาหมดอายุจากคนละการอ่านนาฬิกา การทดลองกับ parser จริงยืนยันว่าเหลื่อม 1 ms จะถูกปฏิเสธ ส่วนการใช้เวลาฐานเดียวผ่าน กำลังเพิ่ม regression และแก้ fixture โดยไม่ผ่อน validation ผลทดสอบเดิมเก็บไว้

## ผลตรวจซ้ำและคำตัดสินรายการคงเหลือ

ผู้ตรวจทั้ง branch และผู้ตรวจซ้ำเป็นคนละคนกับผู้ลงมือ ตรวจซ้ำชุดแก้ที่ตรึง 21 ไฟล์แล้ว: ปิด Important I1–I3 และ Minor M1 เรื่องสายประวัติ ไม่พบ Critical/Important ที่ยังเปิดในชุดแก้ ผู้ตรวจให้ผล Conditional Yes ระหว่างรอ full gates ซึ่ง controller ตรวจผ่านภายหลังครบแล้วตามตาราง ไม่ใช่อำนาจ merge หรือ rollout

| รายการคงเหลือ | คำตัดสินของ controller / ผลกระทบ / งานติดตาม |
|---|---|
| M2 จำนวน query ในการค้นขอบเขตสิทธิ์และการอ่านประวัติ | เป็นความเสี่ยงจริงระดับ Minor พักไว้เป็นงานประสิทธิภาพก่อน rollout หลายโครงการ ไม่ใช่การเปลี่ยนสิทธิ์หรือความถูกต้องของธุรกรรม เจ้าของงาน: ผู้พัฒนา backend การเงิน ต้องวัดจำนวน scope/page/history และ concurrent requests ที่ใกล้ใช้งานจริง กำหนด query/latency/lock-wait budget แล้ว batch ภายในขอบเขตตรวจสิทธิ์เดิม หากประเมินผิดอาจต้องชะลอ rollout และปรับ query; ไม่มีการรับรอง SLA จากการทดสอบนี้ |
| M3 หน้า Policy แจ้ง conflict หลังอนุมัติ replacement สำเร็จ | เป็นข้อบกพร่อง UX จริงระดับ Minor การอนุมัติและประวัติที่บันทึกสำเร็จไม่ถูกย้อน แต่ poll อาจพบ source version เก่าแล้วล้างหน้า ผู้ใช้โหลดใหม่ได้ พักแก้ในงาน UI ถัดไป โดย refresh source หลัง command สำเร็จและเพิ่ม post-approval dwell regression ห้ามปิด version check หากประเมินผิดต้องทบทวน flow และแก้ก่อนเปิดใช้งาน ไม่ถือว่าผู้ใช้อนุมัติข้อยกเว้นแล้ว |

เหตุผลที่ไม่เปิดรอบแก้ใหม่: จบ consolidated fix wave และ scoped re-review ตามกระบวนการ Superpowers แล้ว รายการคงเหลือไม่ใช่เงื่อนไขเชิงโครงสร้างสำหรับสร้างคำขอ 7B และไม่พบการข้ามสิทธิ์หรือสูญเสียข้อมูลจากสองรายการนี้ จึงบันทึกพร้อมเจ้าของและต้นทุนหากตัดสินผิด แยกการพัฒนาใน branch ออกจากการเปิดใช้จริง

ผลวัด local PostgreSQL: policy list 4/20 ฉบับใช้ 17/17 queries; timeline 18/18; grant candidates page-size1 ใช้ 67/67 หลัง batching ส่วน discovery 4/20 scopes ยังใช้ 184/888 queries และ 104.5/381.6 ms ตาม affected TRX ตัวเลขนี้ไม่ใช่ p95 หรือเวลาถือ lock แยก และไม่ใช่ production SLA

Controller รันทวน frontend บนไฟล์ตรึงแล้ว: Node 109/109, Build, Lint, diff check และ hash 21/21 ผ่าน (session 26736 exit 0) Backend build ผ่าน 0 warnings/errors; full backend session 98183 จบ exit 0 ผ่าน 2,258/2,258 ไม่ล้มเหลวหรือข้าม ใช้เวลา 52.7730 นาที จากนั้น session 38237 จบ exit 0: format ทั้ง solution ผ่าน, grant HTTPS Dev11/Production11, production rebuild และ capture Production27 ผ่าน พร้อม hash 21/21 ตรงชุดรีวิว

## คำสั่งและผลตรวจชุดเต็ม

ใช้ Node 24.19.0 และ .NET SDK จาก runtime ที่กำหนดใน workspace ใช้ Docker/PostgreSQL ทิ้งได้ ไม่เชื่อมฐานจริง

| คำสั่ง | ผลจริง |
|---|---|
| `dotnet build backend/TPR10.sln --no-restore --verbosity minimal` | exit 0; 0 warning / 0 error |
| `dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'` | exit 0; 2,258 ผ่าน / 0 ล้มเหลว / 0 ข้าม; 52.7730 นาที |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | exit 0; ไม่พบการเปลี่ยนรูปแบบที่ต้องทำ |
| `npm test` | exit 0; 109 ผ่าน / 0 ล้มเหลว / 0 ข้าม |
| `npm run build` | exit 0; production build และ type check ผ่าน |
| `npm run lint` | exit 0; ใช้ `--max-warnings 0` |
| `git diff --check` และ SHA-256 ของชุดส่งตรวจ | exit 0; 21/21 ไฟล์ตรงกับผู้ตรวจ |

หลักฐานใน workspace: `.superpowers/sdd/2026-10-19-module-7a-policy-permissions/task-10-full-backend.log`, `task-10-controller-{node,build,lint,format}.log`, `task-10-final-fix-hashes.txt`; รายงานผู้ตรวจ `module-7a-final-review-result.md` และ `module-7a-final-rereview-result.md` ผลการรันเป็นวันที่ 22 ตุลาคม 2569 ไม่ใช่วันที่ในชื่อไฟล์แผน

## HTTPS และการทดสอบย้อนหลัง

ใช้ Firefox กับ CA ที่เชื่อถือเฉพาะ profile ทดสอบ ผ่าน proxy4443 ไป Dev4000 หรือ Production4001 ทีละชุด ไม่ปิดการตรวจ certificate ทุกชุดผ่าน TLS/cookie/CSRF/hostile-host acceptance ประกอบกัน ไม่ใช้ build อย่างเดียวแทน browser test

| ชุด | Dev ผ่าน | Production ผ่าน |
|---|---:|---:|
| Identity | 13 | 13 |
| Scopes | 11 | 11 |
| Attendance directory | 7 | 7 |
| Attendance storage | 3 | 3 |
| Attendance capture | 27 | 27 |
| Attendance history | 11 | 11 |
| Attendance corrections | 19 | 19 |
| Field advance policy | 13 | 13 |
| Field advance grant | 11 | 11 |
| Scopes boundary (เฉพาะ Production ตามชุดเดิม) | — | 2 |
| รวมกรณีในแต่ละโหมด ไม่รวมรอบรันซ้ำ | **115** | **117** |

ตารางแสดงผลรอบที่ผ่านใน Task10; ชุดเดิมที่ไม่กระทบตรวจใน regression รอบก่อน final-fix wave ส่วน policy/grant และ capture ตรวจซ้ำหลังแก้ ไม่อ้างว่าทุกรอบแรกผ่าน การล้มเหลวและข้อจำกัดหลักฐานอธิบายไว้ในประวัติด้านบน ไม่มีการเพิ่ม timeout/ลด assertion เพื่อกลบผล การแจ้ง initial-login timeout ยังเป็นงานวิเคราะห์หากเกิดซ้ำ ไม่ถือ successful rerun เป็น root-cause proof

หลักฐาน browser: `task-10-https-4001-*.log` สำหรับ regression เดิม; Dev ใช้ `task-10-https-4000-identity.log`, `*-scopes-diagnostic-rerun.log`, `*-attendance-{directory,storage}-diagnostic-rerun.log`, `*-attendance-{history,corrections}-remaining.log`; final policy ใช้ `task-10-final-policy-{dev-rerun,prod}.log`, capture Dev `task-10-final-capture-dev-rerun.log`, grant และ capture Production ใช้ `task-10-controller-*.log` ตัวเลขใน TRX/ชุด focused ที่ซ้อนกันไม่บวกเป็นเทสต์ใหม่

ตรวจ keyboard และภาพจอกว้าง 390px จาก fixture แล้ว ไม่พบ overflow ที่กีดขวางการอ่านในภาพที่ตรวจ ไม่ใช้ภาพจำลองแทนมือถือจริง

## ขอบเขตที่ยังไม่ได้ตรวจจริง

มือถือจริงและ NAS จริงพักตามคำสั่งผู้ใช้ ผลเบราว์เซอร์จำลองไม่แทนการตรวจอุปกรณ์จริง ชุด HTTPS ใช้ฐานทิ้งได้และ CA เฉพาะ profile ไม่เปลี่ยน trust store เครื่อง Next Production หมายถึง build โหมด Production ส่วน API fixture เป็น Testing ไม่ใช่การรับรอง deployment จริง

ยังไม่ตั้งต้นสิทธิ์หรือกติกาบนฐานจริง บริษัทต้องรับรองผู้บริหารสิทธิ์และกำหนดค่าธุรกิจจริงก่อนเปิดใช้ การรับรอง 7A ไม่ใช่การรับรองการจ่ายเงินครบวงจร
