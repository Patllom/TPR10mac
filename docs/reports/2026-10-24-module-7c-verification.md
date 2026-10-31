# รายงานตรวจสอบ Module 7C การจ่ายเงินทดรองและส่วนเพิ่ม

เริ่มรายงานโดย Task15 วันที่ 31 ตุลาคม 2569 บน `codex/module-7b-design` ฐาน `2711025c38413d53b998de03f9bacbeb7c30da68` การตรวจทั้ง branch ให้ controller ใช้ `origin/main=440e4c8` โดย `merge-base(origin/main, HEAD)=71c006d` ไม่ใช้ local main ที่ค้าง Module6

**สถานะ: Task 15 ผ่านการตรวจครบแล้ว** Backend 3,064 กรณีและ Frontend 175 กรณีผ่าน พร้อม Build, Format, Lint และ HTTPS matrix ผู้ลงมือและผู้ตรวจอิสระแยกกัน ผลนี้ไม่ใช่การอนุมัติใช้งานเงินจริงหรือการทดสอบมือถือ/NAS จริง รายละเอียดหลักฐานและข้อจำกัดอยู่ด้านล่าง

## แผนที่ C01–C25

ไฟล์ทดสอบทั้งหมดในตารางอยู่ใต้ `backend/tests/TPR10.Api.IntegrationTests` ชื่อย่อคอลัมน์หลักฐานระบุ class และ method ที่ตรวจโค้ดจริง ไม่ถือว่ามีชื่อเทสต์แล้วผ่านทุก branch หลักฐานการรัน/ช่องว่างดูส่วนถัดไป

| เกณฑ์ | หลักฐานและ assertion ที่มีจริง | ขอบเขต/ช่องว่าง |
|---|---|---|
| C01 | `FieldAdvancePaymentAtomicityTests.ConcurrentReservationsCannotExceedCeiling`: คำสั่ง 6,000 สองงวดบนวงเงิน 10,000 สำเร็จหนึ่งครั้ง อีกครั้ง 409, Reserved=6,000 และ ledger/receipt/event อย่างละหนึ่ง; `FieldAdvanceInstallmentTests.Concurrent_submissions_do_not_share_available_balance` | service/DB ไม่ใช่ load test production |
| C02 | `FieldAdvanceInstallmentTests.ReturnedKeepsReservation`: ส่งกลับคงกันไว้ ลดคืนส่วนต่าง เพิ่มต้องมีวงเงิน | UI ทุกลำดับยังไม่อ้าง |
| C03 | `FieldAdvanceInstallmentTests.Editing_reviewed_content_preserves_history_and_requires_review_of_new_revision`: เปลี่ยนยอด/ช่องทาง/ปลายทางรักษาฉบับเก่าและต้องตรวจใหม่ | หน้าจอ edit/return/re-review ทุก branch ไม่ได้พิสูจน์ใน Task15 |
| C04 | `FieldAdvanceInstallmentTests.Destination_must_be_complete_and_owner_only`, `Cash_owner_can_be_prepared_but_other_recipient_cannot`; `FieldAdvancePaymentExecutionTests.Invalid_actual_keeps_attempt_and_reservation`: ข้อมูลจริง/ไฟล์ไม่ถูกต้องต้องไม่ consume | ไม่เชื่อมธนาคาร/ไม่จ่ายเงินจริง |
| C05 | `FieldAdvancePaymentExecutionTests.Concurrent_payers_or_accounts_cannot_steal_start`, `StartResponseLost`: attempt เดียว ผู้แพ้ record ไม่ได้; Task15 ordered races บังคับการรอ lock จริง | ไม่ปิด M2-R1 เพราะมีเคส alias ที่คล้ายกัน |
| C06 | `FieldAdvancePaymentExecutionTests.StartResponseLost`: เริ่มแล้ว Paid=0; record แล้ว Paid=3,000 Reserved=0 และ Consume เดียว; Task15 `Deferred_commit_failure_then_same_command_replay_has_one_effect` | ไม่ใช้ตรวจผ่านแทนหลักฐานโอน |
| C07 | Task15 `Deferred_commit_failure_then_same_command_replay_has_one_effect`: trigger ล้มตอน COMMIT, snapshot ไม่เปลี่ยน, command เดิมสำเร็จครั้งเดียว; `Restored_open_attempt_preserves_command_and_revoked_authority`: restore ยัง Open, กันไว้ 3,000 และ replay 403 หลังหมดสิทธิ์ | การ rollback DB ไม่ย้อนเงินจริง; ไม่มีการอ้างซ้อม outage production |
| C08 | `FieldAdvancePaymentRecoveryTests.ThreeOutcomesNeverAutoRetry`, `UnpaidRequiresFreshReviewAndPreservesOldRevision`: ผลสามแบบและกลับตรวจใหม่ก่อนจ่าย | UI Unknown/Matched ทุก branch ยังเป็นช่องว่างเดิม |
| C09 | `FieldAdvancePaymentAmendmentTests.CancelReleasesOnlyAfterConfirmation`, `CancelRacesStartOrRecordWithoutReturningPaidMoney`: คืนเมื่อยืนยันไม่จ่ายและไม่คืนยอดที่จ่ายแล้ว | การแข่งบริการเดิมเสริมด้วย ordered races ของ Task15 ในจุด start/hold |
| C10 | `FieldAdvancePaymentAmendmentTests.MetadataNeverChangesMoney`: คงยอด/ผู้รับ/การใช้วงเงิน เก็บข้อมูลแก้ไข | ไม่ใช่เครื่องมือแก้เงินจริง |
| C11 | `FieldAdvanceCeilingAmendmentTests.EverPaidBlocksOriginalAmendment`, `StartedAttemptBlocksBeginWithoutChangingRevisionOrReservation`; Task15 `Start_and_termination_or_amendment_have_one_serialized_winner`: สองลำดับ มี receipt เดียว ไม่มีทั้ง start และ amendment สำเร็จจากรุ่นเดิม | setup Task15 อนุมัติ policy ผ่าน service แต่ใช้คำขออนุมัติสังเคราะห์ |
| C12 | `FieldAdvanceSupplementTests.AggregateAuthorityIncrementOnlyPayout`: ฐาน 13,000 แต่ส่วนเพิ่มจ่ายเฉพาะ 3,000; `Delegate_uses_source_identity_and_owner_must_confirm_with_current_project_grants` | ไม่มีการย้ายเจ้าของ/โครงการ |
| C13 | `FieldAdvanceSupplementTests.Lower_limit_cannot_review_and_worker_uses_cumulative_basis`, `AggregateAuthorityIncrementOnlyPayout`: ตรวจอำนาจสะสมและวงเงินของแต่ละคำขอแยกกัน | ไม่อ้าง UI แสดงทุกสถานการณ์ |
| C14 | `FieldAdvanceSupplementTests.Concurrent_submissions_claim_one_slot_and_return_keeps_it`: slot ค้างหนึ่งรายการรวมส่งกลับ; C01 ตรวจหลายงวดจ่าย | ไม่ได้จำกัดจำนวนงวดเป็นหนึ่ง |
| C15 | `FieldAdvanceSupplementTests.Original_payout_remains_available_and_existing_family_hold_is_preserved`: หลักจ่ายต่อได้แต่ hold เดิมยังบล็อก | ส่วนเพิ่มยังไม่อนุมัติไม่ให้วงเงิน |
| C16 | `FieldAdvancePaymentAccessTests.Opposite_roles_compete_atomically_across_requests`, `Alias_with_own_grant_cannot_escape_person_history`; `FieldAdvanceSupplementTests.Whole_family_history_excludes_role_switching_on_supplements` | คง M2-R1; Task15 revocation ใช้แถว grant ใน transaction ทดสอบ ไม่อ้างว่าเป็น UI revoke |
| C17 | `FieldAdvancePaymentHttpTests.Prepared_response_and_download_recheck_rights_at_actual_execution`; `FieldAdvancePaymentAccessTests.Read_and_evidence_access_do_not_follow_family_membership_or_admin_status`; Task15 restore/revocation | หน้า queue pagination/account-switch ระหว่าง upload ไม่เพิ่ม coverage |
| C18 | `FieldAdvanceEvidenceTests.Rejected_content_creates_no_evidence_or_participation`, `Failed_migration_preserves_current_location_and_history`; Task15 `Location_switch_during_download_returns_no_stale_bytes_and_preserves_paid_result`: 409 ไม่ส่ง bytes เก่า อ่านใหม่ตรง bytes และผลจ่ายเดิมยังอยู่ | Task15 ใช้ bytes สังเคราะห์ใน store ไม่อ้างเป็นการตรวจเนื้อหารูป/PDF; NAS จริงพัก |
| C19 | Task15 `Committed_revocation_while_command_waits_denies_checker_or_payer`: เห็น waiter ใน pg_locks ก่อน commit revoke, 403 ไม่มี attempt; ordered start/amendment/termination/mismatch และ deferred COMMIT rollback | ไม่ใช้ guessed sleeps; timeout เป็นขอบเขตป้องกันเทสต์ค้าง ไม่ใช่ barrier |
| C20 | Task15 `Restored_7B_clone_migrates_twice_without_minting_money_or_merging_copy_family`: pg_dump/restore 7B clone, MigrateAsync สองครั้งและ generated idempotent SQL ซ้ำสองครั้ง, SourceRequestId เดิมคนละ family, ฐาน 10,000, draft spendable=0, Paid/Reserved=0, grant count เดิม; regression 7A/7B delivery | เทียบข้อมูลการเงินที่ระบุ ไม่ใช่ทุกคอลัมน์ฐานเก่าใน Task15 |
| C21 | `FieldAdvancePaymentRecoveryTests.MismatchHoldsEntireFamily`, `FieldAdvancePaymentAmendmentTests.ExplicitMismatchBeforeCorrectionRejectsStaleVersionsAndHoldsRootAndSupplement`; Task15 `Mismatch_and_sibling_installment_start_serialize_without_losing_hold`: สองลำดับ, hold คงอยู่ Reserved=6,000 Paid=0 ไม่ release/consume | sibling ในเทสต์ใหม่เป็นคนละงวดคำขอเดียว; root/supplement ใช้เทสต์เดิม ไม่อ้างเป็น race ข้ามคำขอใหม่ |
| C22 | `FieldAdvanceCeilingAmendmentTests.ActivationNeedsApprovalAndOldReservationsCleared`, `DispositionRetainsOriginalAndRequiresIndependentCurrentFinalRole`: ต้องครบอนุมัติใหม่และคืนงวดเก่า ถอน/ปฏิเสธไม่เปิดเอง | การแก้เหตุหนึ่งไม่ปลดเหตุอื่น |
| C23 | `FieldAdvancePaymentRecoveryTests.TakeoverPreservesOriginalPayerAndRejectsBothNormalRecordsAndStaleProposal`, `OriginalPayerCanLoseRightsBeforeTakeoverAndNewOwnerMustReviewBeforeFreshStart` | รับช่วงเป็นสิทธิ์สะสาง ไม่สร้าง grant/จ่ายใหม่ |
| C24 | `FieldAdvancePaymentTerminationTests.TerminationPreservesApprovalBasis`: Paid/Spendable=2,000, Approved=10,000, ฐานส่วนเพิ่ม=13,000; `UnknownMustBeReconciledAndUnpaidCancellationConfirmed`; Task15 ordered start/termination | ยุติไม่ปิดคำขอทางการเงิน ไม่ทดแทน 7D |
| C25 | `FieldAdvanceSupplementTests.Assignment_basis_does_not_grant_source_access_or_expose_source_content`; `FieldAdvancePaymentHttpTests.Task14_limited_context_has_no_source_account_installment_or_evidence_fields` | controller ตรวจ HTTPS source-link ตาม spec; ไม่ยกสิทธิ์จากลิงก์ |

## ผลและหลักฐาน Task15

ผลคำสั่งจริง เวลา exit และไฟล์ log/TRX อยู่ใน [รายงาน Task15](../../.superpowers/sdd/2026-10-24-module-7c-payments-supplements/task-15-report.md) และ `/tmp/task15-backend/` ซึ่งเป็นหลักฐานชั่วคราวที่ต้องเก็บในระบบรับมอบก่อนล้างเครื่อง

เทสต์ใหม่ที่ผ่านตั้งแต่รอบรันได้จริงถือเป็น **coverage GREEN ของพฤติกรรมเดิม** ไม่ใช่ regression RED→GREEN ของผลิตภัณฑ์ Compile/setup failures ระหว่างเขียนเทสต์แยกบันทึกไว้ ไม่ใช้แต่งเรื่องว่าพบบั๊กธุรกิจ ส่วนเทสต์ backend ไม่ต้องแก้โค้ดผลิตภัณฑ์ ต่อมาพบและแก้ข้อความผิดพลาดค้างใน CeilingAmendmentPanel โดยมีหลักฐาน RED→GREEN แยกด้านล่าง

## บันทึกผลช่วงต้นงาน (ประวัติ ไม่ใช่สถานะล่าสุด)

| รายการ | สถานะที่บันทึกขณะเริ่มรายงาน |
|---|---|
| npm baseline | controller แจ้ง 175 ผ่าน ไม่อ้างว่าผู้ทำ Task15 รันเอง |
| dev4000 identity HTTPS รอบแรก | **FAILED** 12 ผ่าน 1 ล้ม ณ initial login ของกรณีตั้ง MFA/ออกทุกอุปกรณ์; `/tmp/task15-https/4000-identity.log`; ห้ามแทนด้วยผล rerun ที่ผ่าน |
| การวินิจฉัย identity | log แสดงช่อง username ถูก disable จน timeout, public assets 200 ไม่พบ 504 ในเหตุการณ์นี้; Firefox script termination/browser-internal errors ยังไม่พิสูจน์ product cause เพิ่ม bounded sanitized diagnostics เฉพาะ helper ของ identity spec หลังแจ้ง controller แล้ว ไม่มี timeout/retry/TLS relaxation |
| scopes-boundary บน dev | controller ระบุไม่เข้าเงื่อนไขเทสต์ production-only: dev route เปิดโดยตั้งใจ ไม่อ้างเป็น passed หรือ product regression; ตรวจ4001แยก |
| HTTPS matrix dev4000/prod4001 ผ่าน4443 | controller เป็นเจ้าของ `/tmp/task15-https/results.jsonl` และจะเพิ่ม final gates พร้อม source hash ผลที่เกี่ยวข้อง |
| full G / independent whole-branch review | ยังไม่รันโดยผู้ทำ Task15; controller รับช่วงหลัง review ไม่อ้าง passed |
| มือถือจริง / NAS จริง | **พักตามผู้ใช้ / NOT-RUN** local folders/viewport ไม่ใช่หลักฐานทดแทน |

## รายการติดตามที่ไม่ปิดในงานนี้

- M2-R1: ความครอบคลุม regression สองบัญชีบุคคลเดียวที่ grant ต่างกัน ไม่ปิดเพราะเทสต์ alias ที่คล้ายกัน
- R2-C1: fixture environment normalization callsite คง Minor
- HTTPS504: เปิดติดตาม การผ่านบางรอบหรือเหตุ identity ครั้งนี้ไม่มี504 ไม่พิสูจน์ว่าแก้แล้ว
- Minor log / OpenAPI500: คงรายการเดิม ไม่รวมแก้ใน Task15
- T13-M1 / T14-M1: รหัสสถานะภาษาอังกฤษบางส่วนยัง deferred ไม่อ้างว่าทุกข้อความ UI เป็นไทยครบ
- ข้อจำกัด UI เดิม: Unknown/Matched/edit/return/re-review ทุก branch, queue pagination, account-switch ระหว่าง upload และ keyboard/screen-reader/visual audit ครบชุด ไม่เพิ่มคำอ้างจาก backend tests

## ผลผู้ตรวจอิสระ

Faraday ตรวจทั้งช่วง `71c006d..2711025` และการแก้ Task 15 รวมเทสต์ใหม่ แก้ stale alert และ diagnostics รุ่นแรก โดยแยกจากผู้ลงมือ ผลโค้ดตามขอบเขตและคุณภาพผ่าน ไม่พบ Critical/Important ใหม่ และตรวจเทียบ C01–C25 จากบริการ ข้อจำกัดฐานข้อมูล การตรวจสิทธิ์ และ assertion ของเทสต์จริงแล้ว การตรวจนี้เป็นการตรวจโค้ด ไม่ใช่ผลรันครบทุกกรณี

คง T13-M1/T14-M1 และรายการติดตามเดิม ไม่ปิดด้วยเทสต์ที่คล้ายกัน ขอบเขตที่ยังไม่ได้รับรองคือการทำงานบนมือถือ/NAS จริง การจ่ายเงินจริง การกู้คืนระบบ production การเคลียร์เงินใน 7D การ fuzz PDF และทุกสภาพแวดล้อม/ลำดับเหตุการณ์ที่เป็นไปได้ ส่วน generated metadata และเทสต์เก่าตรวจเฉพาะส่วนเกี่ยวข้อง ไม่อ้างว่าอ่านทุกบรรทัด

หลักฐานผู้ตรวจ: `/tmp/task15-independent-review.md` ผลรันรวมครบตามตารางด้านล่าง และ diagnostics รุ่น2ผ่าน scoped review แล้ว ไม่มีการอนุมัติ push/PR/merge ใน Task นี้

## ผลตรวจรับสุดท้าย

Test ทั้งชุดรอบหลังแก้และ HTTPS ที่ได้รับผลกระทบผ่านแล้ว ไม่ถือว่า HTTPS504 ถูกแก้

### การแก้ 403 ระหว่างบันทึกฉบับแก้ไข

Diagnostics ของ production จับได้ว่า owner อ่าน `payment-context` แล้วได้403ระหว่างบันทึก ทำให้ client ล้างข้อมูลและเทสต์รอจนtimeout ตัวตรวจส่งผลเดิมรวมการเปลี่ยน projection กับการถอนสิทธิ์ไว้เป็นผลเดียวกัน

สร้าง regression ด้วย PostgreSQL สอง connection คนละ PID: เตรียมผลอ่าน → บันทึก draft จริงและ commit → ส่งผลเดิม เจ้าของยังอ่านได้200แต่กลับได้403แทน409 กรณีถอน grant จริงยังได้403 ผล RED คือ1ผ่าน/1ล้ม (`owner-projection-red`) จึงแก้เฉพาะ `PaymentUiContext` ที่เปลี่ยนแค่ RequestVersion โดยเทียบทุกฟิลด์อื่นตรงทั้งหมด แล้วคืน409โดยไม่ส่ง payload เก่า ต้องผ่านการตรวจสิทธิ์และตัวตนทั้งก่อนและหลังเช่นเดิม การเปลี่ยน action, family, hold, รูปแบบ limited context หรือหมดสิทธิ์ยังคง403

GREENเฉพาะจุด10/10 ตามด้วย HTTP/Execution/Race/Delivery99/99 (`projection-focused-regression`, exit0, เริ่ม2026-10-31T03:42:27.425308Z, ใช้154.226วินาที) OpenAPIของเส้นทางนี้มี409อยู่แล้ว Faradayตรวจแก้รอบ1เป็น **ADDRESSED / Approved** ไม่พบCritical/Importantใหม่ (`/tmp/task15-runtime-fix1-review.md`) และ dev requestทั้งชุด11/11ผ่านหลังแก้เวลา03:46:22Z โดยไม่เพิ่มtimeout/retryหรือผ่อนassertions

Testรวมก่อนแก้ถูกยกเลิกหลังเห็น829กรณีผ่านเพื่อไม่ชนการแก้ไข: **ABORTED ไม่ใช่ผ่านทั้งชุด** เก็บใน `/tmp/task15-before-runtime403/` ผลรอบใหม่ใช้ชื่อ `tpr10-task15-final2-G-*` เท่านั้น

<!-- task15-final-gates:start -->
### Test / Build / Format / Lint รอบหลังแก้

| คำสั่ง | Exit | ตรวจเสร็จ UTC | source คงเดิม |
|---|---:|---|---|
| `dotnet build backend/TPR10.sln --no-restore --verbosity minimal` | 0 | 2026-10-31T03:46:13.772945+00:00 | True |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | 0 | 2026-10-31T03:46:31.675640+00:00 | True |
| `dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'` | 0 | 2026-10-31T05:14:31.763579+00:00 | True |
| `npm test` | 0 | 2026-10-31T03:49:41.362922+00:00 | True |
| `npm run build` | 0 | 2026-10-31T03:49:50.911026+00:00 | True |
| `npm run lint` | 0 | 2026-10-31T03:49:52.522315+00:00 | True |
| `git diff --check` | 0 | 2026-10-31T03:49:52.589384+00:00 | True |

SHA-256 ของ source manifest backend: `4ee118e9c4f3ad9de5768d5802340f2477b05ff45b4c083f089eecd6222c8788`

SHA-256 ของ source manifest frontend: `4ee118e9c4f3ad9de5768d5802340f2477b05ff45b4c083f089eecd6222c8788`

Logs ใช้ prefix `/tmp/tpr10-task15-final2-G-` ผลรอบก่อนแก้ที่ยกเลิกไม่ถูกรวมเป็นผ่าน Source ที่ทดสอบคือ working tree บนฐาน `2711025c38413d53b998de03f9bacbeb7c30da68` และต้องตรงกับโค้ดที่จะ commit ก่อนส่งมอบ

### HTTPS acceptance matrix

| ชุดทดสอบ | กรณีผ่าน | Exit | ตรวจเสร็จ UTC | Log |
|---|---:|---:|---|---|
| dev identity | 13 | 0 | 2026-10-31T03:02:02.928577+00:00 | `4000-identity-diagnostic.log` |
| dev scopes | 11 | 0 | 2026-10-31T03:52:15.689102+00:00 | `4000-scopes.log` |
| dev policy | 13 | 0 | 2026-10-31T02:55:23.125200+00:00 | `4000-field-advance-policy.log` |
| dev grant | 11 | 0 | 2026-10-31T02:58:28.867910+00:00 | `4000-field-advance-grant.log` |
| dev request | 11 | 0 | 2026-10-31T03:46:22.046854+00:00 | `4000-field-advance-request-fix1.log` |
| dev payment | 1 | 0 | 2026-10-31T03:47:41.291339+00:00 | `4000-field-advance-payment-fix1.log` |
| dev supplement | 1 | 0 | 2026-10-31T03:49:02.732069+00:00 | `4000-field-advance-supplement-fix1.log` |
| production identity | 13 | 0 | 2026-10-31T03:13:50.630944+00:00 | `4001-identity.log` |
| production scopes | 11 | 0 | 2026-10-31T03:14:32.509600+00:00 | `4001-scopes.log` |
| production boundary | 2 | 0 | 2026-10-31T03:14:52.025600+00:00 | `4001-scopes-boundary.log` |
| production policy | 13 | 0 | 2026-10-31T03:16:45.823475+00:00 | `4001-field-advance-policy.log` |
| production grant | 11 | 0 | 2026-10-31T03:19:44.577509+00:00 | `4001-field-advance-grant.log` |
| production request | 11 | 0 | 2026-10-31T03:57:38.739445+00:00 | `4001-field-advance-request-fix1.log` |
| production payment | 1 | 0 | 2026-10-31T03:58:39.407632+00:00 | `4001-field-advance-payment-fix1.log` |
| production supplement | 1 | 0 | 2026-10-31T03:59:46.040617+00:00 | `4001-field-advance-supplement-fix1.log` |

Logs ของ HTTPS อยู่ใน `/tmp/task15-https/` ทุกชุดใช้ TLS ที่ตรวจ CA จริงผ่าน proxy4443, dev4000 และ production4001 ไม่ใช้ ignoreHTTPSErrors หรือ retryอัตโนมัติ ชุด payment/supplement ระบุ `TPR10_PAYMENT_E2E=1` เพื่อเตรียมที่เก็บหลักฐานใน fixture ทิ้งได้

คำสั่ง HTTPS แต่ละแถวใช้ `TPR10_SMOKE_DIAGNOSTICS=1 node --input-type=module -e` เรียก `runHttpsSmoke({port: PORT, spec: "tests/e2e/NAME.spec.ts", e2e: true})` โดย PORT และ NAME ตรงกับแถว ตัวอย่างคำขอ dev:

```sh
TPR10_SMOKE_DIAGNOSTICS=1 node --input-type=module -e "import {runHttpsSmoke} from './infra/nginx/https-smoke-fixture.mjs'; await runHttpsSmoke({port:'4000',spec:'tests/e2e/field-advance-request.spec.ts',e2e:true});"
```

identity/scopes/7A ใช้ผลรอบที่ตรวจไว้ตามเวลาในตาราง โดย fix1 ไม่แก้เส้นทางเหล่านั้น ชุด request/payment/supplement ทั้งสองพอร์ตรันใหม่หลัง fix1 โดย source ไม่เปลี่ยนระหว่างรัน สำหรับ production grant ตัวตรวจรวมแจ้ง source เปลี่ยนเพียง diagnostics ของ request spec ที่ไม่ได้อยู่ในชุด grant; ตรวจ path/hash แล้วไม่มีโค้ดหรือเทสต์ grant เปลี่ยน

### ผลล้มเหลวที่เก็บไว้และขอบเขตการสรุป

- identity รอบแรก12ผ่าน/1timeout; diagnostic13ผ่านและproduction13ผ่าน แต่ยังไม่ยืนยันสาเหตุแรก ไม่ใช้ผลนี้ปิด HTTPS504
- dev scopes-boundary เป็นการเรียกผิดสภาพแวดล้อม: route เปิดในdevelopmentตามตั้งใจ ส่วนproduction boundaryตรวจแยกจริง
- คำขอพบ stale alert และ assertion เก่าที่คาดปุ่ม disabled ทั้งที่ซ่อนแล้ว แก้และทดสอบแยก ต่อมาพบ owner403ระหว่างsave ซึ่งพิสูจน์ด้วยสองconnectionและแก้ในfix1 ไม่ลบหลักฐาน timeout เดิม
- supplement503 รอบแรกไม่ได้ระบุ storage flag ชัดเจน ไม่เก็บ inherited value จึงไม่ยืนยันย้อนหลังว่า flag หาย การรันที่ระบุครบผ่าน ไม่มีการแก้ระบบจาก503นี้
- productionเริ่มไม่สำเร็จหนึ่งรอบเพราะdevเขียนทับ.nextและไม่มีBUILD_ID แก้ลำดับโดยbuildใหม่ก่อนproduction ไม่แก้โค้ดผลิตภัณฑ์
- Testรวมก่อนfix1ยกเลิกหลัง829กรณีผ่านและfocusedชุดก่อนหน้ายกเลิก ทั้งสองเป็นABORTED ไม่ใช่ผลผ่านเต็มชุด
- มือถือจริง/NASจริง: **พัก / ไม่ได้ทดสอบ** การตรวจโฟลเดอร์และฐานทิ้งได้ไม่แทนระบบจริง

**ผลตรวจรับรวม:** ผ่าน G และ HTTPS ตามตาราง ตรวจโค้ดอิสระครบทั้ง branch และรอบแก้ ไม่มี Critical/Important ค้างจากการตรวจครั้งนี้
<!-- task15-final-gates:end -->


## หลักฐานเพิ่มเติมจากผู้ทำ Task15 ก่อน final gates ของ controller

- backend coverageGREEN13/13: `/tmp/task15-backend/focused-6.log`; delivery ล่าสุด5/5: `/tmp/task15-backend/delivery-diagnostic.log`; broader regression ยกเลิก exit1 ไม่มีsummary: `/tmp/task15-backend/regression.log` ไม่รันซ้ำชน fullG ของ controller
- UI fix ที่อนุมัติล้าง transient read error เฉพาะ consistent read สำเร็จและไม่มีpending command: RED `/tmp/task15-implementation-https/request-stale-alert-red.log` → regression ใหม่ผ่าน1.7วินาทีใน `/tmp/task15-implementation-https/request-dev-green.log` แต่ suite รวม10ผ่าน/1timeout ไม่ใช่ผ่านทั้งหมด Metadata command/exit/เวลา/hash อยู่ในไฟล์ .json ชื่อเดียวกัน
- timeout กรณี “ส่งกลับ แก้ฉบับ ยืนยันใหม่ และถอนด้วยเหตุผล” ยังไม่ทราบ root cause; snapshot `/tmp/task15-implementation-https/request-dev-failure-context.md` มี protected-data invalidation ขณะที่ stack อยู่ในcleanup ไม่พิสูจน์สาเหตุ Firefox เพิ่มเฉพาะ bounded sanitized diagnostics ตาม ruling ล่าสุด ไม่แก้ timeout/retry/assertions/product; controller จะรันครั้งเดียวเมื่อพร้อม
- supplement upload503: `/tmp/task15-implementation-https/supplement-dev.log` และ .json; runner ไม่ตั้ง explicit TPR10_PAYMENT_E2E แต่ไม่บันทึก inherited flag จึงไม่อ้างยืนยัน environmentย้อนหลัง Source fixture ต้องใช้ flag1 สร้าง storage config ตรงกับประวัติ Task14ที่ controller แจ้ง Controller จะรัน corrected invocation ไม่ใช่ product fix
- dev identity diagnostic13/13ผ่านตามcontroller `/tmp/task15-https/4000-identity-diagnostic.log` คงFAILแรกด้วย ไม่อ้าง rootcauseหรือfix

ส่วนนี้ไม่แทน final gates ของ controller และไม่มีการรันทดสอบใหม่จากผู้ทำ Task15 ระหว่าง full backendG/independent review


## คำสั่ง focused และหลักฐานที่เก็บในรายงาน tracked

ทำงานจาก checkout `/Users/theerapat_k/.codex/worktrees/module-7a-policy-permissions/TPR10` เวลาต่อไปนี้เป็น wall-clock ของคำสั่งจาก metadata ไม่ใช่เฉพาะเวลาที่ test runner รายงาน ไม่มีการรันซ้ำเพื่อจัดทำตารางนี้

### focused-1

compile failure ไม่มีผลทดสอบ; exit 1; เริ่ม UTC `2026-10-31T02:50:54.181844+00:00`; wall-clock 3.809 วินาที

```sh
dotnet test backend/tests/TPR10.Api.IntegrationTests --filter 'FullyQualifiedName~FieldAdvancePaymentRaceTests|FullyQualifiedName~FieldAdvancePaymentDeliveryTests' --logger 'trx;LogFileName=focused-1.trx' --results-directory /tmp/task15-backend
```

log `/tmp/task15-backend/focused-1.log`; metadata `/tmp/task15-backend/focused-1.json`

### focused-2

ผ่าน9 ล้ม0; exit 0; เริ่ม UTC `2026-10-31T02:51:15.871877+00:00`; wall-clock 25.093 วินาที

```sh
dotnet test backend/tests/TPR10.Api.IntegrationTests --filter 'FullyQualifiedName~FieldAdvancePaymentRaceTests|FullyQualifiedName~FieldAdvancePaymentDeliveryTests' --logger 'trx;LogFileName=focused-2.trx' --results-directory /tmp/task15-backend
```

log `/tmp/task15-backend/focused-2.log`; metadata `/tmp/task15-backend/focused-2.json`

### focused-3

ผ่าน11 ล้ม2 (fixture setup); exit 1; เริ่ม UTC `2026-10-31T02:52:08.356779+00:00`; wall-clock 45.363 วินาที

```sh
dotnet test backend/tests/TPR10.Api.IntegrationTests --filter 'FullyQualifiedName~FieldAdvancePaymentRaceTests|FullyQualifiedName~FieldAdvancePaymentDeliveryTests' --logger 'trx;LogFileName=focused-3.trx' --results-directory /tmp/task15-backend
```

log `/tmp/task15-backend/focused-3.log`; metadata `/tmp/task15-backend/focused-3.json`

### focused-4

ผ่าน12 ล้ม1 (fixture setup); exit 1; เริ่ม UTC `2026-10-31T02:53:39.795402+00:00`; wall-clock 43.470 วินาที

```sh
dotnet test backend/tests/TPR10.Api.IntegrationTests --filter 'FullyQualifiedName~FieldAdvancePaymentRaceTests|FullyQualifiedName~FieldAdvancePaymentDeliveryTests' --logger 'trx;LogFileName=focused-4.trx' --results-directory /tmp/task15-backend
```

log `/tmp/task15-backend/focused-4.log`; metadata `/tmp/task15-backend/focused-4.json`

### focused-5

ผ่าน11 ล้ม2 (fixture setup); exit 1; เริ่ม UTC `2026-10-31T02:55:45.179264+00:00`; wall-clock 27.685 วินาที

```sh
dotnet test backend/tests/TPR10.Api.IntegrationTests --filter 'FullyQualifiedName~FieldAdvancePaymentRaceTests|FullyQualifiedName~FieldAdvancePaymentDeliveryTests' --logger 'trx;LogFileName=focused-5.trx' --results-directory /tmp/task15-backend
```

log `/tmp/task15-backend/focused-5.log`; metadata `/tmp/task15-backend/focused-5.json`

### focused-6

ผ่าน13 ล้ม0; exit 0; เริ่ม UTC `2026-10-31T02:58:06.295065+00:00`; wall-clock 28.483 วินาที

```sh
dotnet test backend/tests/TPR10.Api.IntegrationTests --filter 'FullyQualifiedName~FieldAdvancePaymentRaceTests|FullyQualifiedName~FieldAdvancePaymentDeliveryTests' --logger 'trx;LogFileName=focused-6.trx' --results-directory /tmp/task15-backend
```

log `/tmp/task15-backend/focused-6.log`; metadata `/tmp/task15-backend/focused-6.json`

### delivery-diagnostic

ผ่าน5 ล้ม0; exit 0; เริ่ม UTC `2026-10-31T03:09:43.137966+00:00`; wall-clock 17.094 วินาที

```sh
dotnet test backend/tests/TPR10.Api.IntegrationTests --filter 'FullyQualifiedName~FieldAdvancePaymentDeliveryTests' --logger 'trx;LogFileName=delivery-diagnostic.trx' --results-directory /tmp/task15-backend
```

log `/tmp/task15-backend/delivery-diagnostic.log`; metadata `/tmp/task15-backend/delivery-diagnostic.json`

### regression

ยกเลิก ไม่มีsummary/countที่ยืนยันได้ ไม่ถือว่าผ่าน; exit 1; เริ่ม UTC `2026-10-31T03:01:24.905439+00:00`; wall-clock 559.927 วินาที

```sh
dotnet test backend/tests/TPR10.Api.IntegrationTests --filter 'FullyQualifiedName~FieldAdvancePayment|FullyQualifiedName~FieldAdvanceInstallment|FullyQualifiedName~FieldAdvanceEvidence|FullyQualifiedName~FieldAdvanceSupplement|FullyQualifiedName~FieldAdvanceCeiling|FullyQualifiedName~FieldAdvanceDeliveryTests|FullyQualifiedName~FieldAdvanceRequestDeliveryTests' --logger 'trx;LogFileName=regression.trx' --results-directory /tmp/task15-backend
```

log `/tmp/task15-backend/regression.log`; metadata `/tmp/task15-backend/regression.json`

### request-stale-alert-red

RED assertion stale alert; exit 1; เริ่ม UTC `2026-10-31T03:06:18.176606+00:00`; wall-clock 26.084 วินาที

```sh
TPR10_SMOKE_DIAGNOSTICS=1 TPR10_E2E_GREP=Task15 node --input-type=module -e 'import { runHttpsSmoke } from '"'"'./infra/nginx/https-smoke-fixture.mjs'"'"'; await runHttpsSmoke({port:'"'"'4000'"'"',spec:"tests/e2e/field-advance-request.spec.ts",e2e:true});'
```

log `/tmp/task15-implementation-https/request-stale-alert-red.log`; metadata `/tmp/task15-implementation-https/request-stale-alert-red.json`; SHA256 ก่อน `6af4d57c48f79b6a5f062f2ddcf1e9d2d36c9170fe4e1771b0e5cc897946775f` หลัง `6af4d57c48f79b6a5f062f2ddcf1e9d2d36c9170fe4e1771b0e5cc897946775f`

### request-dev-green

regressionใหม่ผ่าน แต่ suite10ผ่าน/1timeout; exit 1; เริ่ม UTC `2026-10-31T03:07:08.380736+00:00`; wall-clock 206.020 วินาที

```sh
TPR10_SMOKE_DIAGNOSTICS=1 node --input-type=module -e 'import { runHttpsSmoke } from '"'"'./infra/nginx/https-smoke-fixture.mjs'"'"'; await runHttpsSmoke({port:'"'"'4000'"'"',spec:"tests/e2e/field-advance-request.spec.ts",e2e:true});'
```

log `/tmp/task15-implementation-https/request-dev-green.log`; metadata `/tmp/task15-implementation-https/request-dev-green.json`; SHA256 ก่อน `6c04f762b4ca74fbda538a9fa0161b61115387b1710225de662979aabfe2f800` หลัง `6c04f762b4ca74fbda538a9fa0161b61115387b1710225de662979aabfe2f800`

### supplement-dev

ล้ม1 ที่upload503 คาด201; exit 1; เริ่ม UTC `2026-10-31T03:11:03.464685+00:00`; wall-clock 36.666 วินาที

```sh
TPR10_SMOKE_DIAGNOSTICS=1 node --input-type=module -e 'import { runHttpsSmoke } from '"'"'./infra/nginx/https-smoke-fixture.mjs'"'"'; await runHttpsSmoke({port:'"'"'4000'"'"',spec:"tests/e2e/field-advance-supplement.spec.ts",e2e:true});'
```

log `/tmp/task15-implementation-https/supplement-dev.log`; metadata `/tmp/task15-implementation-https/supplement-dev.json`; SHA256 ก่อน `6c04f762b4ca74fbda538a9fa0161b61115387b1710225de662979aabfe2f800` หลัง `6c04f762b4ca74fbda538a9fa0161b61115387b1710225de662979aabfe2f800`

Faraday รับผิดชอบ independent whole-branch audit; การ self-review ของผู้ทำ Task15 ไม่แทน audit นี้ Controller จะเพิ่ม finalG/matrix/review gates ภายหลัง หลักฐานและคำสั่งด้านบนอยู่ในรายงาน tracked นี้โดยไม่ต้องพึ่งไฟล์ ignored .superpowers เพียงแห่งเดียว


## ประวัติการวินิจฉัยก่อนพิสูจน์ race 403

ส่วนนี้เป็นข้อสรุปในเวลานั้น ก่อน diagnostics รุ่นถัดไปจับ403และก่อน regression พิสูจน์สาเหตุ ผลปัจจุบันอยู่ในหัวข้อการแก้403และตารางตรวจรับด้านบน

Controller รัน session58242; log `/tmp/task15-https/4000-request-timeout-diagnostic.log` ระบุ1ผ่านใน27.5วินาที (ตัวกรณี26.5วินาที) สำหรับ “ส่งกลับ แก้ฉบับ ยืนยันใหม่ และถอนด้วยเหตุผล” เดินครบ23 workflow phases แล้วcleanup `poll-edited-draft` เริ่ม8206ms และ phaseถัดไป `description-preserved` ที่23531ms ต่าง15325ms จึงไม่ใช่การค้างในรอบนี้

คง timeout120000ms เดิมใน `/tmp/task15-implementation-https/request-dev-green.log` และผล10ผ่าน/1timeoutไว้ ผลล่าสุดเป็นไม่เกิดซ้ำในการวินิจฉัยหนึ่งครั้ง สาเหตุยังไม่ยืนยัน ไม่มี product fix หรือการประกาศว่าแก้timeoutแล้ว และไม่แทนผลfull request suite

ข้อจำกัด diagnostics: API events ครบเพดาน120รายการที่5381ms ใน reviewer-navigation ก่อน poll-edited-draft จึงไม่มีAPI statusในช่วงรอ15.325วินาที `dwell()` มีการอ่านrequest-id, clock.fastForward, รอscreen200 และassertไม่มีalert โดยไม่มีsubphase markers จึงยังแยก clock advance/response wait/assertion ไม่ได้จากหลักฐานนี้ ไม่อนุมานสาเหตุจากเวลาใกล้15วินาที ไม่มีการแก้codeหรือรันdiagnosticเพิ่ม

Controller จะรัน full request dev suite และ fresh-build production อีกสามspecตามคิวของตน ผลยังไม่รวมเป็นpassedในส่วนนี้ ผู้ทำ Task15 ไม่รันbuild/test/HTTPSซ้ำ Faraday รายงาน independent code audit ไม่มีCritical/Importantตามcontroller; ข้อสรุปนี้ไม่ทดแทน runtime gates ที่ยังดำเนินอยู่


## ข้อจำกัดการเก็บ log ของตัวจัดคิวทดสอบ

ตัวจัดคิวชั่วคราวที่คัดลอกจาก dev ไป production เปลี่ยนตัวแบ่งข้อความผิด จึงรัน dev identity/scopes ซ้ำและไปหยุดที่ boundary ซึ่งเป็นเทสต์production-only การรันส่วนเกินไม่ได้แก้โค้ดผลิตภัณฑ์ แต่ใช้ชื่อlogเดิม ทำให้ rawlog ของ identityครั้งแรกที่ล้มถูกเขียนทับด้วยรอบผ่าน Metadataแรกใน `results.jsonl` (exit1 เวลา02:50:29UTC) และข้อสังเกตที่ผู้ลงมือ/ผู้ตรวจอ่านไว้ก่อนหน้ายังคงอยู่ จึงไม่อ้างว่าrawlogแรกยังครบ แก้ตัวจัดคิวและให้ชื่อlogซ้ำแยกไฟล์แล้ว ก่อนproductionต้องbuildใหม่หลังdevเสมอ


## ขอบเขตไฟล์ส่งมอบ

บันทึกเฉพาะ11ไฟล์ของTask15ตามแผน เก็บงานเดิม14pathsไว้ รวม capture hooks เดิมใน request spec ซึ่งแยกออกจากcommitด้วยthree-way patch การทดสอบทำบนworking treeที่มีงานเดิมนี้อยู่ แต่ไม่ได้เปิดโหมดcapture ตรวจhashโค้ดที่เกี่ยวข้องและไฟล์เดิมก่อนcommit ไม่มีpush/PR/mergeหรือmigrationฐานจริงในงานนี้
