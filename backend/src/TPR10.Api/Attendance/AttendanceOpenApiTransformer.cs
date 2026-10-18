using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Recording;

namespace TPR10.Api.Attendance;

// Explicitly delegated by IdentityOpenApiTransformer; documentation never authorizes a request.
public sealed class AttendanceOpenApiTransformer
{
    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, DirectoryEndpointMetadata metadata, CancellationToken ct)
    {
        operation.Extensions!["x-tpr10-scope"] = new JsonNodeExtension(JsonValue.Create(metadata.Domain));
        operation.Extensions["x-tpr10-permissions"] = new JsonNodeExtension(new JsonArray(metadata.Capability == "" ? [] : [(JsonNode?)JsonValue.Create(metadata.Capability)]));
        operation.Extensions["x-tpr10-mfa-required"] = new JsonNodeExtension(JsonValue.Create(metadata.RequireMfa));
        if (metadata.Domain == "attendance-corrections")
        {
            await Corrections.CorrectionOpenApi.TransformAsync(operation, context, ct);
            return;
        }
        if (metadata.Domain is "attendance-recording" or "attendance-history")
        {
            await RecordingAsync(operation, context, metadata, ct);
            return;
        }
        operation.Description += metadata.Domain == "attendance-access"
            ? "\nBoolean hints สำหรับแสดงเมนูเท่านั้น ไม่ใช่ authority ไม่คืนข้อมูลลูกทีม รูปหรือพิกัด; API ปลายทางต้องตรวจสิทธิ์ปัจจุบันอีกครั้ง."
            : metadata.Domain == "attendance-evidence"
            ? "\nอ่าน JPEG ที่ Published เท่านั้น: ตรวจ IAttendanceAccess.ReadAsync/CanReadPhoto ทั้งก่อนโหลดและก่อนส่ง. เจ้าของอ่านของตนเองได้; HR ต้อง capability, recent MFA และ current grant ตรงหน่วยงานใน snapshot; หัวหน้าอ่านรูปลูกทีมไม่ได้; Admin/storage-manage ไม่มีสิทธิ์โดยอัตโนมัติ. ไม่มี upload/publish endpoint. full/thumbnail/download ใช้สิทธิ์เดียวกัน; no-store, nosniff, ไม่ตอบ304; Range416หลังตรวจสิทธิ์, HEADไม่เปิดเผยข้อมูลก่อนอนุญาต. โหลดและตรวจ checksum นอก identity lock แล้วใช้ dedicated connection/shared advisory lock7241002 ระหว่าง audit commit และส่ง buffer ไม่เกิน10MiB/5วินาที. ถอนสิทธิ์ชนะ lock จะไม่มีรูป; หาก reader ชนะจะส่งในช่วงจำกัดก่อน revocation commit; เรียกคืน bytes ที่ดาวน์โหลดแล้วไม่ได้. ล้มเหลวหลังเริ่มส่งจะ abort ไม่ต่อ JSON."
            : metadata.Domain == "attendance-storage"
            ? "\nControl plane ที่เก็บหลักฐาน: permission attendance:storage-manage และ recent MFA; ไม่ให้สิทธิ์อ่านรูป/GPS. เลือกได้เฉพาะ alias จาก protected configuration ไม่รับ root path หรือ credentials. readiness มีอายุ60วินาที ผูกกับรุ่นและ mount; หลัง restart ต้องตรวจใหม่. ลงทะเบียนไม่เปลี่ยน write target และเปลี่ยนปลายทางไม่ย้ายรูปเก่า. expectedVersion ใช้รุ่นล่าสุดจาก server; 409 ให้โหลดใหม่ ไม่ retry mutation อัตโนมัติ. เหตุผล1–500 Unicode scalars ไม่มี control characters. health ตรวจ manifest ครั้งละไม่เกิน100; missingObjects หมายถึง copy ที่ตรวจยืนยันไม่ได้ในรอบสแกน ไม่ยืนยันว่าถูกลบ; orphanObjects นับสถานะ Orphan ในฐานข้อมูล ไม่สำรวจไฟล์นอก manifest. errorCode ไม่มี path/ข้อมูลส่วนตัว."
            : "\nControl plane จัดการต้นสังกัด สายบังคับบัญชาและ HR เท่านั้น ไม่ให้สิทธิ์อ่านรูป/GPS หรือ Site โดยอัตโนมัติ. ต้อง permission+recent MFA; ห้ามเพิ่มอำนาจตนเอง. effective-now และประวัติ read-only; เปลี่ยนจริง revoke session ผู้ได้รับผลพร้อม audit ใน transaction. expectedVersion ใช้ค่าจาก server ห้ามสมมติว่าเริ่ม1หลัง replace; ค่าเก่าตอบ409 โหลดใหม่ก่อนส่งด้วยตนเอง. เหตุผล1–500 Unicode scalars ไม่มี control characters.";
        if (operation.Parameters?.Any(p => p.Name == "pageSize") == true)
            operation.Extensions["x-tpr10-pagination"] = new JsonNodeExtension(new JsonObject { ["pageMinimum"] = 1, ["pageSizeDefault"] = 25, ["pageSizeMaximum"] = 100, ["overMaximum"] = "clamp" });
        if (metadata.Domain == "attendance-storage" && operation.Parameters?.Any(p => p.Name == "limit") == true)
            operation.Extensions["x-tpr10-pagination"] = new JsonNodeExtension(new JsonObject { ["offsetMinimum"] = 0, ["limitDefault"] = 25, ["limitMaximum"] = 100, ["overMaximum"] = "reject-400" });
        var problem = await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), cancellationToken: ct);
        if (metadata.Domain == "attendance-evidence" && operation.Responses!["200"] is OpenApiResponse success)
            success.Content = new Dictionary<string, OpenApiMediaType>
            {
                ["image/jpeg"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } }
            };
        foreach (var status in new[] { "400", "401", "403", "404", "409", "500", "503" })
            if (!operation.Responses!.ContainsKey(status)) operation.Responses[status] = new OpenApiResponse
            {
                Description = $"ข้อผิดพลาด {status}; body อาจว่างหรือไม่ใช่ JSON ตรวจ Content-Type ก่อน parse",
                Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = problem } }
            };
        operation.Responses!["default"] = new OpenApiResponse { Description = "ข้อผิดพลาดอื่น อาจไม่มี body; ห้าม retry mutation อัตโนมัติหรือสรุปว่า rollback จาก status เท่านั้น" };
    }

    private static async Task RecordingAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, DirectoryEndpointMetadata metadata, CancellationToken ct)
    {
        operation.Description += "\nลงเวลา/ประวัติ: session และ current grants ตรวจโดย API; ตนเอง→HR→หัวหน้า หัวหน้าไม่มี field photo; Admin ไม่มี bypass. ประวัติและผล operation ตรวจสิทธิ์ตอนส่งจริงภายใต้ dedicated unpooled identity lock ไม่เกิน5วินาที ไม่มี filesystem I/O. no-store; 503 ไม่ใช่ logout; ห้าม retry mutation อัตโนมัติ ให้ตรวจ operation ด้วย key เดิมก่อน. การเรียกคืนข้อมูลที่ส่งไปแล้วทำไม่ได้.";
        var path = context.Description.RelativePath ?? "";
        var upload = path.EndsWith("/check-ins", StringComparison.Ordinal) || path.EndsWith("/check-outs", StringComparison.Ordinal);
        if (path.EndsWith("/capture-challenges", StringComparison.Ordinal))
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
                            AdditionalPropertiesAllowed = false,
                            Required = new HashSet<string> { "action", "site", "idempotencyKey" },
                            Properties = new Dictionary<string, IOpenApiSchema>
                            {
                                ["action"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("CheckIn"), JsonValue.Create("CheckOut")] },
                                ["site"] = await context.GetOrCreateSchemaAsync(typeof(Scopes.ScopeKey), cancellationToken: ct),
                                ["idempotencyKey"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
                            }
                        }
                    }
                }
            };
        if (path.EndsWith("/mine", StringComparison.Ordinal) || path.EndsWith("/team", StringComparison.Ordinal) || path.EndsWith("/hr", StringComparison.Ordinal))
        {
            operation.Parameters ??= [];
            foreach (var name in new[] { "from", "to", "pageSize", "cursor" }) operation.Parameters.Add(new OpenApiParameter
            {
                Name = name,
                In = ParameterLocation.Query,
                Required = name is "from" or "to",
                Schema = new OpenApiSchema
                {
                    Type = name == "pageSize" ? JsonSchemaType.Integer : JsonSchemaType.String,
                    Format = name is "from" or "to" ? "date" : null
                },
                Description = name == "pageSize" ? "ตั้งต้น20 สูงสุด100; เกินขอบเขตตอบ400" : name == "cursor" ? "opaque cursor จากหน้าก่อน ผูกกับsessionและfilterเดิม" : "วันไทย yyyy-MM-dd ช่วงรวมสูงสุด31วัน"
            });
        }
        if (path.Contains("/operations/", StringComparison.Ordinal))
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "action",
                In = ParameterLocation.Query,
                Required = true,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("CheckIn"), JsonValue.Create("CheckOut")] }
            });
        }
        if (upload)
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Description = "requestสูงสุด11MiB; metadata JSONหนึ่งส่วนไม่เกิน16KiB และphoto JPEG/PNGหนึ่งส่วนไม่เกิน10MiB/20ล้านpixels; ห้ามส่วนซ้ำ/ไม่รู้จัก URL/base64. browserสร้างboundaryเอง",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["multipart/form-data"] = new()
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Object,
                            Required = new HashSet<string> { "metadata", "photo" },
                            AdditionalPropertiesAllowed = false,
                            Properties = new Dictionary<string, IOpenApiSchema>
                            {
                                ["metadata"] = await context.GetOrCreateSchemaAsync(typeof(CaptureMetadata), cancellationToken: ct),
                                ["photo"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary", Description = "JPEG/PNG <=10MiB และ20ล้านpixels" }
                            }
                        },
                        Encoding = new Dictionary<string, OpenApiEncoding> { ["metadata"] = new() { ContentType = "application/json" }, ["photo"] = new() { ContentType = "image/jpeg, image/png" } }
                    }
                }
            };
        }
        if (metadata.Domain == "attendance-history") operation.Extensions!["x-tpr10-pagination"] = new JsonNodeExtension(new JsonObject
        { ["pageSizeDefault"] = 20, ["pageSizeMaximum"] = 100, ["maximumInclusiveDays"] = 31, ["overMaximum"] = "reject-400", ["cursor"] = "protected; actor/session/audience/filter/pageSize bound; no cross-scope total" });
        var problem = await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), cancellationToken: ct);
        foreach (var status in new[] { "400", "401", "403", "404", "409", "413", "415", "429", "500", "503" })
            operation.Responses![status] = new OpenApiResponse
            {
                Description = $"ข้อผิดพลาด {status}; ตรวจ Content-Type ก่อนอ่าน JSON; ไม่ retry mutation อัตโนมัติ",
                Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = problem } }
            };
        foreach (var status in new[] { "409", "429" })
            ((OpenApiResponse)operation.Responses![status]).Headers = new Dictionary<string, IOpenApiHeader>
            { ["Retry-After"] = new OpenApiHeader { Description = "วินาทีที่ต้องรอเมื่อ operation กำลังทำหรือ budget เต็ม", Schema = new OpenApiSchema { Type = JsonSchemaType.String } } };
        operation.Responses!["default"] = new OpenApiResponse { Description = "อาจไม่มี body เมื่อ transport ล้มเหลว; ให้ตรวจ operation ก่อนส่งใหม่" };
    }
}
