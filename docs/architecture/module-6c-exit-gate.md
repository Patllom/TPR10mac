# Module 6C — รายงานตรวจรับและส่งต่อ Task 9

สถานะล่าสุด 17 ตุลาคม 2026: **ชุดตรวจทางเทคนิคผ่านครบ พร้อมไปขั้นจัดทำแผน6D — มือถือ/NASพักตามคำสั่งผู้ใช้ ยังไม่รับรอง Production และยังไม่ commit/push/merge**

## ผลตรวจรับรอบล่าสุด — 17 ตุลาคม 2026

ใช้ worktree6C ที่ HEAD `365946e` พร้อมงานแก้Reviewที่ยังไม่commit ติดตั้ง Microsoft .NET SDK10.0.401 ใน `/Users/theerapat_k/.dotnet` และเปิด Docker29.8.1 ตามอนุมัติ ไม่มีการแก้ business source ในรอบนี้ ไม่เปลี่ยน global.json, timeout, retry หรือ assertions

| รายการ | ผลใหม่ | หลักฐานใน `.superpowers/sdd/2026-09-26-module-6c-online-attendance/` |
| --- | --- | --- |
| Frontend test | 60/60, exit0 | `task9-oct17-node.log` |
| Lint | exit0 | `task9-oct17-lint.log` |
| Frontend production Build ก่อน/หลัง dev | exit0 | `task9-oct17-build.log`, `task9-oct17-final-build.log` |
| Backend ทั้งโครงการ รวม OpenAPI | 1,481/1,481, exit0, 36.3226นาที | `task9-oct17-backend.log` |
| Backend Build | exit0 ไม่มี warning/error | `task9-oct17-backend-build.log` |
| format verify / diff check | exit0 | `task9-oct17-format.log` / ตรวจGitรอบส่งมอบ |
| HTTPS production-build | 74/74, exit0 | `task9-oct17-prod.log` |
| HTTPS dev | 72/72, exit0 | `task9-oct17-dev.log` |

Production: identity13, scopes11, boundary2, directory7, storage3, capture27, history11; devเหมือนกันยกเว้นboundary2ตามrunbookเดิม ไม่มี skip/retry การตรวจpublic request timingในproductionมี1,112รายการสูงสุด0.014วินาที ผลสามsource hashesตรงตารางเดิมก่อนและหลังชุดตรวจ Backendรวมlocal two-target DB/file restoreและfinal-audit deadline regressionแล้ว

**ข้อสรุปการไปต่อ:** ไม่พบtest failureหรือข้อขัดข้องทางเทคนิคใหม่จากชุดตรวจปัจจุบัน จึงไปขั้นจัดทำและขออนุมัติ Implementation Plan6Dได้ ไม่ต้องรอมือถือ/NASที่ผู้ใช้พักไว้ แต่ไม่ได้แปลว่าModule6ทั้งหมดเสร็จ งาน6Cยังต้องcommit/push/PR/mergeตามคำสั่งก่อนเริ่มbranch6Dตามdelivery map

**ข้อจำกัดที่ยังคงอยู่:** timeoutเดือนกันยายนยังไม่มีcausal fix; รอบใหม่ไม่พบซ้ำทั้งdev/prod จึงบันทึกเป็นความเสี่ยงด้านเสถียรภาพที่ต้องติดตาม ไม่ลบผล73/74เดิมและไม่อ้างว่าแก้หาย การวางแผน6Dไม่ใช่การอนุมัติProduction ต้องตรวจอุปกรณ์จริง/NASเมื่อกลับมาใช้งาน พร้อมload/deployment recoveryและผู้รับผิดชอบข้อมูลก่อนเปิดใช้จริง

ส่วนต่อไปนี้เป็นประวัติการตรวจเดือนกันยายนและการตรวจเบื้องต้น17ตุลาคมก่อนจัดเตรียมruntime ผลรอบล่าสุดด้านบนใช้สำหรับประเมินความพร้อมปัจจุบัน

วันที่ 28 กันยายน 2026 บนสาขา `codex/module-6c-online-attendance` ฐานตรวจอิสระ `8a965af2b47463c58b40005e99ef6e5a51f18adb..365946ed6deb3032001571ae34db1ee604e99e3c` การแก้ review รอบนี้ยังอยู่ใน working tree หลังฐานดังกล่าว ไม่มี push/merge ในรอบนี้

## เกณฑ์แยกตามขอบเขต

### การทบทวน 17 ตุลาคม 2026

ผู้ใช้ให้พักการตรวจมือถือจริงและ NAS จริงไว้ก่อน จึงแยกสองรายการนี้เป็น **เลื่อนตรวจตามคำสั่งผู้ใช้** ไม่ใช้เป็นข้อห้ามในการวางแผน6D แต่ไม่ถือว่าผ่านและยังต้องตรวจรับก่อนเปิดใช้งานในสภาพแวดล้อมนั้น การเลื่อนนี้ไม่ยกเว้นเกณฑ์ทางเทคนิคหรืออนุมัติ Production

ตรวจใหม่ใน worktree6C: Node tests60/60, Lint และ production Build ผ่าน (exit0) ตรวจรายการTODO/FIXME/NotImplementedExceptionใน Attendance backend/components/lib ไม่พบ ทั้งนี้ไม่ใช่หลักฐานว่าปลอดบั๊กทั้งหมด ยังมีงานแก้Task9ที่ไม่commit และสัญญาส่งต่อ6Dอยู่ในรายงานนี้

ยังตรวจครบไม่ได้: Docker daemon ไม่ทำงาน และ SDK10.0.401ที่เคยใช้ใน temporary directoryหายไป ขณะที่ SDKในเครื่องเป็น10.0.400 ไม่ตรงglobal.json จึงยังไม่ได้รัน backendเต็มและ HTTPS matrixใหม่ ผลเดิม1,481/1,481และ73/74ยังเป็นประวัติ ไม่ใช่ผลตรวจวันนี้ อาการHTTPS timeoutเดิมยังเปิดอยู่ ต้องเตรียมruntime/Dockerแล้วตรวจให้ครบก่อนรับรอง technical gate การจัดทำแผน6Dทำได้ แต่ยังไม่รับรองพร้อมmerge/Production

### ติดตามวันที่ 29 กันยายน 2026 (เวลาไทย)

รัน scopes production-build พร้อม `TPR10_SMOKE_DIAGNOSTICS=1` อีกหนึ่งรอบเพื่อเก็บหลักฐาน: ผ่าน11/11 มีคำขอสาธารณะ234รายการ เวลาสูงสุด0.010วินาที ไม่พบอาการค้างในรอบนี้ (`task9-resume-diagnostic.log`) ตรวจ transport แยกจากแอปด้วย `node --test infra/nginx/test-web-transport.mjs` ผ่าน1/1 ยืนยันการใช้ socket ซ้ำและ WebSocket echo ของ configuration ปัจจุบัน (`task9-resume-transport.log`) Node60/60, Lint และ frontend production Build ผ่านใหม่ (`task9-resume-node.log`, `task9-resume-build.log`)

ไม่ได้แก้ source, เพิ่ม timeout/retry หรือผ่อน assertions ในรอบติดตามนี้ และไม่ได้รัน backendเต็มหรือ HTTPS matrixเต็มซ้ำ ผล backend1,481/1,481ยังเป็นหลักฐานรอบ28กันยายน ไม่ถือว่าผล scopes ผ่านครั้งนี้ลบผล matrix73/74เดิม สาเหตุปัจจุบันยังไม่ยืนยัน: หลักฐานเก่า6Bที่ค้างก่อนเชื่อมต่อDocker→hostเป็นเพียงแนวทางสืบสวน ไม่ใช่ข้อพิสูจน์ของ6C จึงคง Task9เปิดไว้ ขั้นต่อไปต้องเก็บ timings ในรอบที่เกิดอาการจริงก่อนแก้ โดยไม่สุ่มปรับระบบเครือข่ายหรือ TLS ของเครื่อง


| เกณฑ์ | สถานะ |
| --- | --- |
| Technical | แก้ 5 ประเด็น review พร้อม regression; backend1481/1481, Node60/60, Build/Lint/format ผ่าน แต่ matrix production ล่าสุดมี scopes timeout 1 เคส จึงยังไม่ผ่าน gate |
| Mobile-device | ยังไม่มีผล Safari/iOS และ Chrome/Android บนเครื่องจริง |
| Local storage/recovery | ผ่าน drill ใหม่ 1/1 ด้วยคู่เข้า–ออกจริงผ่าน HTTP และภาพสังเคราะห์ในโฟลเดอร์จริงสองแห่ง |
| NAS | รอ share จริงและอนุญาตทดสอบ disruption; local folder ไม่ใช่ NAS |
| Production | ยังไม่อนุมัติ ต้องมี device/NAS/load/restore และผู้รับผิดชอบข้อมูลลงนาม |

ดู [แบบตรวจรับอุปกรณ์และสภาพแวดล้อม](module-6c-device-acceptance.md) ไม่ถือว่า Module 6 ทั้งหมดเสร็จ เพราะ 6D ยังไม่พัฒนา

## การตรวจอิสระและการแก้ไข

ผู้ตรวจอิสระ Pascal อ่าน diff ทั้งสาขาเทียบ spec/plan และ ledger โดยไม่แก้ไฟล์หรือรัน tests พบ Important 5 ข้อ ไม่มี Critical หรือ Minor ผู้ทำหลักตรวจสาเหตุและสร้าง regression ก่อนแก้ ไม่ใช้คำว่า independent review กับการตรวจซ้ำของผู้เขียนเอง

| ประเด็น | หลักฐาน RED | การแก้และหลักฐานเฉพาะส่วน |
| --- | --- | --- |
| ผล upload/operation ค้างข้ามบัญชีโดยไม่มี auth event | browser 2 เคสไม่ล้างผลบัญชีเดิม และเพิ่ม RED อีก 1 เคสเปลี่ยนบัญชีก่อน challenge | ตรวจ session หลังรับข้อมูลครบก่อนแสดงผลและหลังรับ challenge ก่อนเปิดกล้อง; mismatch/ตรวจไม่ได้ล้างข้อมูลส่วนตัว |
| หมดอายุระหว่าง final audit | backend 2 เคสคาด 409/400 แต่ได้ 201 | ตรวจ deadline อีกครั้งก่อนและหลัง SaveChanges; จำกัดเวลา save/commit ด้วย lease/challenge ที่เหลือ; focused suite 16/16 |
| กล้องถูกขัดจังหวะแต่ยังมีเฟรมค้าง | browser เคส mute หลัง ready ไม่แสดงข้อผิดพลาด | จัดการ mute/ended หยุดกล้องและทิ้งร่าง; ต้องได้รับเฟรมใหม่ก่อนวาด canvas |
| ข้ามเที่ยงคืนหลังออก challenge | browser ไม่แสดงการยืนยันทำงานต่อเนื่อง | เวลาเซิร์ฟเวอร์อ้างอิง monotonic elapsed time; ตรวจใหม่ก่อนส่งและหลังรอ GPS |
| นาฬิกา/GPS ผิดได้ข้อความทั่วไป | browser 2 เคสไม่ได้คำแนะนำแก้เวลา | แปลงเฉพาะ ProblemDetails type ใน allowlist เป็นภาษาไทย ไม่แสดงข้อความจากเซิร์ฟเวอร์โดยตรง |

`task9-capture-red.log`: RED 6 เคสใหม่ตรง finding และ timeout อีก 1 เคสเดิมระหว่างเริ่มกล้อง (ไม่ใช่ RED ที่ตั้งใจ) หลังแก้ `task9-capture-green.log` ผ่าน 25/25 ทั้งชุด จากนั้นเพิ่ม coverage เฟรมไม่มาและ parser error แบบ allowlist; ผลชุดสุดท้ายบันทึกด้านล่าง

ข้อจำกัด: การตรวจหลัง response ลดความเสี่ยงผลค้าง แต่ไม่ใช่การรับรองว่าเบราว์เซอร์ตรวจจับการเปลี่ยน cookie ทุก instant ได้เอง การตรวจสิทธิ์ API และการล้างเมื่อ auth/focus/visibility ยังคงจำเป็น

## การกู้คืนข้อมูล local

`AttendanceRecoveryExitTests.Closed_pair_on_two_local_targets_survives_database_and_file_restore` ใช้ HTTP challenge/upload เข้า–ออกจริง ภาพสังเคราะห์ที่ผ่าน image pipeline จริง PostgreSQL จริง และ local filesystem จริง ตรวจว่า:

1. เข้าเก็บใน local-0 แล้วออกเก็บใน local-1; ยังมีวันทำงานเดียวและสอง events/bindings
2. รูปเดิมยังอ่านได้หลังเปลี่ยน write target ทั้ง full/thumbnail/download
3. `pg_dump`/`pg_restore` ไปฐานข้อมูลทดลองใหม่ และคืนไฟล์ทั้งสี่สำเนาโดยตรวจ checksum
4. เปิด host ใหม่ อ่าน bytes เดิมได้ด้วยเจ้าของ ขณะที่ anonymous ได้ 401 และ response เป็น no-store
5. day ID/version/effective times และ event/binding counts ไม่เปลี่ยน

การเปลี่ยน pointer ใน drill นี้เป็นการ seed configuration ที่ฐานข้อมูล ไม่ได้ทดสอบปุ่มของ operator ใน drill เดียว; authorization/version/audit ของ switch endpoint มีชุด StoragePin/StorageRace แยกใน full suite การคืนไฟล์ใช้ root/fingerprint และ protected configuration เดิม ไม่ใช่ relocation ข้ามเครื่อง ไม่ใช่ process-kill และไม่ใช่ NAS failure drill ไม่มีการแตะฐานข้อมูลหรือรูปพนักงานจริง

รอบ 12:54 UTC รัน `Synthetic_visual_artifacts` อีกครั้งผ่าน 4/4 แล้วเปิดดู portrait thumbnail และ landscape full จริง อ่าน `25/09/2026 00:00:00 (UTC+7) — เข้า/ออก` ได้โดยข้อความอยู่ในแถบภาพ ไม่ใช่ DOM overlay ภาพเป็นแถบสีสังเคราะห์ในพื้นที่ชั่วคราวนอก Git ไม่ใช้แทนภาพจากกล้องมือถือจริง

## ผลตรวจรอบนี้

ใช้ Node 24.19.0 และ .NET SDK 10.0.401 ตาม runtime pin; temporary SDK ที่สมบูรณ์ ไม่เปลี่ยน global.json รันจาก worktree แยกด้วย Docker PostgreSQL ข้อมูลทดสอบเท่านั้น

| คำสั่ง/ชุดตรวจ | ผลที่ยืนยันแล้ว |
| --- | --- |
| `npm test` ก่อนเพิ่ม parser coverage | exit 0; 59/59 |
| `npm run lint` หลังแก้ frontend | exit 0 |
| backend final-audit RED | exit 1; 2 ล้มเหลวตามคาด |
| backend failure + recovery หลังแก้ | exit 0; 16/16 ไม่ข้าม |
| HTTPS dev capture หลังแก้ | exit 0; 25/25 |
| HTTPS dev identity รอบแรก | exit 0; 13/13 |
| HTTPS dev scopes รอบแรก | exit 1; 10 ผ่าน 1 timeout ที่ navigation `/login` |
| backend full suite รอบแรก | ยกเลิกหลังแก้ backend จึงไม่ใช้เป็นผลผ่าน; เริ่ม full suite ใหม่แล้ว |

### ชุดตรวจสุดท้ายหลังแก้ review — 28 กันยายน 2026 (UTC)

| คำสั่ง | เวลา/ช่วงเวลา | exit / ผล |
| --- | --- | --- |
| `npm test` | 12:51–12:52 | 0; 60/60 ไม่ล้มเหลว/ไม่ข้าม |
| `npm run lint` | 12:51–12:52 | 0; ไม่มี warning |
| `dotnet build backend/TPR10.sln --no-restore` | 12:51–12:52 | 0; 0 warnings / 0 errors |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | 12:51–12:52 | 0; แก้เฉพาะ whitespace ที่ verify รอบแรกพบแล้ว |
| `npm run build` หลังหยุด dev | ก่อน 12:53:02 | 0; ทุก route compile สำเร็จ |
| `git diff --check` | 12:57 | 0 |
| `dotnet test backend/TPR10.sln --logger 'console;verbosity=normal'` | ประมาณ12:44–13:22:02 | exit0; 1,481/1,481 ไม่ล้มเหลว/ไม่ข้าม รวม OpenAPI และ recovery/deadline ใหม่ ใช้เวลา37.2307นาที |

HTTPS ใช้ `runHttpsSmoke({port, spec, e2e:true})` จาก `infra/nginx/https-smoke-fixture.mjs` ทุกชุดตรวจ certificate ด้วย CA เฉพาะ Firefox profile, cookie/CSRF, hostile Host และ API ที่ไม่เปิดพอร์ตสาธารณะ ไม่มี TLS bypass และ Playwright retries=0

| Browser suite | dev4000: ผ่าน / เวลาสิ้นสุด UTC | production-build4001: ผ่าน / เวลาสิ้นสุด UTC |
| --- | --- | --- |
| identity | 13/13 · 12:34:54 | 13/13 · 12:53:42 |
| scopes | 11/11 · 12:49:23 (รันทวน; มี timeout รอบแรกตามด้านล่าง) | 11/11 · 12:54:20 |
| scopes-boundary | ไม่อยู่ matrix dev ตาม runbook6B | 2/2 · 12:54:36 |
| attendance-directory | 7/7 · รอบ 12:47 | 7/7 · 12:55:04 |
| attendance-storage | 3/3 · 12:48:00 | 3/3 · 12:55:23 |
| attendance-capture | 26/26 · รอบ 12:52 | 26/26 · 12:56:19 |
| attendance-history | 11/11 · 12:48:25 | 11/11 · 12:56:36 |

ตารางข้างต้นเป็นผลก่อนเพิ่ม guard ตรวจบัญชีหลัง challenge จึงไม่ใช้แทนผลรอบสุดท้ายด้านล่าง ไม่ใช้ตารางนี้ลบล้าง timeout รอบก่อนหรือแทนการทดสอบมือถือจริง ไม่ได้หยุด Preview ของผู้ใช้เพื่อแย่งพอร์ต

### ผลหลังเพิ่ม account guard ก่อนเปิดกล้อง — หลักฐานล่าสุด

- `task9-challenge-account-red.log`: RED1 พบเปลี่ยนบัญชีแล้วกล้องยังเปิด; หลังเพิ่ม guard `task9-capture-final2-dev.log` ผ่าน27/27 เวลา13:12:55 UTC, Node60/60 และ Lint exit0
- `task9-frontend-final2-build.log`: Build exit0 ก่อน13:14:26 UTC
- `task9-https-final2-prod.log`: **exit1**, identity13/13, scopes10/11, boundary2/2, directory7/7, storage3/3, capture27/27, history11/11 รวม73ผ่าน/1timeout ไม่มี skipped จบ13:18:35 UTC
- เคสที่ timeout: `scopes.spec.ts` “พนักงานอ่านเฉพาะ A ไม่มี restricted field และ logout/back/เปลี่ยนบัญชีไม่แสดงข้อมูลเก่า” ค้างตั้งแต่ `login` แรก (`:52`) ก่อนตรวจสิทธิ์ โดย document เป็น interactive และ pending scripts `255-…js`, `main-app-…js` ไม่ใช่ผลยืนยันว่า logic สิทธิ์ผิด และยังระบุ root cause ไม่ได้
- วินิจฉัยเพิ่มด้วย `TPR10_SMOKE_DIAGNOSTICS=1` สองรอบ: scopes11/11 ทั้งสองครั้ง (`task9-scopes-diagnostic*.log`) ข้อมูล public proxy ที่เก็บได้ตอบเร็วสูงสุด0.009/0.010วินาที แต่ไม่จับช่วงที่ค้าง จึงไม่ใช้ผลนี้อ้างว่าแก้แล้ว
- แยกทดสอบ public login20contexts โดยไม่ล็อกอิน: ผ่าน20/20 (`task9-public-transport-diagnostic.log`); สูงสุด0.015วินาที มี499สำหรับ asset สองคำขอที่ยกเลิกเร็ว0–0.001วินาที ไม่ใช่หลักฐานของ timeout30วินาที ถอดเคสวินิจฉัยชั่วคราวออกจาก `scopes.spec.ts` แล้ว
- เก็บ diagnostic mode แบบ opt-in ใน fixture ไว้สำหรับจับเหตุการณ์ครั้งถัดไป: เฉพาะ public path/status/timing ไม่เก็บ query, headers, body, cookie หรือ private DOM และไม่แก้ timeout/retry/skip

**ขั้นถัดไป:** ต้องจับหลักฐาน request ที่ค้างด้วย diagnostics แล้วแก้สาเหตุพร้อม regression ก่อนปิด Technical gate ไม่ commit/push/merge หรือเขียน ledger ว่า Task9 complete ในรอบที่ยังมีข้อค้างนี้ การวินิจฉัยที่ผ่านไม่ใช่การยอมรับความเสี่ยงแทนผู้ใช้ และไม่เปลี่ยนการตั้งค่า Docker/Firefox/TLS ทั้งเครื่องโดยพลการ

หลักฐานอยู่ใน `.superpowers/sdd/2026-09-26-module-6c-online-attendance/task9-*.log` ซึ่งไม่ commit เพราะอาจมี diagnostic data เก็บ workspace ไว้จนกว่าจะปิด external gates ผลบน production-build พอร์ต 4001 ไม่ใช่การ deploy หรืออนุมัติ Production

### ลายนิ้วมือ source ที่แก้ในรอบ review

ตรวจ SHA-256 ก่อน production matrix; backend ชุดเต็มเริ่มหลังแก้ production source แล้ว การจัด whitespace ของ initializer ใน recovery test ไม่มีผลต่อพฤติกรรม

| ไฟล์ | SHA-256 |
| --- | --- |
| `backend/src/TPR10.Api/Attendance/Recording/AttendanceRecorder.cs` | `edd1892d0d3f64feacf938f002338ee8b3c5729956b7955913234ce78ccde93f` |
| `components/attendance/useLiveCapture.ts` | `1c2d7a06f1627c98ceb65cc2da12e3b145d2fe663222169f7e919290aaa67b8a` |
| `lib/attendance/recording-client.ts` | `1e45933f92368452a32f89fa87e0538e70b523415a39fda68a6188c2fcf94b62` |

### ผล timeout ที่ต้องเก็บไว้

รอบ dev scopes แรกมี 1 timeout ขณะเริ่ม navigation `/login` โดยยังมี pending document และหน้าเป็น `about:blank`; ยังไม่ถึงการตรวจ restricted DOM ของเคสนั้น การรันทวนแบบเดิมผ่าน 11/11 โดยไม่เพิ่ม timeout/retry/skip หรือแก้ assertions **ยังพิสูจน์ต้นเหตุไม่ได้และไม่อ้างว่าแก้ timeout แล้ว** รวมถึง timeout เริ่มกล้องหนึ่งครั้งในรอบ RED ซึ่งชุดหลังผ่านทั้งหมด ต้องติดตามความเสถียรเพิ่มเติมก่อน Production ไม่ลบผลล้มเหลวเพื่อแสดงเฉพาะผลผ่าน

## สัญญาส่งต่อ Module 6D

- `AttendanceDay` เป็นเจ้าของวันทำงาน Snapshot ตอนเข้า `EffectiveInUtc`/`EffectiveOutUtc` และ `Version`; `AttendanceEvent`/รูปเดิมเป็นหลักฐาน immutable
- เวลา authoritative มาจากเซิร์ฟเวอร์ instant เดียวกับ image stamp; device time เป็นข้อมูลประกอบ ไม่ใช้แทนเวลาเข้า–ออก
- หนึ่งคู่ต่อ employee/workDate และหนึ่งรอบเปิดต่อคน ข้ามวันใช้ workDate ตอนเข้า แต่ Site เข้า/ออกเป็นคนละค่า
- 6D ต้องถือ identity lock `7241002` ก่อน `AttendanceEmployeeLock.AcquireAsync` (`7241003` + employee-derived key) ใน transaction เจ้าของเดียวกัน และตรวจ version เพื่อไม่ชน checkout จริง
- แก้ effective values ผ่านคำร้อง revision/หัวหน้า→HR เท่านั้น ห้ามแก้ original event/evidence หรือสร้าง direct correction endpoint ใน 6C
- Route snapshot เป็นประวัติ ไม่ทดแทน current grants; หัวหน้าเห็น GPS ไม่เห็นภาพ HR ตามหน่วยงาน เจ้าของเห็นของตนเอง Admin ไม่ bypass
- ต้องจัดทำและอนุมัติ Implementation Plan 6D ก่อนพัฒนา state transitions, 7 วัน/รอบ, ส่งกลับ revision ใหม่, ลำดับอนุมัติและ audit

## สิ่งที่ยังไม่ได้พิสูจน์และข้อวินิจฉัย

- ผู้ตรวจเว้น actual mobile และ NAS: คงเป็น external gates เพราะไม่มีอุปกรณ์/share; หากสรุปว่าผ่านจาก mock จะเสี่ยงกล้อง mount หรือสิทธิ์บนอุปกรณ์จริงผิด
- Process-kill recovery และ deployment restore: local coordinated drill ไม่รับรอง crash ทุกจุด; ต้องทดสอบก่อน Production
- Throughput ของ exclusive metadata-delivery lock: คง implementation ที่มี bounded delivery แต่ยังต้องวัด load จริง หากไม่พออาจต้องปรับก่อนเปิดใช้
- Task 9 เอกสาร/ผลตรวจ: ผู้ทำหลักจัดทำและยืนยันจากผลจริง ไม่อ้างว่าผู้ตรวจอิสระเห็นผลที่ยังไม่เกิด
- 6D และ biometric/virtual-camera/GPS spoof resistance: ไม่เพิ่ม scope ใน 6C; ยังแก้เวลาที่ขาดไม่ได้และไม่ใช่ระบบพิสูจน์ตัวบุคคล/ป้องกันปลอมตำแหน่งระดับฮาร์ดแวร์
- เพิ่ม recovery test เป็น acceptance characterization ของระบบเดิม ไม่อ้างว่ามี business-feature RED และไม่ทำให้ระบบเสียเพื่อสร้าง RED
- การตรวจเฟรมใหม่ใช้ `requestVideoFrameCallback` และปฏิเสธเบราว์เซอร์ที่ไม่รองรับอย่างชัดเจน ไม่ fallback ไปส่งเฟรมค้าง; ผลกระทบคืออุปกรณ์รุ่นเก่าอาจต้องอัปเดตเบราว์เซอร์และยังต้องผ่าน device gate
- คง log/workspace ที่ ignored ไว้เพื่อ gates ที่ยังค้าง ต้องสำรองแยก ไม่อ้างว่า Git เก็บหลักฐานเหล่านี้แล้ว

## เจ้าของข้อมูลและการเก็บรักษา

องค์กรยังต้องแต่งตั้งผู้รับผิดชอบภาพ/GPS และอนุมัตินโยบายการแจ้งพนักงาน การเข้าถึง สำรองและเก็บรักษาอย่างเป็นทางการ ระยะแรกยังไม่ลบหลักฐานอัตโนมัติ ไม่เผยแพร่ภาพ/พิกัดจริงใน Git และไม่ตีความว่าการเก็บไม่จำกัดเวลาคือผ่านข้อกำหนดกฎหมายโดยอัตโนมัติ
