using System.Text;

// Synthetic users in the disposable HTTPS database only; never part of the API deployment.
internal static class AttendanceSeed
{
    internal static string Sql(string hash)
    {
        var sql = new StringBuilder();
        var actor = Guid.NewGuid(); var role = Guid.NewGuid();
        sql.AppendLine($"INSERT INTO roles(id,name,role_class) VALUES ('{role}','ผู้จัดการทะเบียนทดสอบ','system-administration');");
        sql.AppendLine($"INSERT INTO role_permissions(role_id,permission_id) SELECT '{role}',id FROM permissions WHERE capability='attendance:directory-manage';");
        foreach (var name in new[] { "operator", "second", "privacy", "late", "history", "recovery", "masking", "staff", "employee", "manager", "hr", "employee-history", "manager-history", "manager-history-two" })
        {
            var id = name == "operator" ? actor : Guid.NewGuid();
            sql.AppendLine($"INSERT INTO users(id,username,normalized_username) VALUES ('{id}','e2e-directory-{name}','E2E-DIRECTORY-{name.ToUpperInvariant()}');");
            sql.AppendLine($"INSERT INTO local_credentials(user_id,password_hash,must_change_password,password_changed_at_utc) VALUES ('{id}','{hash}',false,now());");
            if (name is "operator" or "second" or "privacy" or "late" or "history" or "recovery" or "masking") sql.AppendLine($"INSERT INTO user_roles(user_id,role_id) VALUES ('{id}','{role}');");
        }
        const string workspace = "11111111-1111-4111-8111-111111111111";
        sql.AppendLine($"INSERT INTO workspaces(id,code,name,created_by) VALUES ('{workspace}','ATTENDANCE','หน่วยงานทดสอบ 6A','{actor}');");
        foreach (var (id, code) in new[] { ("22222222-2222-4222-8222-222222222222", "A"), ("33333333-3333-4333-8333-333333333333", "B") })
            sql.AppendLine($"INSERT INTO departments(id,workspace_id,code,name,created_by) VALUES ('{id}','{workspace}','{code}','แผนกทดสอบ {code}','{actor}');");
        for (var i = 0; i < 101; i++)
            sql.AppendLine($"INSERT INTO users(id,username,normalized_username) VALUES ('{Guid.NewGuid()}','pagination-{i:D3}','PAGINATION-{i:D3}');");
        return sql.ToString();
    }
}
