using System.Text;

// Disposable browser fixtures only. Never linked into the deployed API.
internal static class ScopeSeed
{
    internal static string Sql(string hash)
    {
        var sql = new StringBuilder();
        var users = new Dictionary<string, Guid>();
        foreach (var name in new[] { "staff", "other", "approver", "privacy", "window-read", "window-export", "admin", "manager", "reviewer", "target", "many" })
        {
            var user = Guid.NewGuid(); users[name] = user;
            sql.AppendLine($"INSERT INTO users(id,username,normalized_username) VALUES ('{user}','e2e-scope-{name}','E2E-SCOPE-{name.ToUpperInvariant()}');");
            sql.AppendLine($"INSERT INTO local_credentials(user_id,password_hash,must_change_password,password_changed_at_utc) VALUES ('{user}','{hash}',false,now());");
        }
        const string w = "11111111-1111-4111-8111-111111111111";
        const string p = "22222222-2222-4222-8222-222222222222";
        var a = "33333333-3333-4333-8333-333333333333";
        var b = "44444444-4444-4444-8444-444444444444";
        sql.AppendLine($"INSERT INTO workspaces(id,code,name,created_by) VALUES ('{w}','E2E','พื้นที่ทดสอบ','{users["admin"]}');");
        sql.AppendLine($"INSERT INTO projects(id,workspace_id,code,name,created_by) VALUES ('{p}','{w}','E2E','โครงการทดสอบ','{users["admin"]}');");
        foreach (var (id, name) in new[] { (a, "A"), (b, "B") })
        {
            sql.AppendLine($"INSERT INTO sites(id,workspace_id,project_id,code,name,created_by) VALUES ('{id}','{w}','{p}','{name}','ไซต์ทดสอบ {name}','{users["admin"]}');");
            sql.AppendLine($"INSERT INTO scope_probe_records(id,workspace_id,project_id,site_id,note,restricted_note,created_by) VALUES ('{Guid.NewGuid()}','{w}','{p}','{id}','ข้อมูลทดสอบเฉพาะ {name}','ข้อมูลจำกัด {name}','{users["admin"]}');");
        }
        Guid Role(string name, string roleClass, params string[] caps)
        {
            var role = Guid.NewGuid();
            sql.AppendLine($"INSERT INTO roles(id,name,role_class) VALUES ('{role}','บทบาททดสอบ {name}','{roleClass}');");
            foreach (var cap in caps) sql.AppendLine($"INSERT INTO role_permissions(role_id,permission_id) SELECT '{role}',id FROM permissions WHERE capability='{cap}';");
            return role;
        }
        var staff = Role("พนักงาน", "staff", "scope-probe:read", "scope-probe:write");
        var read = Role("อ่าน", "staff", "scope-probe:read");
        var approval = Role("อนุมัติ", "approval", "scope-probe:read", "scope-probe:write", "scope-probe:export", "scope-probe:restricted-read");
        foreach (var name in new[] { "admin", "manager", "reviewer" })
        {
            var role = Role(name, "system-administration", name == "admin" ? new[] { "organization:manage", "scope-assignments:manage" } : new[] { "scope-assignments:manage" });
            sql.AppendLine($"INSERT INTO user_roles(user_id,role_id) VALUES ('{users[name]}','{role}');");
        }
        foreach (var (name, site, role) in new[] { ("staff", a, staff), ("other", b, read), ("approver", a, approval), ("approver", b, read), ("privacy", a, approval), ("window-read", a, approval), ("window-export", a, approval) })
            sql.AppendLine($"INSERT INTO user_scope_assignments(id,user_id,workspace_id,project_id,site_id,role_id,created_by,reason) VALUES ('{Guid.NewGuid()}','{users[name]}','{w}','{p}','{site}','{role}','{users["admin"]}','ข้อมูลทดสอบเท่านั้น');");
        for (var i = 0; i < 101; i++)
        {
            var workspace = Guid.NewGuid();
            sql.AppendLine($"INSERT INTO workspaces(id,code,name,created_by) VALUES ('{workspace}','PAGE_{i}','พื้นที่แบ่งหน้า {i}','{users["admin"]}');");
            sql.AppendLine($"INSERT INTO user_scope_assignments(id,user_id,workspace_id,role_id,created_by,reason) VALUES ('{Guid.NewGuid()}','{users["many"]}','{workspace}','{read}','{users["admin"]}','ข้อมูลทดสอบเท่านั้น');");
        }
        return sql.ToString();
    }
}
