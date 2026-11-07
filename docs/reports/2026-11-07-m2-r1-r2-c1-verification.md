# ผลตรวจ M2-R1 และ R2-C1

สถานะ: **ปิดช่องว่างเทสต์ทั้งสองรายการใน working tree แล้ว** ยังไม่commit/push/merge ฐานmain f597772 branch codex/m2-r1-r2-c1 ไม่แก้โค้ดระบบหรือfixture production

## M2-R1

ผู้ลงมือ Cicero ผู้ตรวจ Socrates ตรวจบัญชีaliasสองบัญชี personเดียวกัน ทั้งสองมีsessionและMFAที่ถูกต้อง แต่มีapproved grantเพียงบัญชีเดียว

เพิ่มเทสต์HTTPจริง3กรณี Grant/Policy/Request บัญชีที่มีgrantค้นscopeและอ่านได้200 อีกบัญชีค้นได้รายการว่างและเรียกscopeตรงได้403 finance-access-denied ตรวจsessionก่อนและหลังยังActive ไม่ใช่การปฏิเสธเพราะloginผิด มีpending proposalของaliasเพื่อให้batch authorizationมีcandidateจริง ไม่ใช่ผ่านเพราะไม่มีscopeให้ตรวจ ผู้ตรวจยืนยันปิดM2-R1ได้

## R2-C1

ผู้ลงมือ Gibbs ผู้ตรวจ Carver เรียกrunHttpsSmokeจริงผ่านmock subprocess/network wait ทดสอบ7ชุดค่าสี×8scenario ตรวจ9จุดเรียก subprocess: run wrapper, Next, Playwright และnative direct Docker readiness/logs, curl2จุด, cleanup2จุด

ตรวจnormalization3จุดและoverrides โดยnative direct callsยังรับenvironmentเดิมตามสัญญา เก็บFORCE_COLOR=0/ค่าว่าง ไม่แก้parent environment และไม่ปิดwarningอื่น

ผู้ตรวจพบช่องว่างstdioของPlaywright แก้โดยยืนยันinherit ทดลองเปลี่ยนเป็นignoreแล้วเทสต์ล้มเหลวตรงassertionครบ7กรณี รวมmutationที่จับได้8แบบ (normalization/override7แบบและstdio1แบบ) ใช้สำเนาชั่วคราว ไม่แก้fixtureต้นฉบับ ผู้ตรวจทวนแล้วไม่มีข้อค้าง

## ผลตรวจสุดท้าย

- Backend alias3/3ผ่าน `/private/tmp/m2r1-controller-final.log`
- Node scoped16/16ผ่าน `/private/tmp/r2c1-controller-final.log` รวมhelperและPlaywright workerจริงเดิม
- Backend Buildผ่าน0warnings/errors `/private/tmp/m2r1-backend-build.log`
- Formatทั้งsolutionผ่าน `/private/tmp/m2r1-format-final.log` (รอบแรกพบwhitespaceในเทสต์ใหม่ แก้เฉพาะไฟล์และรันเทสต์ซ้ำแล้ว)
- Frontend Build/Lintผ่าน `/private/tmp/m2r1-r2c1-web-build.log`, `/private/tmp/m2r1-r2c1-lint.log`
- Lintไฟล์JSใหม่หลังreviewผ่าน `/private/tmp/r2c1-controller-final-lint.log`
- diff checkผ่าน

## ข้อจำกัดและงานที่คงอยู่

นี่คือการเพิ่มcoverageสำหรับโค้ดที่ถูกต้องอยู่แล้ว ไม่ใช่การอ้างว่าพบบั๊กสิทธิ์ใหม่ เทสต์fixtureจำลองDocker/TLS/network ไม่ใช่HTTPSacceptanceจริง และใช้global mocks/environmentภายในtest processที่แยกจากไฟล์อื่นตามnode --testปกติ ไม่รองรับshared-process concurrent execution

คงOpenAPI500และHTTPS504รอหลักฐานจริง Minor log/EF10102, retry error code, ภาษาUIและvalidator/UIตามเดิม พักมือถือจริง/NASจริง ไม่รันเทสต์ทั้งโครงการซ้ำ
