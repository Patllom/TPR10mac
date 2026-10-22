using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace TPR10.Api.FieldAdvances;

public sealed class FieldAdvanceOpenApiTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.RelativePath?.StartsWith("api/v1/field-advances/", StringComparison.Ordinal) != true)
            return Task.CompletedTask;
        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        operation.Extensions["x-tpr10-mfa-required"] = new JsonNodeExtension(JsonValue.Create(true));
        operation.Extensions["x-tpr10-scope"] = new JsonNodeExtension(JsonValue.Create("exact-workspace-project"));
        operation.Description = "กติกาและสิทธิ์การเงิน: ตรวจ session, MFA, บุคคล และ capability ปัจจุบันใน WorkspaceId/ProjectId ที่ตรงกันจาก server ทุกครั้ง รวมส่งคำสั่งซ้ำ; null project ใช้เฉพาะระดับบริษัท ไม่มี admin bypass. no-store. ผู้ร่วมแก้สายกติกาเดียวกันห้ามตรวจ แต่ผู้แก้บริษัทฐานไม่ถูกห้ามอัตโนมัติ. meta.policy ระบุ mode/source/base/expectedActiveRevisionId/expectedActiveVersion และ expectedContentHash สำหรับแก้/ส่ง/ตรวจ คู่ active=null/0 หมายถึงยังไม่มีฉบับเปิดใช้; create expectedVersion=1. review ผูก hash/version ที่เห็นและตรวจ active เดิม; inherit ตรวจบริษัทฐานล่าสุด หากเปลี่ยนให้409 โหลดใหม่และส่งตรวจอีกครั้ง. grants propose/review คืน proposal ID; revoke รับ assignment ID. อ่านกติกาด้วย policy.edit.company ระดับบริษัท หรือ policy.edit.project ตรงโครงการ หรือ policy.review ตรง scope; review-queue เฉพาะ policy.review. สิทธิ์โครงการอ่านบริษัทฐานได้เฉพาะ effective policy ไม่เปิด company catalog/history. asOf ไม่ย้อนสิทธิ์ อนาคต400 หลักฐานเปิดใช้ไม่ครบ409. activeRevisionId/activeVersion เป็น pointer ปัจจุบันแยกจาก expectedActive*. page เริ่ม1, pageSize 1–100; กรอง scope ก่อนแบ่งหน้า. preview คืนระดับอนุมัติ ไม่มี business write และ POST ต้องมี CSRF.";
        if (context.Description.RelativePath?.StartsWith("api/v1/field-advances/policies", StringComparison.Ordinal) == true)
        {
            operation.Description = operation.Description.Replace("สิทธิ์โครงการอ่านบริษัทฐานได้เฉพาะ effective policy ไม่เปิด company catalog/history.",
                "สิทธิ์โครงการอ่านบริษัทฐานและฉบับล่าสุดผ่าน company-context ได้เฉพาะ id/content/contentHash; policyId ต้องเป็นฉบับใน exact project ที่มีสิทธิ์ ไม่รับ arbitrary company revision lookup และไม่เปิด company catalog/history หรือ metadata อื่น.");
            operation.Description += " scopes คืนเฉพาะชื่อ workspace/project และ policy capabilities ที่บัญชีปัจจุบันผ่านตรวจ exact scope ก่อนแบ่งหน้า ไม่มี grant catalog หรือสิทธิ์อัตโนมัติ. policy HTTP money ขาออกเป็น decimal string บาทและสตางค์; frontend ส่ง string, ขารับรองรับ legacy JSON number โดยแปลงตรงเป็น .NET decimal. domain hash/evidence/receipt ไม่เปลี่ยน.";
        }
        else
            operation.Description = "สิทธิ์การเงิน: session/MFA ปัจจุบัน exact company/project และ no-store ทุกครั้ง. scopes ต้องมี grant.propose/review/revoke; list/details/history ต้องมีอำนาจมอบต่อครอบคลุมทุก capability/delegable capability และวงเงินเป้าหมาย ไม่รวมวงเงินหลายสิทธิ์ ไม่ตรวจช่วงเวลาเป้าหมายสำหรับอ่าน. review-queue ต้อง grant.review. recipients ต้อง grant.propose ตรง scope คืนเฉพาะบัญชี/ชื่อพนักงานบริษัทที่ active และ finance identity ชัดเจน ไม่ต้องเป็นสมาชิกโครงการเดิม ไม่คืนบุคคลเดียวกับผู้เสนอรวม aliases. page 1 ขึ้นไป pageSize 1–100; กรองสิทธิ์ก่อนแบ่งหน้า. ผู้เสนอ/ผู้ตรวจ/ผู้รับต่างบุคคล และตรวจอำนาจมอบต่อพร้อมเวลาเมื่ออนุมัติ. approvalLimit HTTP เป็น decimal string หรือ null (ไม่มีอำนาจอนุมัติยอดเงิน); รับ legacy number ผ่าน decimal, hash/receipt เดิมไม่เปลี่ยน. propose/review คืน proposal id/version; revoke รับ assignmentId/assignmentVersion จากรายละเอียด. ส่งกลับ/ต่ออายุ/เพิ่มอำนาจสร้างข้อเสนอใหม่ผ่านตรวจใหม่ สิทธิ์เดิมยังอยู่จนถอนหรือหมดอายุ. ไม่มี suspend/resume. ทุก POST ต้อง CSRF และ reason; ไม่มี autoapprove/admin bypass. ประวัติไม่มีข้อมูลยอดคำขอหรือหลักฐานการเงินจริง.";
        return Task.CompletedTask;
    }
}
