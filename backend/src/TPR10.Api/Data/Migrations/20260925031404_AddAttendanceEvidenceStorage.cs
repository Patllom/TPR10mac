using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendanceEvidenceStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_employee_memberships_id_user_id_workspace_id_department_id",
                table: "employee_memberships",
                columns: new[] { "id", "user_id", "workspace_id", "department_id" });

            migrationBuilder.CreateTable(
                name: "attendance_storage_locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    alias = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    config_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    accept_writes = table.Column<bool>(type: "boolean", nullable: false),
                    health = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "unknown"),
                    checked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    free_bytes = table.Column<long>(type: "bigint", nullable: true),
                    total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_storage_locations", x => x.id);
                    table.CheckConstraint("ck_attendance_storage_locations_version", "version>=1");
                    table.CheckConstraint("ck_storage_alias", "alias ~ '^[a-z0-9][a-z0-9-]{0,99}$'");
                    table.CheckConstraint("ck_storage_capacity", "(free_bytes IS NULL OR free_bytes>=0) AND (total_bytes IS NULL OR total_bytes>=0) AND (free_bytes IS NULL OR total_bytes IS NULL OR free_bytes<=total_bytes)");
                    table.CheckConstraint("ck_storage_fingerprint", "config_fingerprint ~ '^[a-f0-9]{64}$'");
                    table.CheckConstraint("ck_storage_health", "health IN ('unknown','ready','warning','unavailable')");
                    table.CheckConstraint("ck_storage_kind", "kind IN ('local-folder','nas-mounted-folder')");
                });

            migrationBuilder.CreateTable(
                name: "attendance_storage_write_target",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    storage_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_storage_write_target", x => x.id);
                    table.CheckConstraint("ck_attendance_storage_write_target_version", "version>=1");
                    table.CheckConstraint("ck_storage_target_singleton", "id=1");
                    table.ForeignKey(
                        name: "FK_attendance_storage_write_target_attendance_storage_location~",
                        column: x => x.storage_id,
                        principalTable: "attendance_storage_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "evidence_migration_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_version = table.Column<long>(type: "bigint", nullable: false),
                    target_version = table.Column<long>(type: "bigint", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Pending"),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence_migration_jobs", x => x.id);
                    table.UniqueConstraint("AK_evidence_migration_jobs_id_source_id_target_id", x => new { x.id, x.source_id, x.target_id });
                    table.CheckConstraint("ck_evidence_job_endpoints", "source_id<>target_id AND source_version>=1 AND target_version>=1");
                    table.CheckConstraint("ck_evidence_job_reason", "length(btrim(reason)) BETWEEN 1 AND 500 AND reason !~ '[[:cntrl:]]'");
                    table.CheckConstraint("ck_evidence_job_status", "status IN ('Pending','Running','Blocked','Completed')");
                    table.CheckConstraint("ck_evidence_migration_jobs_version", "version>=1");
                    table.ForeignKey(
                        name: "FK_evidence_migration_jobs_attendance_storage_locations_source~",
                        column: x => x.source_id,
                        principalTable: "attendance_storage_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_evidence_migration_jobs_attendance_storage_locations_target~",
                        column: x => x.target_id,
                        principalTable: "attendance_storage_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_evidence_migration_jobs_users_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "evidence_objects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    storage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_version = table.Column<long>(type: "bigint", nullable: false),
                    object_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    length = table.Column<long>(type: "bigint", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    thumbnail_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    thumbnail_length = table.Column<long>(type: "bigint", nullable: true),
                    thumbnail_width = table.Column<int>(type: "integer", nullable: true),
                    thumbnail_height = table.Column<int>(type: "integer", nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Reserved"),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    fencing_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    lease_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence_objects", x => x.id);
                    table.CheckConstraint("ck_evidence_action", "action IN ('CheckIn','CheckOut')");
                    table.CheckConstraint("ck_evidence_image", "((sha256 IS NULL AND length IS NULL AND width IS NULL AND height IS NULL) OR (sha256 IS NOT NULL AND length IS NOT NULL AND width IS NOT NULL AND height IS NOT NULL AND sha256 ~ '^[a-f0-9]{64}$' AND length BETWEEN 1 AND 10485760 AND width>0 AND height>0 AND width::bigint*height<=20000000))");
                    table.CheckConstraint("ck_evidence_key", "object_key = 'objects/' || left(replace(id::text,'-',''),2) || '/' || replace(id::text,'-','')");
                    table.CheckConstraint("ck_evidence_objects_version", "version>=1");
                    table.CheckConstraint("ck_evidence_pin", "storage_version>=1 AND fencing_version>=1 AND operation_id<>'00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_evidence_prepared", "state NOT IN ('Prepared','Published') OR (sha256 IS NOT NULL AND thumbnail_sha256 IS NOT NULL)");
                    table.CheckConstraint("ck_evidence_state", "state IN ('Reserved','Prepared','Published','Orphan')");
                    table.CheckConstraint("ck_evidence_thumbnail", "((thumbnail_sha256 IS NULL AND thumbnail_length IS NULL AND thumbnail_width IS NULL AND thumbnail_height IS NULL) OR (thumbnail_sha256 IS NOT NULL AND thumbnail_length IS NOT NULL AND thumbnail_width IS NOT NULL AND thumbnail_height IS NOT NULL AND thumbnail_sha256 ~ '^[a-f0-9]{64}$' AND thumbnail_length BETWEEN 1 AND 10485760 AND thumbnail_width>0 AND thumbnail_height>0 AND thumbnail_width::bigint*thumbnail_height<=20000000))");
                    table.CheckConstraint("ck_evidence_time", "snapshot_at_utc<=occurred_at_utc AND isfinite(snapshot_at_utc) AND isfinite(occurred_at_utc)");
                    table.ForeignKey(
                        name: "FK_evidence_objects_attendance_storage_locations_storage_id",
                        column: x => x.storage_id,
                        principalTable: "attendance_storage_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_evidence_objects_employee_memberships_membership_id_owner_i~",
                        columns: x => new { x.membership_id, x.owner_id, x.workspace_id, x.department_id },
                        principalTable: "employee_memberships",
                        principalColumns: new[] { "id", "user_id", "workspace_id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "evidence_bindings",
                columns: table => new
                {
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    published_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence_bindings", x => x.evidence_id);
                    table.CheckConstraint("ck_evidence_event", "event_id<>'00000000-0000-0000-0000-000000000000'::uuid AND isfinite(published_at_utc)");
                    table.ForeignKey(
                        name: "FK_evidence_bindings_evidence_objects_evidence_id",
                        column: x => x.evidence_id,
                        principalTable: "evidence_objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "evidence_locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    variant = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    length = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Pending"),
                    verified_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence_locations", x => x.id);
                    table.CheckConstraint("ck_evidence_copy_bytes", "sha256 ~ '^[a-f0-9]{64}$' AND length BETWEEN 1 AND 10485760");
                    table.CheckConstraint("ck_evidence_copy_key", "object_key = 'objects/' || left(replace(evidence_id::text,'-',''),2) || '/' || replace(evidence_id::text,'-','') || '/' || variant || '.jpg'");
                    table.CheckConstraint("ck_evidence_copy_state", "state IN ('Pending','Verified','Active','Fallback','Quarantined')");
                    table.CheckConstraint("ck_evidence_copy_variant", "variant IN ('full','thumbnail')");
                    table.CheckConstraint("ck_evidence_copy_verified", "state NOT IN ('Verified','Active','Fallback') OR verified_at_utc IS NOT NULL");
                    table.CheckConstraint("ck_evidence_locations_version", "version>=1");
                    table.ForeignKey(
                        name: "FK_evidence_locations_attendance_storage_locations_storage_id",
                        column: x => x.storage_id,
                        principalTable: "attendance_storage_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_evidence_locations_evidence_objects_evidence_id",
                        column: x => x.evidence_id,
                        principalTable: "evidence_objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "evidence_migration_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    expected_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expected_length = table.Column<long>(type: "bigint", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Pending"),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_owner = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fencing_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidence_migration_items", x => x.id);
                    table.CheckConstraint("ck_evidence_item_error", "error_code IS NULL OR error_code ~ '^[a-z0-9][a-z0-9-]{0,79}$'");
                    table.CheckConstraint("ck_evidence_item_hash", "expected_sha256 ~ '^[a-f0-9]{64}$' AND expected_length BETWEEN 1 AND 10485760");
                    table.CheckConstraint("ck_evidence_item_lease", "attempts>=0 AND fencing_version>=1 AND ((lease_owner IS NULL)=(lease_until_utc IS NULL))");
                    table.CheckConstraint("ck_evidence_item_status", "status IN ('Pending','Copying','Verified','Completed','Blocked')");
                    table.CheckConstraint("ck_evidence_item_variant", "variant IN ('full','thumbnail')");
                    table.CheckConstraint("ck_evidence_migration_items_version", "version>=1");
                    table.ForeignKey(
                        name: "FK_evidence_migration_items_evidence_migration_jobs_job_id_sou~",
                        columns: x => new { x.job_id, x.source_id, x.target_id },
                        principalTable: "evidence_migration_jobs",
                        principalColumns: new[] { "id", "source_id", "target_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_evidence_migration_items_evidence_objects_evidence_id",
                        column: x => x.evidence_id,
                        principalTable: "evidence_objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_storage_locations_alias",
                table: "attendance_storage_locations",
                column: "alias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_storage_write_target_storage_id",
                table: "attendance_storage_write_target",
                column: "storage_id");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_bindings_event_id",
                table: "evidence_bindings",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_evidence_locations_evidence_id_storage_id_variant",
                table: "evidence_locations",
                columns: new[] { "evidence_id", "storage_id", "variant" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_evidence_locations_evidence_id_variant",
                table: "evidence_locations",
                columns: new[] { "evidence_id", "variant" },
                unique: true,
                filter: "state = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_locations_storage_id_object_key",
                table: "evidence_locations",
                columns: new[] { "storage_id", "object_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_evidence_migration_items_evidence_id",
                table: "evidence_migration_items",
                column: "evidence_id");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_migration_items_job_id_evidence_id_variant",
                table: "evidence_migration_items",
                columns: new[] { "job_id", "evidence_id", "variant" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_evidence_migration_items_job_id_source_id_target_id",
                table: "evidence_migration_items",
                columns: new[] { "job_id", "source_id", "target_id" });

            migrationBuilder.CreateIndex(
                name: "IX_evidence_migration_jobs_request_id",
                table: "evidence_migration_jobs",
                column: "request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_evidence_migration_jobs_requested_by",
                table: "evidence_migration_jobs",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_migration_jobs_source_id",
                table: "evidence_migration_jobs",
                column: "source_id",
                unique: true,
                filter: "status <> 'Completed'");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_migration_jobs_target_id",
                table: "evidence_migration_jobs",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "IX_evidence_objects_membership_id_owner_id_workspace_id_depart~",
                table: "evidence_objects",
                columns: new[] { "membership_id", "owner_id", "workspace_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "IX_evidence_objects_operation_id",
                table: "evidence_objects",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_evidence_objects_owner_id_occurred_at_utc",
                table: "evidence_objects",
                columns: new[] { "owner_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_evidence_objects_storage_id",
                table: "evidence_objects",
                column: "storage_id");
            migrationBuilder.Sql(CreateGuards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DowngradePreflight);
            migrationBuilder.DropTable(
                name: "attendance_storage_write_target");

            migrationBuilder.DropTable(
                name: "evidence_bindings");

            migrationBuilder.DropTable(
                name: "evidence_locations");

            migrationBuilder.DropTable(
                name: "evidence_migration_items");

            migrationBuilder.DropTable(
                name: "evidence_migration_jobs");

            migrationBuilder.DropTable(
                name: "evidence_objects");

            migrationBuilder.DropTable(
                name: "attendance_storage_locations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_employee_memberships_id_user_id_workspace_id_department_id",
                table: "employee_memberships");
            migrationBuilder.Sql("DROP FUNCTION evidence_serialize_write(), evidence_no_remove(), evidence_preserve_object(), evidence_preserve_copy(), evidence_preserve_binding(), evidence_preserve_storage(), evidence_monotonic_target(), evidence_validate_publication();");
        }
    }
}
