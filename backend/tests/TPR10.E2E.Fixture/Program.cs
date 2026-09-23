using TPR10.Api.Identity.Passwords;

// Test-only executable, never shipped with API; no HTTP seeding endpoint.
Console.Write(await new ArgonPasswordHasher().HashAsync("e2e-isolated-password-123", CancellationToken.None));
