# Module 6B — ผลการตรวจ Task 2: ตรวจภาพและประทับเวลาไทย

วันที่ 25 กันยายน 2026

สถานะ: Task 2 ผ่าน TDD, Code Review และ Test/Build/Lint แล้ว ตรวจผลรอบส่งมอบวันที่ 25 กันยายน 2026 เวลา 08:29 UTC

## ขอบเขตที่ทำ

ทำเฉพาะ Task 2 บนสาขา `codex/module-6b-evidence-storage` ต่อจาก `3246d75` ไม่แก้ checkout หลัก ไม่หยุด Preview และไม่ push/merge

- รับ JPEG/PNG จากเนื้อหาไฟล์จริง ไม่ใช้ชื่อไฟล์หรือ MIME เป็นหลักฐาน รองรับ stream ที่ไม่มี `Length`
- ตรวจซองไฟล์และมิติก่อน native decode จำกัด 10 MiB และ 20 ล้านพิกเซล ปฏิเสธภาพหลายเฟรม ไฟล์ขาดท้าย และข้อมูลต่อท้าย
- ใช้ orientation ทั้ง 8 แบบจาก EXIF แล้ววาดลง canvas ใหม่ ไม่ส่งต่อ EXIF, GPS, comment หรือข้อความใน PNG
- ย่อภาพด้านยาวไม่เกิน 2,048 พิกเซล ไม่ขยายภาพเล็ก เติมพื้นขาวและแถบดำสูง 112 พิกเซลโดยไม่ทับเนื้อภาพ
- ประทับวันที่/เวลา `Asia/Bangkok` ปี ค.ศ. พร้อม `(UTC+7)` และ `เข้า`/`ออก` จาก instant ที่ผู้เรียกฝั่งเซิร์ฟเวอร์ส่งมา ไม่อ่านนาฬิกาใหม่
- ใช้ Noto Sans Thai ที่ฝังใน assembly และ HarfBuzz จัดรูปอักษร ขนาด 32 พิกเซล ตรวจว่าฟอนต์มี glyph ครบ
- เข้ารหัส JPEG คุณภาพ 90 ตรวจ decode ผลลัพธ์อีกครั้ง และคำนวณ SHA-256 แยกภาพเต็ม/ภาพย่อ
- จำกัดงานทั้ง process: ทำพร้อมกัน 2 งาน รอได้ 8 งาน deadline 10 วินาที ผู้เรียกที่ timeout/cancel ไม่คืนโควตาของงานที่ยังทำไม่จบ
- ส่งข้อผิดพลาดแบบมีรหัส: คิวเต็มใช้ 429/Retry-After 5 วินาที และ timeout ใช้ 503 การเชื่อม HTTP จริงอยู่ Task ถัดไป

## TDD และข้อบกพร่องที่ตรวจพบ

1. เริ่ม formatter ด้วย RED 3 กรณีเพราะยังไม่มี type แล้ว GREEN 3 กรณี เพิ่มการทดสอบ culture ภาษาไทยโดยต้องคงปี ค.ศ.
2. เริ่ม image pipeline ด้วย RED 28 กรณีและ formatter ผ่าน 4 กรณี หลังเพิ่ม implementation ผ่าน 32/32
3. Build พบ `NETSDK1022` เพราะ SDK รวม JSON เป็น Content อยู่แล้ว แก้ manifest เป็น `Content Update` ไม่ปิด default items ทั้งโครงการ
4. เพิ่มการตรวจขอบเขตพิกเซล/overflow, deadline/cancel/คิว และภาพสังเคราะห์ รวมผ่าน 45/45 กรณี
5. ตรวจภาพจริงพบว่าการย่อภาพเต็มทั้งรูปทำให้ข้อความบนภาพย่อขนาดใหญ่เหลือหมึกสูงเพียง 6–8 พิกเซล ไม่ผ่านเกณฑ์อ่านได้
6. เพิ่ม regression ที่นับเฉพาะ pixels สีขาว ไม่รวมพื้นที่สีเหลืองของ fixture: รูปใหญ่ RED 2 กรณี ส่วนรูปเล็กผ่าน 2 กรณี แล้วแก้การสร้างภาพย่อจนผ่าน 45/45
7. Code Review ไม่พบ Critical/Important แต่เสนอ Minor เรื่อง coverage 2 จุด จึงเพิ่มการตรวจเวลาเปลี่ยนแล้ว pixels เปลี่ยนเฉพาะแถบข้อความ ตรวจ footer ภาพย่อเทียบการย่อ pixels เดิม และ queued cancel/timeout คืน admission โดยไม่อ่าน source รวมถึงตรวจ PNG text metadata
8. พิสูจน์ regression ใหม่ด้วย mutation ชั่วคราว: ตรึงข้อความเวลาผิดทำให้ test pixels ล้ม 1 กรณี; ไม่คืน admission ของงานที่ยังรอคิวทำให้ test queued cancellation ล้ม ส่วน timeout กรณีถัดไปล้มต่อเนื่องเพราะ slot รั่วจากกรณีแรก จึงไม่นับเป็นหลักฐาน mutation แยกต่างหาก ถอน mutation ทั้งหมดก่อนตรวจรอบส่งมอบ

## การตัดสินใจเรื่องภาพย่อ

ภาพย่อยังสร้างจาก bytes ของภาพที่ประทับแล้วเท่านั้น และมีด้านยาวไม่เกิน 640 พิกเซล แต่แบ่งการย่อเป็นสองส่วน:

- ส่วนภาพรักษาสัดส่วนและเติมขอบขาวตามจำเป็น ให้เหลือพื้นที่สำหรับแถบเวลา
- ตัดเฉพาะแถบเวลาด้านซ้ายกว้าง 800 พิกเซลจากภาพเต็ม ซึ่งมีข้อความครบ แล้วย่อเป็น 640 × 90 พิกเซล ส่วนที่ทิ้งเป็นพื้นที่ดำว่างด้านขวา
- ไม่เขียนข้อความซ้ำ ไม่เปลี่ยน instant ไม่ย้อนกลับไปอ่าน upload ต้นทาง ตรวจให้ข้อความเต็มอยู่ภายใน 768 พิกเซลตั้งแต่ขั้นประทับ

เป็นรายละเอียด implementation เพื่อให้ผ่านเกณฑ์อ่านวันเวลาได้ตามแผน ไม่เปลี่ยน pixels ของภาพเต็ม รูปสังเคราะห์แนวตั้ง/แนวนอน เข้า/ออก และเวลาเที่ยงคืนได้รับการเปิดดูจริงแล้ว

ไฟล์ภาพตรวจด้วยตาอยู่ใน `.superpowers/sdd/2026-09-25-module-6b-evidence-storage/visual/` ไม่มีภาพบุคคลหรือพิกัดจริง ใช้เฉพาะแถบสีสังเคราะห์ ไม่ commit ไฟล์ log/ภาพทดสอบทั้งหมด

## Dependency และฟอนต์

รุ่นแพ็กเกจตรงกับแผน: SkiaSharp/SkiaSharp.HarfBuzz/Linux native 3.119.4 และ HarfBuzzSharp/Linux native 8.3.1.5 ล็อก transitive dependencies ใน API, IntegrationTests และ E2E fixture

ฟอนต์จาก [release NotoSansThai-v2.002](https://github.com/notofonts/thai/releases/tag/NotoSansThai-v2.002) ใช้ `full/ttf/NotoSansThai-Regular.ttf` เพื่อครอบคลุมทั้งไทยและตัวเลข/ข้อความละติน เก็บ SHA-256 ของ archive, font และ OFL ใน `Assets/manifest.json` พร้อม license และ third-party notices จากแพ็กเกจ native โดยไม่แก้ข้อความใบอนุญาต

`git diff --cached --check` แบบรวม vendor assets แจ้ง whitespace จาก CRLF/ช่องว่างท้ายบรรทัดในใบอนุญาตต้นฉบับ คง bytes เดิมไว้เพื่อรักษาหลักฐาน SHA-256 และตรวจ `cmp` ของ native notices กับแพ็กเกจแล้วตรงทั้งหมด การตรวจ diff ที่ยกเว้นเฉพาะ OFL และโฟลเดอร์ native licenses ผ่าน ไม่มี whitespace error ในโค้ด/เอกสารที่เขียนใหม่

## ผลตรวจ

| รายการ | ผล |
| --- | --- |
| Focused image/budget tests บน macOS หลัง review | ผ่าน 49/49 |
| Linux SDK 10.0.401 container, timezone America/Los_Angeles หลัง review | ผ่าน 49/49 ใช้ native decode/shape จริง |
| ภาพเต็ม/ภาพย่อ: เข้า ออก แนวตั้ง แนวนอน เที่ยงคืน | ตรวจด้วยตาแล้วหลังแก้ภาพย่อ |
| NuGet audit รวม transitive ของ API/IntegrationTests/E2E fixture | ไม่พบรายการช่องโหว่ที่แหล่งข้อมูลปัจจุบันรายงาน |
| Locked restore ทั้ง 3 projects | ผ่าน ใช้ artifacts แยก ไม่เปลี่ยน lock |
| Backend Build / Format | ผ่าน ไม่มี warning/error |
| Backend Publish แบบแยก artifacts | ผ่าน มี Linux native libraries, OFL, manifest และ native notices ในผล publish |
| Backend regression ทั้งชุด | ผ่าน 1,095/1,095 ไม่มีล้มหรือข้าม ใช้เวลา 22.9826 นาที |
| Frontend Test / Build / Lint | Test ผ่าน 40/40, Build และ Lint exit 0 |
| Code Review อิสระเฉพาะ Task 2 | ไม่มี Critical/Important; Minor 2 ข้อเพิ่ม regression แล้ว |

Linux image digest: `sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29` ใช้ source/NuGet cache แบบ read-only และ artifacts ในพื้นที่ชั่วคราว ไม่แตะฐานข้อมูลหรือ Preview

หลัง backend ทั้งชุดจบ exit 0 ตรวจ SHA-256 ของ source/test/project/font ทั้ง 13 รายการเทียบชุดที่ตรึงก่อนรันแล้วตรงทั้งหมด ไม่มีการแก้ production code หรือ tests ระหว่างรอบส่งมอบ

คำสั่งตรวจที่ใช้:

```sh
dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~EvidenceStampTests|FullyQualifiedName~EvidenceImageBudgetTests'
dotnet build backend/TPR10.sln --no-restore
dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'
dotnet format backend/TPR10.sln --no-restore --verify-no-changes
dotnet restore backend/TPR10.sln --locked-mode --artifacts-path /private/tmp/tpr10-task2-locked
dotnet restore backend/tests/TPR10.E2E.Fixture/TPR10.E2E.Fixture.csproj --locked-mode --artifacts-path /private/tmp/tpr10-task2-locked
dotnet list backend/TPR10.sln package --vulnerable --include-transitive --no-restore
dotnet list backend/tests/TPR10.E2E.Fixture/TPR10.E2E.Fixture.csproj package --vulnerable --include-transitive --no-restore
dotnet publish backend/src/TPR10.Api/TPR10.Api.csproj --artifacts-path /private/tmp/tpr10-task2-publish -p:RestoreLockedMode=true
npm test
npm run lint
npm run build
```

Linux ใช้ image digest ข้างต้น รัน focused command เดียวกันโดยมี `--artifacts-path /work/artifacts`, ตั้ง `TZ=America/Los_Angeles`, mount source เป็น `/src:ro`, NuGet cache แบบ read-only และ `/work` เป็น tmpfs ที่รัน native code ได้ ไม่ใช้ Docker socket หรือฐานข้อมูลจริงใน container นี้

Log สำคัญอยู่ใต้ `.superpowers/sdd/2026-09-25-module-6b-evidence-storage/`: `task2-focused-final.log`, `task2-linux-final.log`, `task2-backend-delivery.log`, `task2-backend-build-final.log`, `task2-format-final.log`, `task2-locked-restore.log`, `task2-audit-final.log`, `task2-publish-final.log`, `task2-node.log`, `task2-lint.log`, `task2-frontend-build.log` และ checksum ตรึงโค้ด `task2-delivery-source.sha256` เป็นหลักฐานในเครื่อง ไม่ commit log ทั้งหมด

## ขอบเขตที่ผู้ตรวจเว้นและข้อสรุปของผู้ดำเนินงาน

- Storage/NAS/durability/switch/migration: เป็น Tasks 3–6 ไม่ขยายขอบเขต Task 2 การผ่าน image tests ไม่พิสูจน์ที่เก็บจริง
- Publication, authorization/revocation, HTTP cache/headers/status mapping และ DI: เป็นงานเชื่อม Tasks 4–5/7 รอบนี้ยืนยันเพียงสัญญา error ของ service ไม่อ้าง HTTP ใช้งานแล้ว
- Camera/GPS/challenge และการผูก attendance event: เป็น 6C ต้องตรวจต่อเมื่อมี workflow จริง
- Linux, audit, notices, verification และเอกสาร: ผู้ดำเนินงานตรวจเอง ไม่ใช้ความเห็นจาก review แทนผลคำสั่ง
- Visual gate: ผู้ตรวจอ่าน logic แต่ไม่ได้เปิดภาพ ผู้ดำเนินงานเปิดไฟล์ full/thumbnail จริงหลังแก้และบันทึกผลแยก
- Task 8/NAS drill/ความพร้อมทั้งโมดูล: อยู่นอก Task 2 ไม่ถือว่าผ่านหรือพร้อม production

Minor ทั้งสองข้อได้รับการเพิ่ม coverage ไม่เหลือข้อเสนอที่เลื่อนโดยไม่ระบุ

## ข้อจำกัดและงานถัดไป

- นี่คือ image service ภายใน ยังไม่มีเส้นทางลงเวลาหรือหน้าเว็บกล้อง/GPS พร้อมใช้
- เวลาเป็น instant ที่เซิร์ฟเวอร์กำหนดให้ ไม่ใช่หลักฐานยืนยันเวลาที่กล้องกดชัตเตอร์ และไม่มีการตรวจใบหน้าหรือ liveness
- การหยุดผู้เรียกเมื่อ deadline ถึง ไม่สามารถบังคับหยุด native codec ที่กำลังรันได้ โควตาถูกถือจนงานจบเพื่อไม่ให้เกิดงานเกินเพดาน
- Task 3 คือ storage adapter สำหรับ local-folder/NAS ตามแผน ยังไม่เริ่ม
- การทำกล้องจริง การลงเวลา และการผูกเหตุการณ์อยู่ 6C ไม่อยู่ Task 2
