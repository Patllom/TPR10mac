using System.Text;

// Only the disposable HTTPS runner consumes this SQL. No deployed endpoint or bootstrap.
internal static class FieldAdvanceSeed
{
    internal static string Sql()
    {
        var sql = new StringBuilder();
        for (var n = 1; n <= 8; n++)
        {
            var users = Enumerable.Range(1, 5).Select(i => $"88000000-0000-4000-8000-{n * 100 + i:D12}").ToArray();
            foreach (var (user, index) in users.Select((u, i) => (u, i)))
                sql.AppendLine($"INSERT INTO users(id,username,normalized_username) VALUES ('{user}','policy-fixture-{n}-{index}','POLICY-FIXTURE-{n}-{index}');");
            var w = $"88100000-0000-4000-8000-{n:D12}";
            var p = $"88200000-0000-4000-8000-{n:D12}";
            var d = Guid.NewGuid();
            sql.AppendLine($"INSERT INTO workspaces(id,code,name,created_by) VALUES ('{w}','POLICY{n}','บริษัทจำลอง {n}','{users[0]}');");
            sql.AppendLine($"INSERT INTO departments(id,workspace_id,code,name,created_by) VALUES ('{d}','{w}','TEST','แผนกจำลอง','{users[0]}');");
            sql.AppendLine($"INSERT INTO projects(id,workspace_id,code,name,created_by) VALUES ('{p}','{w}','TEST','โครงการจำลอง {n}','{users[0]}');");
            var people = users.Select(_ => Guid.NewGuid()).ToArray();
            foreach (var (user, i) in users.Select((u, i) => (u, i)))
            {
                sql.AppendLine($"INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason) VALUES (gen_random_uuid(),'{user}','{w}','{d}',now()-interval '1 day','{users[0]}','ทดสอบเท่านั้น');");
                sql.AppendLine($"INSERT INTO finance_persons(id,workspace_id,created_by,reason) VALUES ('{people[i]}','{w}','{users[0]}','ทดสอบเท่านั้น');");
                sql.AppendLine($"INSERT INTO finance_account_links(id,workspace_id,user_id,person_id,created_by,created_at_utc,reason) VALUES (gen_random_uuid(),'{w}','{user}','{people[i]}','{users[0]}',now()-interval '1 day','ทดสอบเท่านั้น');");
            }
            foreach (var i in new[] { 0, 1, 3 })
                foreach (var project in new[] { false, true })
                {
                    if (i == 3 && !project) continue;
                    var scope = project ? $"'{p}'" : "NULL";
                    var cap = i == 1 ? "policy.review" : project ? "policy.edit.project" : "policy.edit.company";
                    var proposal = Guid.NewGuid(); var review = Guid.NewGuid();
                    // Proposer/reviewer/recipient are three distinct synthetic people.
                    var reviewer = i == 1 ? 0 : 1;
                    sql.AppendLine($"INSERT INTO field_advance_grant_proposals(id,workspace_id,project_id,recipient_id,recipient_person_id,proposed_by,proposed_by_person_id,state,capabilities,delegable_capabilities,starts_at_utc,created_at_utc,reason) VALUES ('{proposal}','{w}',{scope},'{users[i]}','{people[i]}','{users[2]}','{people[2]}','Approved',ARRAY['{cap}'],ARRAY[]::text[],now()-interval '1 hour',now()-interval '1 hour','สิทธิ์ทดสอบ');");
                    sql.AppendLine($"INSERT INTO field_advance_grant_reviews(id,workspace_id,proposal_id,reviewer_id,reviewer_person_id,outcome,created_at_utc,reason) VALUES ('{review}','{w}','{proposal}','{users[reviewer]}','{people[reviewer]}','Approved',now()-interval '1 hour','ผลตรวจทดสอบ');");
                    sql.AppendLine($"INSERT INTO field_advance_grant_assignments(id,workspace_id,project_id,proposal_id,review_id,created_by,created_at_utc,reason) VALUES (gen_random_uuid(),'{w}',{scope},'{proposal}','{review}','{users[reviewer]}',now()-interval '1 hour','มอบหมายทดสอบ');");
                }
        }
        return sql.ToString();
    }
}
