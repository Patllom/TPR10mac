using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace TPR10.Api.Attendance.Corrections;

internal static class CorrectionOpenApi
{
    internal static async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        var path = context.Description.RelativePath ?? "";
        var post = context.Description.HttpMethod == "POST";
        operation.Description += "\nWorkflowRouting: เฉพาะคำร้อง pending ในหน่วยงานที่เป็น HR ปัจจุบัน พร้อม directory-manage และ recent MFA; จัดเส้นทางใหม่เริ่มหัวหน้า ไม่เพิ่มสิทธิ์ภาพ/GPS/ประวัติทั่วไป. GET sites รับ at เวลา ISO8601 พร้อมเขตเวลา คืนเฉพาะ Site ที่เจ้าของเคยได้รับมอบหมายในเวลานั้นและมี membership ประวัติ; ต้องยังมี CanRecord ปัจจุบัน ไม่รับ employeeId. ตัวเลือกนี้ไม่ใช่สิทธิ์ลงเวลาปัจจุบัน. HR ปฏิเสธเปิดรอบที่มี CheckOut สดแล้ว แต่แก้ค่าเวลาออกได้.";
        operation.Description += "\nรายละเอียดส่ง comparison เฉพาะวัน/เวลา/Site/version ของรายการที่คำร้องอ้างอิง ภายใต้สิทธิ์อ่านคำร้องปัจจุบัน; list ไม่ส่ง comparison. ผู้มีสิทธิ์ workflow อ่านได้ระหว่างต้องตัดสินเท่านั้น ไม่เพิ่มสิทธิ์ประวัติทั่วไป ภาพ หรือ GPS. original คือเวลาจากเหตุการณ์เดิม current คือ effective ปัจจุบัน baselineMatches ใช้เทียบรุ่น ห้ามอ้าง current ว่าเป็น baseline เมื่อรุ่นไม่ตรง; วันจากคำร้องที่อนุมัติแล้วอ้าง adjustment โดยไม่สร้างเหตุการณ์ปลอม.";
        operation.Description += "\nคำร้องแก้ไขเวลา: API ตรวจ session/current grants และ MFA สำหรับหัวหน้า/HR; Admin ไม่มี bypass. readBasis ระบุ Own/Supervisor/Hr/WorkflowSupervisor/WorkflowHr; ไม่มีภาพ GPS หรือ storage locator ในคำร้อง; list ไม่ส่ง history, detail ส่งประวัติตามสิทธิ์. state เป็นเลข 0 Draft, 1 PendingSupervisor, 2 PendingHr, 3 Returned, 4 Approved, 5 Expired. mutation ตอบเฉพาะ id และ commandId ไม่ส่ง proposal/history และไม่เพิ่มสิทธิ์อ่านหลังอนุมัติ ให้แสดงผลสำเร็จแล้วกลับคิว. identity delivery lock จำกัด5วินาที ตรวจ current authorization ก่อนส่ง byte แรก. ใช้ commandId เดิมและ payload เดิมเมื่อผู้ใช้ส่งซ้ำโดยชัดแจ้ง ตรวจ GET/revision ก่อน retry; ห้าม retry อัตโนมัติ. หากสิทธิ์อ่านสิ้นสุดให้กลับคิว ห้ามสรุปว่า rollback จาก404/503. 503 ไม่ logout. no-store.";
        if (post)
        {
            var command = Object(new() { ["commandId"] = Uuid(), ["expectedVersion"] = Integer(1), ["revision"] = Integer(1), ["reason"] = Text(null, true) });
            var site = Object(new() { ["workspaceId"] = Uuid(), ["projectId"] = Uuid(), ["siteId"] = Uuid() });
            var proposal = Object(new()
            {
                ["workDate"] = Text("date"),
                ["inUtc"] = Text("date-time"),
                ["outUtc"] = Text("date-time", true),
                ["inSite"] = site,
                ["outSite"] = new OpenApiSchema { AnyOf = [site, new OpenApiSchema { Type = JsonSchemaType.Null }] },
                ["reason"] = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 2000 }
            });
            var input = Object(new()
            {
                ["dayId"] = Text("uuid", true),
                ["originalWorkDate"] = Text("date"),
                ["expectedDayVersion"] = new OpenApiSchema { Type = JsonSchemaType.Integer | JsonSchemaType.Null, Format = "int64", Minimum = "1" },
                ["proposal"] = proposal
            });
            var create = path.EndsWith("/corrections", StringComparison.Ordinal);
            var schema = create ? Object(new() { ["commandId"] = Uuid(), ["input"] = input })
                : path.EndsWith("/revisions", StringComparison.Ordinal) ? Object(new() { ["input"] = input, ["command"] = command }) : command;
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Description = "JSON UTF-8 สูงสุด64KiB; ทุกfieldต้องมี แม้ค่าที่nullableต้องส่งnull; ปฏิเสธfieldไม่รู้จัก/ซ้ำทุกระดับ ไม่รับactorIdหรือmultipart. reason บังคับreturn/reroute1–2000อักขระ",
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = schema } }
            };
        }
        else if (path.EndsWith("/corrections", StringComparison.Ordinal) || path.EndsWith("/approvals", StringComparison.Ordinal) || path.EndsWith("/sites", StringComparison.Ordinal))
        {
            operation.Parameters ??= [];
            foreach (var name in new[] { "page", "pageSize" }) operation.Parameters.Add(new OpenApiParameter
            {
                Name = name,
                In = ParameterLocation.Query,
                Schema = Integer(1),
                Description = name == "page" ? "ตั้งต้น1" : "ตั้งต้น20 สูงสุด100 เกินขอบเขต400"
            });
            if (path.EndsWith("/approvals", StringComparison.Ordinal)) operation.Parameters.Add(new OpenApiParameter
            {
                Name = "stage",
                In = ParameterLocation.Query,
                Required = true,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("supervisor"), JsonValue.Create("hr"), JsonValue.Create("routing")] },
                Description = "ขั้นที่รอตัดสินตาม current route/capability และ recent MFA; จำกัดขอบเขตก่อนนับและแบ่งหน้า"
            });
            if (path.EndsWith("/sites", StringComparison.Ordinal)) operation.Parameters.Add(new OpenApiParameter
            {
                Name = "at",
                In = ParameterLocation.Query,
                Required = true,
                Schema = Text("date-time"),
                Description = "เวลาเข้า/ออกที่เสนอ ต้องมี timezone และไม่เป็นอนาคต; pageSize ตั้งต้น100 สูงสุด100; เฉพาะเจ้าของบัญชี"
            });
        }
        var problem = await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), cancellationToken: ct);
        foreach (var status in new[] { "400", "401", "403", "404", "409", "413", "415", "429", "500", "503" })
            operation.Responses![status] = new OpenApiResponse
            {
                Description = $"ข้อผิดพลาด {status}; ตรวจ Content-Type ก่อน parse; ไม่ retry mutation อัตโนมัติ",
                Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = problem } }
            };
        ((OpenApiResponse)operation.Responses!["429"]).Headers = new Dictionary<string, IOpenApiHeader>
        { ["Retry-After"] = new OpenApiHeader { Description = "วินาทีเมื่อ limiter ระบุค่า", Schema = Text() } };
        operation.Responses["default"] = new OpenApiResponse { Description = "transport อาจไม่มีbody; เก็บ commandId เดิมแล้วตรวจสถานะ ห้ามส่งซ้ำอัตโนมัติ" };
    }
    private static OpenApiSchema Object(Dictionary<string, IOpenApiSchema> properties) => new()
    { Type = JsonSchemaType.Object, Properties = properties, Required = properties.Keys.ToHashSet(), AdditionalPropertiesAllowed = false };
    private static OpenApiSchema Text(string? format = null, bool nullable = false) => new()
    { Type = JsonSchemaType.String | (nullable ? JsonSchemaType.Null : 0), Format = format };
    private static OpenApiSchema Uuid() => Text("uuid");
    private static OpenApiSchema Integer(int minimum) => new() { Type = JsonSchemaType.Integer, Minimum = minimum.ToString(System.Globalization.CultureInfo.InvariantCulture) };
}
