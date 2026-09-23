using System.Text;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.Identity.Passwords;

namespace TPR10.Api.Identity.Accounts;

public static class BootstrapCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || args[0] != "--bootstrap-admin" || Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("ใช้ --bootstrap-admin เพียงอย่างเดียวจาก terminal แบบ interactive เท่านั้น");
            return 64;
        }
        var connection = Environment.GetEnvironmentVariable("TPR10_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine("ต้องกำหนด TPR10_CONNECTION_STRING ของฐานข้อมูลที่เตรียมไว้ก่อน");
            return 64;
        }
        Console.Write("ชื่อผู้ใช้: ");
        var username = Console.ReadLine();
        Console.Write("รหัสผ่าน: ");
        var password = ReadPassword();
        if (username is null || password is null) return 1;
        Console.Write("ยืนยันรหัสผ่าน: ");
        var confirmation = ReadPassword();
        if (confirmation is null || !string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("ยกเลิก: รหัสผ่านยืนยันไม่ตรงกันหรือผู้ใช้ยกเลิก ไม่มีการสร้างบัญชี");
            return 1;
        }
        try
        {
            await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(connection).Options);
            var correlation = new CorrelationContext();
            correlation.Initialize(Guid.NewGuid());
            var service = new BootstrapService(db, new ArgonPasswordHasher(), new AuditEventWriter(db, correlation, TimeProvider.System), TimeProvider.System);
            var code = await service.CreateAsync(username, password, CancellationToken.None);
            Console.WriteLine(code switch
            {
                0 => "สร้างผู้ดูแลเริ่มต้นแล้ว ต้องตั้งค่า MFA ก่อนใช้งานสิทธิ์ผู้ดูแล",
                2 => "ปฏิเสธ: มีบัญชีอยู่แล้ว ไม่มีการแก้ไขบัญชีเดิม",
                _ => "ข้อมูลไม่ถูกต้อง: ชื่อผู้ใช้หรือรหัสผ่านไม่ผ่านนโยบาย"
            });
            return code;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine("สร้างผู้ดูแลไม่สำเร็จ ตรวจฐานข้อมูลและสถานะ migration ก่อนลองใหม่");
            return 1;
        }
    }

    private static string? ReadPassword()
    {
        var buffer = new StringBuilder();
        var overflow = false;
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return overflow ? null : buffer.ToString(); }
            if (key.Key == ConsoleKey.Escape) { Console.WriteLine(); return null; }
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; continue; }
            if (key.KeyChar == '\0') continue;
            if (buffer.Length < 256) buffer.Append(key.KeyChar);
            else overflow = true;
        }
    }
}
