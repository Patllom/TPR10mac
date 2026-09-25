using TPR10.Api.Identity.Passwords;

// Test-only executable, never shipped with API; no HTTP seeding endpoint.
var hash = await new ArgonPasswordHasher().HashAsync("e2e-isolated-password-123", CancellationToken.None);
Console.Write(args.SequenceEqual(new[] { "--scope-sql" }) ? ScopeSeed.Sql(hash)
    : args.SequenceEqual(new[] { "--attendance-sql" }) ? AttendanceSeed.Sql(hash) : hash);
