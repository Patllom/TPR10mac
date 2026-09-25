# Module 6B — ผลการตรวจ Task 3: ที่เก็บรูปแบบโฟลเดอร์และ NAS

วันที่ 25 กันยายน 2026

สถานะ: Task 3 ผ่าน TDD, Code Review และ Test/Build/Lint แล้ว ตรวจผลรอบส่งมอบวันที่ 25 กันยายน 2026 เวลา 09:35 UTC

## ขอบเขต

ทำเฉพาะ Task 3 บนสาขา `codex/module-6b-evidence-storage` ต่อจาก `0d15ace` ใช้ worktree เดิม ไม่แก้ checkout หลัก ไม่หยุด Preview ไม่ push/merge และไม่เชื่อมต่อหรือ mount NAS จริง

- `IStorageAdapter` รองรับเขียนแบบไม่ทับ อ่านพร้อมตรวจขนาด/SHA-256 และตรวจสุขภาพด้วยการเขียน–อ่านจริง
- `StorageRootResolver` รับเฉพาะ alias ที่ตั้งค่าฝั่งเซิร์ฟเวอร์ ผูก storage ID กับ alias เดิม ไม่รับพาธจากผู้ใช้ และเก็บ adapter เดิมเพื่อตรวจการเปลี่ยน inode ของ root
- ใช้ key รูปแบบ `objects/{เลขฐานสิบหกสองตัวแรก}/{guid:N}/{full|thumbnail}.jpg` เท่านั้น ปฏิเสธ traversal, absolute path, UNC, encoded separator และรูปแบบที่ไม่ canonical
- เปิด root และทุกส่วนของพาธด้วย directory handle + `openat` แบบไม่ตาม symlink ตรวจเจ้าของ สิทธิ์ ชนิดไฟล์ inode และ volume/mount จาก handle
- เขียนไฟล์ `.partial-{operationGuid}` ด้วย exclusive create จากนั้น flush, rename แบบ no-replace, sync directory และเปิดอ่านกลับตรวจ checksum ก่อนคืนผลสำเร็จ หาก key เดิมมีอยู่ ต้องขนาดและ checksum ตรงกันเท่านั้น ไม่เขียนทับ
- ตรวจ mount identity จากระบบปฏิบัติการร่วมกับ marker ที่เตรียมไว้ใน protected configuration ไม่สร้างหรือซ่อม marker ใน request และไม่ยอมใช้ local directory ที่เหลือหลัง NAS หลุดแทน NAS
- จำกัดงาน I/O ทั้ง process ที่ 2 งานทำพร้อมกัน และคิวอีก 8 งาน กำหนด deadline 10 วินาที การ timeout ผู้เรียกไม่คืนโควตาก่อน native worker จบจริง
- Probe ใช้ไฟล์สังเคราะห์ชื่อสุ่มเฉพาะงาน ตรวจ durable roundtrip และพื้นที่ว่าง ส่ง storage ID จาก binding คืน ไม่สร้าง evidence และลบเฉพาะไฟล์ probe ของงานนั้น
- พื้นที่ว่างต่ำกว่า 10 GiB **หรือ** 10% เป็น `warning`; ข้อมูลพื้นที่ไม่ครบ/ผิดรูปแบบเป็น `unknown` ไม่แสดงพร้อมใช้งาน

ยังไม่มี API/DI wiring, หน้าจัดการที่เก็บ, การย้ายรูป หรือหน้าลงเวลา งานเหล่านี้อยู่ Tasks 4–8 และ Module 6C

## เงื่อนไขการติดตั้ง

- root ต้องเป็น absolute canonical path ไม่มี symlink ในทุก component บน macOS จึงใช้ `/private/tmp/...` ใน fixture ไม่ใช้ `/tmp` ที่เป็น symlink
- root และ object directories เป็นของ service account และไม่มีสิทธิ์ group/other; งานเขียนต้องมีสิทธิ์เจ้าของครบ `0700` ไฟล์ใหม่สร้าง `0600` การอ่านยอมรับ directory แบบ `0500`
- macOS ตรวจ extended ACL ผ่าน file descriptor และปฏิเสธทุก entry รวม inherited/deny โดยไม่แก้ ACL ให้เอง ทั้ง root, marker, directories และ object files ต้องผ่าน หาก filesystem ตรวจ ACL ไม่ได้จะปฏิเสธ Linux ใช้ owner/mode ตาม POSIX ส่วนสิทธิ์ที่ NAS server บังคับต้องตรวจจริงใน deployment gate เพิ่มเติม
- marker `.tpr10-storage-id` ต้องเป็น regular file ของ service account มี hard link เดียว เนื้อหา GUID รูปแบบ D ยาว 36 bytes ไม่มี newline ทดสอบด้วยสิทธิ์ `0400` ต้อง provision แยกก่อนเริ่มใช้งาน
- `ExpectedVolumeId` เป็น fingerprint จาก device/filesystem/source ที่อ่านผ่าน handle ไม่ใช่ข้อความจาก client ใช้ `MountIdentity.GetVolumeId` เป็น diagnostic ฝั่ง deployment แล้วอนุมัติค่าใน protected configuration ไม่เปิด diagnostic นี้ทาง HTTP
- fingerprint และ inode อาจเปลี่ยนหลัง remount/reboot การเปลี่ยนดังกล่าวต้องตรวจและรับรอง configuration ใหม่ ไม่ fallback อัตโนมัติ
- NAS อนุญาตชนิด NFS/SMB ที่ระบบรายงานจริงเท่านั้น หาก primitive, mount information หรือ permission model ไม่รองรับ ให้ปฏิเสธ ไม่เปลี่ยนไปใช้วิธีเปิดไฟล์ที่อ่อนกว่า
- implementation ระบุ ABI สำหรับ Linux/macOS แบบ ARM64/x64; รอบนี้มีหลักฐานรันจริงบน ARM64 ทั้งสองระบบ ไม่อ้างว่า x64 หรือ NAS รุ่นใดผ่าน deployment validation แล้ว Windows ไม่รองรับ
- `fsync` และ exclusive rename เป็นเงื่อนไขขั้นต่ำ ไม่พิสูจน์ว่าอุปกรณ์ NAS เขียนลงสื่อถาวรเมื่อไฟดับ ต้องตรวจกับ test share และผู้ดูแลอุปกรณ์ใน Task 8

## หลักฐาน TDD และข้อผิดพลาดที่พบ

1. ชุดแรก RED 18 กรณีเพราะยังไม่มี adapter/identity แล้วผ่าน 18/18 หลัง implementation
2. เพิ่ม probe และ failure injection: RED 7 กรณี กับผ่านเดิม 20 กรณี แล้วผ่าน 27/27
3. เพิ่ม deadline/worker ownership และ resolver: RED 2 กรณี แล้วผ่าน 29/29
4. เพิ่ม race tests: RED 3 กรณี พบ next-directory handle รั่วเมื่อ sync ของ parent ล้ม, parent ถูกย้ายหลังเขียนแล้วยังรายงานสำเร็จ และ final inode เปลี่ยนระหว่างอ่านแต่ checksum เท่าเดิมแล้วยังคืน bytes แก้ disposal ownership และเปิดตรวจ object path/identity ซ้ำก่อนเผยแพร่และหลังอ่าน จนผ่าน 32/32
5. Linux ครั้งแรกผ่าน 3/29 แต่ล้ม 26 กรณีด้วย `EINVAL` ตอนเปิด root สาเหตุคือ ARM64 ใช้ค่า `O_DIRECTORY` และ `O_NOFOLLOW` ต่างจาก x64 แก้เลือกค่าตามสถาปัตยกรรม แล้วผ่าน 32/32 บน Linux ARM64
6. ปรับ test seam จาก reflection ที่ใช้ช่วง RED เป็น typed internal constructor และใช้ thread-safe collection เก็บ handle ของงานทดสอบพร้อมกัน ไม่เปลี่ยน public API
7. Review พบ retry หลัง rename สำเร็จแต่ directory sync ล้ม อาจคืน success ทั้งที่ยังไม่ผ่าน durability barrier เพิ่ม regression RED 1 กรณี แล้วแก้ให้ retry sync ทั้งไฟล์เดิมและ directory ก่อนคืนผล จนผ่าน
8. Review พบ BSD mode bits ไม่ครอบคลุม ACL บน macOS เพิ่ม ACL จริงบน fixture ด้วย `/bin/chmod` ทั้ง root แบบ explicit, child แบบ inherited และ final file ทั้ง 3 กรณี RED เพราะไม่ปฏิเสธ จากนั้นเพิ่มการตรวจ ACL ผ่าน handle
9. ACL implementation แรกปฏิเสธกรณีไม่มี ACL ด้วย ทำให้ชุดเดิมล้ม 10 กรณี ตรวจ implementation ของ Apple พบ `acl_get_fd_np` ใช้ `filesec_get_property` ซึ่งคืน null/ENOENT เมื่อไม่มี ACL แก้รับเฉพาะ absence นี้ ไม่รับ error อื่นเป็นไม่มี ACL แล้วชุดเดิมและ ACL tests ผ่าน
10. Minor เรื่อง low-capacity แก้ใน Task 3 เพื่อไม่ส่ง `healthy` ผิดให้ Task 4: หลังแก้ ACL แล้ว boundary tests RED 3 กรณี/ผ่าน 38 กรณี ก่อนแก้ classification จนผ่าน 41/41 บน macOS

อ้างอิงค่าธง ARM64 จาก [Linux kernel UAPI](https://github.com/torvalds/linux/blob/master/arch/arm64/include/uapi/asm/fcntl.h) และ semantics ของ [open](https://man7.org/linux/man-pages/man2/open.2.html), [rename](https://man7.org/linux/man-pages/man2/rename.2.html), [statx](https://man7.org/linux/man-pages/man2/statx.2.html) และ [mountinfo](https://man7.org/linux/man-pages/man5/proc_pid_mountinfo.5.html) ส่วน Darwin ตรวจ struct/flags จาก Command Line Tools SDK headers ในเครื่อง

การอ่าน ACL อ้างอิง [Apple ACL API](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man3/acl_get_fd_np.3.html), [Apple Libc ACL retrieval](https://github.com/apple-oss-distributions/Libc/blob/main/posix1e/acl_file.c) และ [การอ่าน ACL entry](https://github.com/apple-oss-distributions/Libc/blob/main/posix1e/acl_entry.c) พร้อมยืนยันกรณีมี/ไม่มี ACL จากการรันบนเครื่องจริง ไม่คัดลอก source upstream มาไว้ในโครงการ

การจำลอง ENOSPC, flush/rename failure, short read และ native I/O ค้าง ใช้ test seam ที่ boundary ของ native operation และไฟล์สังเคราะห์จริง ไม่ได้ทำดิสก์เครื่องผู้ใช้เต็ม ไม่ได้ถอด NAS จริง และไม่ถือแทน NAS drill

## ผลตรวจรอบส่งมอบ

| รายการ | ผล |
| --- | --- |
| macOS ARM64 focused tests หลังแก้ review | ผ่าน 41/41 ไม่มีข้าม |
| Linux ARM64 focused tests หลังแก้ review | ผ่าน 38 กรณี ข้าม 1 theory สำหรับ Darwin ACL (3 inputs บน macOS) ไม่มีล้ม |
| Backend Build / Format | ผ่าน ไม่มี warning/error |
| Frontend Test / Build / Lint | ผ่าน 40/40, Build และ Lint exit 0 |
| Backend regression ทั้งชุด | ผ่าน 1,136/1,136 ไม่มีล้มหรือข้าม ใช้เวลา 23.1229 นาที exit 0 |
| Code Review อิสระ | ไม่พบ Critical; Important 2 ข้อและ Minor 1 ข้อแก้พร้อม regression แล้ว |

Linux ใช้ SDK image `mcr.microsoft.com/dotnet/sdk:10.0.401`, digest `sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29` ผูก source และ NuGet cache แบบ read-only ใช้ artifacts บน `/work` tmpfs ไม่ผูก Docker socket และไม่ต่อฐานข้อมูลจริง

หลัง backend ทั้งชุดจบ ตรวจ SHA-256 ของ source/test/project ทั้ง 12 ไฟล์ตรงกับชุดที่ตรึงไว้ก่อนรันทั้งหมด ไม่มีการแก้ production code หรือ tests ระหว่างรอบส่งมอบ ฝั่งเว็บรัน Test/Build/Lint ซ้ำหลังแก้ Review แล้วผ่านครบ

คำสั่งหลัก:

```sh
dotnet test backend/TPR10.sln --filter FullyQualifiedName~StorageAdapterTests
dotnet format backend/TPR10.sln --no-restore --verify-no-changes
dotnet build backend/TPR10.sln --no-restore
dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'
npm test
npm run lint
npm run build
```

Linux ใช้ focused command เดียวกัน เพิ่ม `--artifacts-path /work/artifacts` ภายใน container ส่วน backend ทั้งชุดรันกับฐานข้อมูลทดสอบแยก ไม่ใช้ Preview

Logs อยู่ใน `.superpowers/sdd/2026-09-25-module-6b-evidence-storage/` โดยไม่ commit log ทั้งหมด: `task3-focused-final.log`, `task3-linux-final.log`, `task3-backend-build-final.log`, `task3-format-final.log`, `task3-frontend-test-final.log`, `task3-lint-final.log`, `task3-frontend-build-final.log`, `task3-backend-delivery.log` และ checksum ตรึง source/test/project `task3-delivery-source.sha256`

## ข้อสรุปต่อขอบเขตที่ผู้ตรวจเว้น

- NAS disconnect/remount/reconnect, server ACL และ server persistence: ยังคงเป็น Task 8 หลังได้รับ test share และอนุญาต disruption ไม่ใช้ test seam อ้างแทน
- Power loss และ storage controller: ต้องมีหลักฐานจาก Operations/อุปกรณ์จริง `fsync` สำเร็จไม่พิสูจน์ฮาร์ดแวร์
- x64 execution และผล ARM64 ที่ผู้ตรวจไม่ได้รันเอง: ผู้ดำเนินงานยืนยัน ARM64 จากคำสั่งข้างต้น ส่วน x64 ยังไม่มีผลรันจริง
- Persistent registry binding, configuration protection, API authorization/error mapping, readiness expiry และ scheduled scans: Tasks 4/7/8; Task 3 มี resolver ใน memory ไม่ใช่ registry พร้อมใช้
- Migration, DB/audit publication atomicity, orphan reconciliation และ cleanup: Tasks 5/6/8 ไม่เพิ่มระบบลบหลักฐานใน adapter
- ผู้ควบคุม root/service identity ที่มุ่งร้าย: ไม่รับรองว่าป้องกันได้ ต้องคุมสิทธิ์ระบบและ NAS ด้วย
- Frontend, full regression และเอกสาร: ผู้ดำเนินงานตรวจเอง ไม่ถือคำรับรองของ reviewer แทนผลคำสั่ง

ข้อเสนอทั้งสามรายการได้รับการแก้ ไม่มี Minor ที่เลื่อนโดยไม่ระบุ การรีวิวครั้งนี้เป็นเฉพาะ Task 3 ไม่ใช่ whole-module review ของ Task 8

## ข้อจำกัดที่ต้องส่งต่อ

- ผู้มีสิทธิ์ service account/root ยังแก้ filesystem ได้ การตรวจไม่ใช่ sandbox ป้องกันผู้ดูแลระบบปฏิบัติการที่มุ่งร้าย และไม่อาจรับประกันว่าพาธจะไม่เปลี่ยนหลังจุดตรวจสุดท้าย
- native I/O ที่ค้างหยุดบังคับไม่ได้ timeout จำกัดการรอของผู้เรียกและปริมาณ worker ไม่ได้ยกเลิก syscall ที่ค้าง หากครบสอง worker ระบบจะปฏิเสธงานเพิ่มเมื่อคิวเต็ม ต้องมีการฟื้นระบบโดย Operations
- เมื่อเกิด error หรือการแข่งขัน ไฟล์ partial/final ที่ไม่ได้เผยแพร่ใน metadata อาจค้างอยู่ ตั้งใจไม่ลบ evidence อัตโนมัติ การรายงาน orphan/reconciliation เป็นงาน Tasks 4–6/8
- การเขียน immutable ในที่นี้หมายถึง adapter ไม่ overwrite key ที่มีแล้ว ไม่ใช่ WORM hardware หรือ filesystem immutable flag
- `ProbeAsync` ที่ I/O ล้มส่ง exception ให้ registry แปลงเป็นสถานะไม่พร้อมใน Task 4; พื้นที่ว่างที่อ่านไม่ได้ให้ `unknown` ไม่ใช่พร้อมใช้งาน
- Test gate ของ Task 3 ไม่เท่ากับ Module 6B เสร็จหรือ production/NAS gate ผ่าน
- ขั้นต่อไปคือ Task 4: ลงทะเบียนที่เก็บ เปลี่ยนปลายทาง และ health ตามแผน ยังไม่เริ่มในคำสั่งนี้
