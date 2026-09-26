# คู่มือดูแลและกู้คืนที่เก็บหลักฐานภาพ Module 6B

สถานะ: คู่มือสำหรับตรวจรับทางเทคนิค ยังไม่อนุมัติ Production และยังไม่มีผลทดสอบ NAS จริง

## ขอบเขตและผู้รับผิดชอบ

- Operations จัดเตรียม filesystem, service identity, mount, protected configuration และ backup นอก Git
- ผู้ได้รับ `attendance:storage-manage` พร้อม MFA ล่าสุดจัดการ alias และงานย้ายได้ แต่ไม่ได้สิทธิ์อ่านภาพ
- พนักงานอ่านภาพของตนเอง; HR ต้องมีสิทธิ์และ assignment ปัจจุบันตรงหน่วยงานใน snapshot; หัวหน้าและ Admin ไม่มีสิทธิ์ภาพลูกทีมโดยอัตโนมัติ
- ก่อน Production ต้องระบุผู้รับผิดชอบภาพ/พิกัด การแจ้งพนักงาน การรักษาข้อมูล การรับ incident และผู้อนุมัติกู้คืน ห้ามถือการอนุมัติแผนพัฒนาเป็น sign-off ใช้งานจริง

## เตรียม alias

1. ใช้ filesystem บน Linux หรือ macOS ที่รองรับ native adapter; Windows ยังไม่รับรอง
2. Operations เตรียม root ส่วนตัวนอก webroot และ mount NAS ล่วงหน้าด้วย service identity สิทธิ์ขั้นต่ำ ไม่ใช้สิทธิ์เปิดทั้งเครือข่าย
3. ตั้ง `AttendanceStorage:Locations` ใน protected configuration: Alias, Kind (`local-folder`/`nas-mounted-folder`), RootPath, ExpectedVolumeId และ MarkerId ตาม deployment จริง รวม marker `.tpr10-storage-id` ที่เตรียมด้วยสิทธิ์จำกัด
4. ห้ามส่ง root, UNC path, password หรือ SMB credential ผ่าน Portal/เอกสาร/Git ไม่สร้าง marker ใหม่เพื่อกลบ mount ที่หาย
5. ห้ามเปลี่ยน root หรือ fingerprint ของ alias ที่ลงทะเบียนและมีข้อมูลแล้วให้ชี้สถานที่อื่น ให้ลงทะเบียน alias ใหม่และย้ายผ่าน protocol เท่านั้น การกู้คืน disaster recovery ต้องคืน protected configuration และ mount identity ที่ผ่านการตรวจ ไม่แก้ metadata เพื่อข้าม guard

## เพิ่มที่เก็บและเปลี่ยนปลายทาง

เปิด `/portal/admin/attendance-storage` ด้วยสิทธิ์เฉพาะและ MFA ล่าสุด:

1. ลงทะเบียน alias ที่ Operations เตรียม พร้อมเหตุผล
2. กดตรวจที่เก็บ (probe) ตรวจอ่าน/เขียนและ readiness; หลัง restart ต้องตรวจใหม่ ไม่เชื่อสถานะเดิมในฐานข้อมูล
3. เปลี่ยนปลายทางรูปใหม่ด้วย version ล่าสุดของ write-target และเหตุผล ไม่ใช่ version ของ location
4. รูปเดิมยังอ้าง storage เดิม; reservation ที่เริ่มไปแล้วตรึงปลายทางเดิม ห้ามถอดต้นทาง

Probe มีอายุ 60 วินาทีและผูก version; native I/O ตรวจ filesystem identity ทุกครั้ง ป้ายในหน้าจออาจเป็นค่าจากครั้งที่โหลด ต้องดูเวลาและกดตรวจใหม่เมื่อจำเป็น

## ย้ายหลักฐานเก่า

1. เลือกต้นทางที่ไม่ใช่ write-target ปัจจุบันและปลายทางที่ผ่าน probe
2. ส่ง start ด้วย request ID เดียว รุ่นต้นทาง/ปลายทางและเหตุผล ระบบปิดรับ reservation ใหม่ที่ต้นทางอย่าง atomic พร้อม job/manifest/audit
3. ติดตาม job และจำนวน verified/blocked; คัดลอก full และ thumbnail แล้วอ่านกลับตรวจ SHA-256/จำนวน bytes ก่อนสลับสำเนา Active
4. ไฟล์ต้นทางคงอยู่เป็น Fallback ที่ตรวจแล้ว ไม่มีการประทับเวลาใหม่หรือเปลี่ยน evidence ID
5. Completed หมายถึง manifest ผ่าน ไม่ใช่อนุญาตถอด NAS หรือลบต้นทาง ต้องตรวจการอ่านตามสิทธิ์และ backup/restore พร้อมอนุมัติแยกก่อนเสมอ รุ่นนี้ไม่มี API ลบหรือ retire

กรณี network timeout: ผล mutation อาจสำเร็จแล้ว ให้ตรวจ job เดิมก่อน ไม่ retry ด้วย request ID ใหม่อัตโนมัติ; 409 ให้โหลด version ใหม่; 503 ให้ตรวจ dependency ไม่ตีความเป็น logout

## งานค้างและการแจ้งเตือน

| สถานะ/เหตุการณ์ | การดำเนินการ |
| --- | --- |
| unknown | ตรวจ config/mount และ probe ใหม่ ไม่อ้างว่าพร้อมเขียน |
| unavailable / mount หาย | ระงับการใช้งานที่เก็บนั้น ตรวจ mount identity/marker ห้ามเขียนลง local mount point แทน |
| warning พื้นที่ต่ำ | เกณฑ์ต่ำกว่า 10 GiB หรือ 10%; วางแผนเพิ่มพื้นที่/เปลี่ยน target ไม่มี silent fallback |
| missing / checksum mismatch | เก็บหลักฐาน incident ตรวจ backup และสำเนาที่ตรวจแล้ว ห้ามสร้างภาพใหม่ทดแทนเงียบ ๆ |
| orphan | รายงานและตรวจ business reference ไม่มี cleanup อัตโนมัติ |
| Blocked | ตรวจสาเหตุ อำนาจผู้สร้างงานและ dependencies; probe ใหม่ แล้ว resume ด้วย version/เหตุผลและ MFA ล่าสุด |

Worker ใช้ lease 60 วินาทีและ fencing ป้องกัน worker เก่าตัดสลับสำเนา งาน transient ลองครั้งแรกแล้ว retry อีกสูงสุด 5 ครั้ง หน่วง 1/5/30/120/300 วินาที จากนั้น Blocked; checksum mismatch หยุดทันที ไม่ retry อัตโนมัติ การ resume ต้องเป็นการตัดสินใจของผู้มีสิทธิ์

Scheduler ทำรอบทุก 30 วินาที มี batch จำกัด 100; health refresh และ manifest scan เป็นงานแยก อย่าตีความว่าทุกไฟล์ในคลังใหญ่ถูกตรวจครบในรอบเดียว งาน Reserved ที่ยังไม่เสร็จอาจบล็อกการประกาศ Completed เพื่อไม่ทิ้งรูปที่มาช้า

Incident บันทึกผู้รับผิดชอบ เวลา UTC/เวลาไทย correlation ID, storage ID/job ID/evidence reference, error code, รุ่นก่อน–หลังและการตัดสินใจ ห้ามแนบ cookie/token/MFA secret, credential, พาธลับ ภาพหรือ GPS จริงใน log ทั่วไป

## Backup และกู้คืน

Checksum และ Fallback ไม่ใช่ backup การสำรองต้องครอบคลุมฐานข้อมูล metadata/bindings/audit/jobs พร้อมไฟล์ full/thumbnail ทุก storage ที่ยังถูกอ้าง และ key ring/certificate/protected configuration/สิทธิ์ของ service identity

1. กำหนด maintenance window และหยุด writers/worker; ยืนยันว่าไม่มี I/O ค้างก่อน snapshot หากต้องการ online backup ต้องออกแบบ consistency protocol เพิ่ม ไม่ถือว่าคัดลอกตามเวลาคร่าว ๆ เพียงพอ
2. สำรอง PostgreSQL แบบ consistent ด้วยเครื่องมือที่ตรงรุ่น พร้อม manifest ไฟล์ immutable, SHA-256/bytes และ backup ของ keys/config แยกเข้ารหัส สิทธิ์จำกัด บันทึก backup set ID เดียวกัน
3. ทดลอง restore ไปฐานข้อมูลใหม่และพื้นที่ทดสอบแยก **ห้าม restore ทับ Preview/Production** เตรียม alias/mount/config ให้ตรงหลักฐานเดิม ไม่แก้ fingerprint เพื่อข้าม guard
4. เปิดแอปใหม่ด้วย worker ปิดไว้ก่อน ตรวจ migrations, binding ทุกตัว, full/thumbnail SHA และ bytes, file permissions, key ring/certificate ที่ถอดรหัสได้ และ configuration identity
5. ทดสอบผ่าน API: เจ้าของอ่านได้ HR เฉพาะหน่วยงาน; บุคคลอื่น/หัวหน้า/Admin ที่ไม่มีสิทธิ์ต้องถูกปฏิเสธ ทุก variant รวม download เป็น no-store
6. Probe ทุก storage ใหม่ ตรวจงาน Pending/Running/Blocked และ lease ค้าง; ทดสอบ resume งานเดิม ไม่สร้าง job ทดแทนโดยไม่ตรวจ
7. บันทึกระยะเวลากู้คืน/จำนวนข้อมูลสูญเสียจริงเทียบ RTO/RPO ที่เจ้าของระบบกำหนด ผู้รับผิดชอบตรวจครบและลงนามก่อนเปิด writer

Automated drill ของ Task 8 ใช้ PostgreSQL ชั่วคราว รูปสังเคราะห์และ host ใหม่บนเครื่องพัฒนา ทดสอบการคืนไฟล์ไป root เดิมที่ fixture เป็นเจ้าของ ไม่ใช่ disaster recovery ข้ามเครื่อง/volume และไม่ใช่ผลทดสอบ SMB/NAS

## NAS และข้อจำกัดก่อน Production

ยังไม่มี NAS test share จึงยังไม่ผ่าน gate นี้ เมื่อพร้อมต้องขออนุญาต disruption เจาะจง test share ที่ไม่มีข้อมูลจริง แล้วตรวจ write/read/rename/flush, disconnect/reconnect, mount หายแต่ directory อยู่, read-only, quota เต็ม, DB/audit fail, copy crash/resume และ SHA ทั้งสอง variant ก่อน–หลัง เก็บรุ่น NAS/SMB/mount options/service identity โดยไม่เก็บ secret

ต้องตรวจ durability ของ SMB/server cache/power loss จริง ไม่อ้างจาก fsync บน local disk; native Skia/HarfBuzz ต้องโหลดได้บน runtime เป้าหมายพร้อมฟอนต์ไทย; decoder มีขีดจำกัดแต่ไม่ใช่ sandbox ป้องกัน native vulnerability ทั้งหมด ผู้ดูแล OS/DBA ที่มีสิทธิ์ระดับสูงอยู่นอกขอบเขตป้องกันของ application authorization

การส่งภาพถือ shared authorization lock จนส่งเสร็จ มี timeout/เพดานข้อมูล แต่ throughput/slow-client และ manifest ขนาด Production ต้องทดสอบโหลดจริง รุ่นนี้รับรองขอบเขต single API process ไม่ถือว่าพร้อม multi-replica

## รายการลงนามก่อนใช้งานจริง

- [ ] Operations: NAS drill และ native runtime เป้าหมายผ่าน
- [ ] ผู้ดูแล backup: restore ข้ามเครื่องตาม RTO/RPO ผ่านและ key custody พร้อม
- [ ] เจ้าของข้อมูล/HR: สิทธิ์ การแจ้งพนักงานและนโยบายเก็บรักษาอนุมัติ
- [ ] ผู้ดูแลระบบ: monitoring/capacity/incident และ load test ผ่าน
- [ ] เจ้าของโครงการ: ยอมรับข้อจำกัดและอนุมัติ Production โดยชัดแจ้ง
