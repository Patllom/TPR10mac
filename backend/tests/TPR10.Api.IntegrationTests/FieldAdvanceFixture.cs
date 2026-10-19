using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;

namespace TPR10.Api.IntegrationTests;

// ข้อมูลจำลองทั้งหมด ใช้ฐานแยกใน test container เท่านั้น
internal sealed class FieldAdvanceFixture(IdentityDatabase database) : IAsyncDisposable
{
    internal const string PreviousMigration = "20261018122358_AddCorrectionRecoveryDay";
    internal static readonly Guid WorkspaceId = Guid.Parse("71000000-0000-0000-0000-000000000001");
    internal static readonly Guid OtherWorkspaceId = Guid.Parse("71000000-0000-0000-0000-000000000002");
    internal static readonly Guid ProjectId = Guid.Parse("71000000-0000-0000-0000-000000000011");
    internal static readonly Guid OtherProjectId = Guid.Parse("71000000-0000-0000-0000-000000000013");
    internal static readonly Guid AuthorId = Guid.Parse("71000000-0000-0000-0000-000000000031");
    internal static readonly Guid ReviewerId = Guid.Parse("71000000-0000-0000-0000-000000000032");
    internal static readonly Guid RecipientId = Guid.Parse("71000000-0000-0000-0000-000000000033");
    internal static readonly Guid AliasAccountId = Guid.Parse("71000000-0000-0000-0000-000000000034");
    internal static readonly Guid AuthorPersonId = Guid.Parse("72000000-0000-0000-0000-000000000031");
    internal static readonly Guid ReviewerPersonId = Guid.Parse("72000000-0000-0000-0000-000000000032");
    internal static readonly Guid RecipientPersonId = Guid.Parse("72000000-0000-0000-0000-000000000033");
    internal static readonly Guid CompanyRevisionId = Guid.Parse("73000000-0000-0000-0000-000000000001");
    internal static readonly Guid ProjectRevisionId = Guid.Parse("73000000-0000-0000-0000-000000000011");
    internal static readonly Guid ProposalId = Guid.Parse("74000000-0000-0000-0000-000000000001");

    internal static readonly string[] Tables =
    [
        "finance_persons", "finance_account_links", "field_advance_policy_revisions",
        "field_advance_approval_bands", "field_advance_category_rules",
        "field_advance_policy_contributors", "field_advance_policy_reviews", "field_advance_active_policies",
        "field_advance_grant_proposals", "field_advance_grant_reviews", "field_advance_grant_assignments",
        "field_advance_command_receipts", "field_advance_bootstrap_receipts"
    ];

    internal IdentityDatabase Database { get; } = database;
    internal Tpr10DbContext CreateContext() => Database.CreateContext();

    public static async Task<FieldAdvanceFixture> CreateAsync(PostgresFixture postgres)
    {
        var database = new IdentityDatabase(postgres.ConnectionString);
        await database.InitializeAsync();
        try
        {
            await using var db = database.CreateContext();
            await db.Database.MigrateAsync();
            await SeedDirectoryAsync(db);
            return new FieldAdvanceFixture(database);
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    internal static async Task AssertFoundationAsync(Tpr10DbContext db)
    {
        var actual = await db.Database.SqlQueryRaw<string>(
            "SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync();
        foreach (var table in Tables)
            Assert.Contains(table, actual);
    }

    // ค่า ID ของบุคคลแยกจากบัญชีโดยเจตนา ไม่มีการอนุมานหรือสร้าง mapping จาก directory
    internal async Task SeedPeopleAsync()
    {
        await using var db = CreateContext();
        await AssertFoundationAsync(db);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO finance_persons(id,workspace_id,created_by,reason) VALUES
            ('72000000-0000-0000-0000-000000000031','71000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000031','บุคคลจำลองผู้เสนอ'),
            ('72000000-0000-0000-0000-000000000032','71000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000031','บุคคลจำลองผู้ตรวจ'),
            ('72000000-0000-0000-0000-000000000033','71000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000031','บุคคลจำลองผู้รับ'),
            ('72000000-0000-0000-0000-000000000041','71000000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000031','บุคคลจำลองต่างบริษัท');
            INSERT INTO finance_account_links(id,workspace_id,user_id,person_id,created_by,created_at_utc,reason) VALUES
            ('75000000-0000-0000-0000-000000000031','71000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000031','72000000-0000-0000-0000-000000000031','71000000-0000-0000-0000-000000000031','2026-01-01T00:00:00Z','เชื่อมบัญชีจำลองโดยชัดแจ้ง'),
            ('75000000-0000-0000-0000-000000000032','71000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000032','72000000-0000-0000-0000-000000000032','71000000-0000-0000-0000-000000000031','2026-01-01T00:00:00Z','เชื่อมบัญชีจำลองโดยชัดแจ้ง'),
            ('75000000-0000-0000-0000-000000000033','71000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000033','72000000-0000-0000-0000-000000000033','71000000-0000-0000-0000-000000000031','2026-01-01T00:00:00Z','เชื่อมบัญชีจำลองโดยชัดแจ้ง');
            """);
    }

    internal async Task SeedPoliciesAsync()
    {
        await SeedPeopleAsync();
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO field_advance_policy_revisions
                (id,workspace_id,project_id,base_company_version_id,state,clearing_days,content_hash,created_by,created_by_person_id,reason) VALUES
            ('73000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000001',NULL,NULL,'Draft',
             7,repeat('a',64),
             '71000000-0000-0000-0000-000000000031','72000000-0000-0000-0000-000000000031','กติกาจำลองเท่านั้น'),
            ('73000000-0000-0000-0000-000000000011','71000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000011',
             '73000000-0000-0000-0000-000000000001','Draft',7,repeat('b',64),
             '71000000-0000-0000-0000-000000000031','72000000-0000-0000-0000-000000000031','ฉบับโครงการจำลอง');
            INSERT INTO field_advance_approval_bands(policy_revision_id,"order",level_code,upper_inclusive)
            VALUES ('73000000-0000-0000-0000-000000000001',1,'TEST',1234.56);
            INSERT INTO field_advance_category_rules(policy_revision_id,id,name,active,unit,standard_rate,evidence_types)
            VALUES ('73000000-0000-0000-0000-000000000001','73000000-0000-0000-0000-000000000021','หมวดจำลอง',true,'หน่วยจำลอง',42.75,ARRAY['receipt']);
            INSERT INTO field_advance_policy_contributors(id,workspace_id,policy_revision_id,person_id,user_id,reason) VALUES
            (gen_random_uuid(),'71000000-0000-0000-0000-000000000001','73000000-0000-0000-0000-000000000001','72000000-0000-0000-0000-000000000031','71000000-0000-0000-0000-000000000031','ผู้ร่วมแก้จำลอง');
            """);
    }

    internal async Task SeedGrantAsync()
    {
        await SeedPeopleAsync();
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO field_advance_grant_proposals
                (id,workspace_id,project_id,recipient_id,recipient_person_id,proposed_by,proposed_by_person_id,
                 state,capabilities,delegable_capabilities,approval_limit,starts_at_utc,reason) VALUES
            ('74000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000001',NULL,
             '71000000-0000-0000-0000-000000000033','72000000-0000-0000-0000-000000000033',
             '71000000-0000-0000-0000-000000000031','72000000-0000-0000-0000-000000000031',
             'Pending',ARRAY['policy:read'],ARRAY[]::text[],1234.56,'2026-02-01T00:00:00Z','ข้อเสนอจำลอง');
            INSERT INTO field_advance_grant_reviews(id,workspace_id,proposal_id,reviewer_id,reviewer_person_id,outcome,reason) VALUES
            ('74000000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000001','74000000-0000-0000-0000-000000000001',
             '71000000-0000-0000-0000-000000000032','72000000-0000-0000-0000-000000000032','Approved','ผลตรวจจำลอง');
            """);
    }

    internal async Task SeedAssignmentAsync()
    {
        await SeedGrantAsync();
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE field_advance_grant_proposals SET state='Approved';
            INSERT INTO field_advance_grant_assignments(id,workspace_id,project_id,proposal_id,review_id,created_by,reason)
            VALUES ('74000000-0000-0000-0000-000000000003','71000000-0000-0000-0000-000000000001',NULL,
             '74000000-0000-0000-0000-000000000001','74000000-0000-0000-0000-000000000002',
             '71000000-0000-0000-0000-000000000032','สิทธิ์จำลองจากข้อเสนอ');
            INSERT INTO field_advance_command_receipts(command_id,workspace_id,actor_id,action,payload_sha256,status,result_json)
            VALUES ('76000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000001',
             '71000000-0000-0000-0000-000000000031','bootstrap-test',repeat('c',64),201,jsonb_build_object('code','ok'));
            INSERT INTO field_advance_bootstrap_receipts(id,workspace_id,command_id,grant_assignment_id,created_by,reason)
            VALUES (gen_random_uuid(),'71000000-0000-0000-0000-000000000001','76000000-0000-0000-0000-000000000001',
             '74000000-0000-0000-0000-000000000003','71000000-0000-0000-0000-000000000031','หลักฐาน bootstrap จำลอง');
            """);
    }

    // Seed only existing directory/account data. Financial records are always explicit per test.
    internal static Task SeedDirectoryAsync(Tpr10DbContext db) => db.Database.ExecuteSqlRawAsync("""
        INSERT INTO users(id,username,normalized_username) VALUES
        ('71000000-0000-0000-0000-000000000031','fa-author','FA-AUTHOR'),
        ('71000000-0000-0000-0000-000000000032','fa-reviewer','FA-REVIEWER'),
        ('71000000-0000-0000-0000-000000000033','fa-recipient','FA-RECIPIENT'),
        ('71000000-0000-0000-0000-000000000034','fa-alias','FA-ALIAS');
        INSERT INTO roles(id,name,role_class) VALUES
        ('71000000-0000-0000-0000-000000000051','ผู้ดูแลจำลอง','system-administration');
        INSERT INTO user_roles(user_id,role_id) VALUES
        ('71000000-0000-0000-0000-000000000031','71000000-0000-0000-0000-000000000051');
        INSERT INTO workspaces(id,code,name,created_by) VALUES
        ('71000000-0000-0000-0000-000000000001','FA1','บริษัทจำลองหนึ่ง','71000000-0000-0000-0000-000000000031'),
        ('71000000-0000-0000-0000-000000000002','FA2','บริษัทจำลองสอง','71000000-0000-0000-0000-000000000031');
        INSERT INTO departments(id,workspace_id,code,name,created_by) VALUES
        ('71000000-0000-0000-0000-000000000021','71000000-0000-0000-0000-000000000001','D1','แผนกจำลอง','71000000-0000-0000-0000-000000000031');
        INSERT INTO projects(id,workspace_id,code,name,created_by) VALUES
        ('71000000-0000-0000-0000-000000000011','71000000-0000-0000-0000-000000000001','P1','โครงการจำลองหนึ่ง','71000000-0000-0000-0000-000000000031'),
        ('71000000-0000-0000-0000-000000000012','71000000-0000-0000-0000-000000000001','P2','โครงการจำลองสอง','71000000-0000-0000-0000-000000000031'),
        ('71000000-0000-0000-0000-000000000013','71000000-0000-0000-0000-000000000002','P3','โครงการต่างบริษัท','71000000-0000-0000-0000-000000000031');
        INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason)
        SELECT gen_random_uuid(),id,'71000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000021',
               '2026-01-01T00:00:00Z','71000000-0000-0000-0000-000000000031','สมาชิกจำลอง'
        FROM users WHERE normalized_username LIKE 'FA-%';
        """);

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
