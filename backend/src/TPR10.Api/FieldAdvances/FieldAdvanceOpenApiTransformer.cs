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
        operation.Description = "กติกาและสิทธิ์การเงิน: ตรวจ session, MFA, บุคคล และ capability ปัจจุบันใน WorkspaceId/ProjectId ที่ตรงกันจาก server ทุกครั้ง รวมส่งคำสั่งซ้ำ; null project ใช้เฉพาะระดับบริษัท ไม่มี admin bypass. no-store. ผู้ร่วมแก้สายกติกาเดียวกันห้ามตรวจ แต่ผู้แก้บริษัทฐานไม่ถูกห้ามอัตโนมัติ. meta.policy ระบุ mode/source/base/expectedActiveRevisionId/expectedActiveVersion และ expectedContentHash สำหรับแก้/ส่ง/ตรวจ คู่ active=null/0 หมายถึงยังไม่มีฉบับเปิดใช้; create expectedVersion=1. review ผูก hash/version ที่เห็นและตรวจ active เดิม; inherit ตรวจบริษัทฐานล่าสุด หากเปลี่ยนให้409 โหลดใหม่และส่งตรวจอีกครั้ง. grants propose/review คืน proposal ID; revoke รับ assignment ID. ไม่เปิด resolver/read API ใน Task6.";
        return Task.CompletedTask;
    }
}
