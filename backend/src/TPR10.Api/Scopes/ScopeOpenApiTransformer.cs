using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TPR10.Api.Scopes.Probes;

namespace TPR10.Api.Scopes;

// Called explicitly by the identity transformer, not registered as a competing operation transformer.
public sealed class ScopeOpenApiTransformer
{
    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        ScopeEndpointMetadata scope, CancellationToken ct)
    {
        operation.Extensions!["x-tpr10-scope"] = new JsonNodeExtension(JsonValue.Create(scope.Mode));
        operation.Extensions["x-tpr10-scope-level"] = new JsonNodeExtension(JsonValue.Create(scope.Level));
        var business = scope.Mode == "exact-business";
        var export = scope.Capability == "scope-probe:export";
        var write = scope.Capability == "scope-probe:write";
        operation.Description += scope.Mode switch
        {
            "scope-discovery" => "\nคืนเฉพาะ exact tuples ที่มอบหมายและ active พร้อม breadcrumb ขั้นต่ำและ business capabilities ของแต่ละ tuple; ไม่ให้สิทธิ์ parent/sibling ไม่ส่งข้อมูลธุรกิจ ไม่รวมสิทธิ์ลง session; audit ก่อนส่งผล.",
            "system-management" when scope.Capability == "organization:manage" => "\nControl plane จัดการโครงสร้าง ไม่ให้สิทธิ์อ่านข้อมูลธุรกิจ; ตรวจ ancestry/version ใน transaction. Deactivate ถอน assignment/session ที่ได้รับผล; reactivate ไม่คืนสิทธิ์เดิม; Department เป็น metadata ไม่ใช่ security scope.",
            "system-management" => "\nControl plane มอบหมายบทบาทธุรกิจให้ผู้อื่นตาม exact scope; ห้าม grant/replace/revoke ของตนเองและห้าม system-administration role. ไม่มี business bypass; replace เปลี่ยนจริงจะ revoke เดิมและสร้างใหม่พร้อม invalidate sessions ใน transaction; no-op ไม่ invalidate. Options คืนข้อมูลขั้นต่ำ ไม่ต้อง users:manage.",
            _ => "\nตรวจ exact workspace/project/site รวม null จาก assignment ปัจจุบันเท่านั้น ไม่มี parent inheritance/global-admin bypass; scope ที่ไม่มีอยู่ ไม่ active หรือไม่ถูกมอบหมายใช้404แบบเดียวกัน. อ่านและ audit ภายใน transaction ก่อนส่ง DTO; technical endpoints เฉพาะ Development/Testing ไม่เปิดใน Production."
        };
        if (operation.Parameters?.Any(p => p.Name == "pageSize") == true)
        {
            operation.Extensions["x-tpr10-pagination"] = new JsonNodeExtension(new JsonObject
            { ["pageMinimum"] = 1, ["pageSizeDefault"] = 25, ["pageSizeMaximum"] = 100, ["overMaximum"] = "clamp" });
            operation.Description += " page เริ่ม1 pageSize เริ่ม25 สูงสุด100 (ค่าที่มากกว่าลดเป็น100); ค่าน้อยกว่า1/offset overflowตอบ400; count และลำดับคงที่ภายในขอบเขตคำขอ.";
        }
        if (business)
        {
            operation.Extensions["x-tpr10-field-policy"] = new JsonNodeExtension(new JsonObject
            {
                ["restrictedNote"] = new JsonObject
                {
                    ["permission"] = "scope-probe:restricted-read",
                    ["recentMfa"] = true,
                    ["withoutPermission"] = "omit",
                    ["writeWhenPresentIncludingNull"] = write
                }
            });
            operation.Description += export
                ? "\nExport-simulation ส่ง JSON ไม่สร้างไฟล์; exact scope+export+recent MFA ไม่ต้องมี read; restricted field ต้อง restricted-read ด้วย. จำกัด100รายการ หากเกินตอบ400ไม่truncate ให้ลดช่วง UTC createdFrom รวมขอบต้น / createdTo ไม่รวมขอบท้าย; from>=to หรือ offset ไม่เป็น0ตอบ400; อนาคตใช้ได้และอาจว่าง. Audit filters/row-count/destination-type ก่อนส่งผล."
                : "\nRead/list ส่ง restrictedNote เฉพาะ restricted-read+recent MFA; write-only คืน id/version+Location; POST/PATCH ส่ง restrictedNote รวม null ต้อง write+restricted-read+recent MFA, absent คงเดิมเมื่อ PATCH, null ล้าง, string แทนค่า; note ไม่เกิน500ไม่มี control. ไม่มี/ถูกrevoke session401, ไม่มีcapability/MFA403, scope/recordผิดtuple404, versionเก่า409.";
            var list = !write && !export && !context.Description.RelativePath!.EndsWith("/{id}", StringComparison.Ordinal);
            var variants = new List<IOpenApiSchema>
            {
                await context.GetOrCreateSchemaAsync(export ? typeof(ExportRecordPage<PublicRecordView>) : list ? typeof(Page<PublicRecordView>) : typeof(PublicRecordView), cancellationToken: ct),
                await context.GetOrCreateSchemaAsync(export ? typeof(ExportRecordPage<RestrictedRecordView>) : list ? typeof(Page<RestrictedRecordView>) : typeof(RestrictedRecordView), cancellationToken: ct)
            };
            if (write) variants.Add(await context.GetOrCreateSchemaAsync(typeof(WrittenRecordView), cancellationToken: ct));
            var success = new OpenApiResponse
            {
                Description = "ผลสำเร็จตามสิทธิ์อ่านของผู้ใช้ใน exact scope; public DTO ไม่มี property restrictedNote ไม่ใช่ส่ง null แทน",
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = new OpenApiSchema { AnyOf = variants } } }
            };
            if (write)
            {
                success.Headers = new Dictionary<string, IOpenApiHeader>
                { ["Location"] = new OpenApiHeader { Description = "เส้นทางของ record ภายใน scope เดิม", Schema = new OpenApiSchema { Type = JsonSchemaType.String } } };
                var update = context.Description.HttpMethod == "PATCH";
                var properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["note"] = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 500, Description = "ว่างได้ ไม่รับ control characters; จำกัด500 UTF-16 code units ตาม API" },
                    ["restrictedNote"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null, MaxLength = 500, Description = "ไม่ส่งเพื่อคงค่าเดิมใน PATCH; null ล้างค่า; string แทนค่า ไม่รับ control; เมื่อส่งรวมnull ต้อง write+restricted-read+recent MFA" }
                };
                if (update) properties["expectedVersion"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64", Description = "version ปัจจุบันอย่างน้อย1; ค่าเก่าตอบ409" };
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new()
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = JsonSchemaType.Object,
                                Properties = properties,
                                AdditionalPropertiesAllowed = false,
                                Required = update ? new HashSet<string> { "note", "expectedVersion" } : new HashSet<string> { "note" }
                            }
                        }
                    }
                };
            }
            operation.Responses![write && context.Description.HttpMethod == "POST" ? "201" : "200"] = success;
        }
        // Unhandled/non-database failures can reach the exception handler (500), while binding,
        // revalidation or an upstream rejection may produce no JSON body. Do not promise only 503.
        operation.Responses!["500"] = new OpenApiResponse
        {
            Description = "ข้อผิดพลาดที่ไม่ถูกแปลงเป็น503 เช่น anonymous CSRF denial audit ล้ม; body อาจว่าง ตรวจ Content-Type ก่อน parse",
            Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), cancellationToken: ct) } }
        };
        operation.Responses["default"] = new OpenApiResponse { Description = "ข้อผิดพลาดอื่นที่ client ต้องรองรับ; อาจไม่มี body หรือไม่ใช่ JSON ห้าม retry mutation อัตโนมัติหรือสรุปว่า rollback จาก status เพียงอย่างเดียว" };
    }
}
