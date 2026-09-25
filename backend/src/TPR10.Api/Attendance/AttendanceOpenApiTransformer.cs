using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TPR10.Api.Attendance.Directory;

namespace TPR10.Api.Attendance;

// Explicitly delegated by IdentityOpenApiTransformer; documentation never authorizes a request.
public sealed class AttendanceOpenApiTransformer
{
    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, DirectoryEndpointMetadata metadata, CancellationToken ct)
    {
        operation.Extensions!["x-tpr10-scope"] = new JsonNodeExtension(JsonValue.Create(metadata.Domain));
        operation.Extensions["x-tpr10-permissions"] = new JsonNodeExtension(new JsonArray(metadata.Capability == "" ? [] : [(JsonNode?)JsonValue.Create(metadata.Capability)]));
        operation.Extensions["x-tpr10-mfa-required"] = new JsonNodeExtension(JsonValue.Create(metadata.RequireMfa));
        operation.Description += metadata.Domain == "attendance-access"
            ? "\nBoolean hints สำหรับแสดงเมนูเท่านั้น ไม่ใช่ authority ไม่คืนข้อมูลลูกทีม รูปหรือพิกัด; API ปลายทางต้องตรวจสิทธิ์ปัจจุบันอีกครั้ง."
            : "\nControl plane จัดการต้นสังกัด สายบังคับบัญชาและ HR เท่านั้น ไม่ให้สิทธิ์อ่านรูป/GPS หรือ Site โดยอัตโนมัติ. ต้อง permission+recent MFA; ห้ามเพิ่มอำนาจตนเอง. effective-now และประวัติ read-only; เปลี่ยนจริง revoke session ผู้ได้รับผลพร้อม audit ใน transaction. expectedVersion ใช้ค่าจาก server ห้ามสมมติว่าเริ่ม1หลัง replace; ค่าเก่าตอบ409 โหลดใหม่ก่อนส่งด้วยตนเอง. เหตุผล1–500 Unicode scalars ไม่มี control characters.";
        if (operation.Parameters?.Any(p => p.Name == "pageSize") == true)
            operation.Extensions["x-tpr10-pagination"] = new JsonNodeExtension(new JsonObject { ["pageMinimum"] = 1, ["pageSizeDefault"] = 25, ["pageSizeMaximum"] = 100, ["overMaximum"] = "clamp" });
        var problem = await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), cancellationToken: ct);
        foreach (var status in new[] { "400", "401", "403", "404", "409", "500", "503" })
            if (!operation.Responses!.ContainsKey(status)) operation.Responses[status] = new OpenApiResponse
            {
                Description = $"ข้อผิดพลาด {status}; body อาจว่างหรือไม่ใช่ JSON ตรวจ Content-Type ก่อน parse",
                Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = problem } }
            };
        operation.Responses!["default"] = new OpenApiResponse { Description = "ข้อผิดพลาดอื่น อาจไม่มี body; ห้าม retry mutation อัตโนมัติหรือสรุปว่า rollback จาก status เท่านั้น" };
    }
}
