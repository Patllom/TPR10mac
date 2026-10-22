// Synthetic challenge payloads only. Keep the production parser strict.
export function captureChallenge(id: string, lifetimeMs = 120000, clock = Date.now) {
  const sampledAt = clock();
  return {
    id,
    serverNowUtc: new Date(sampledAt).toISOString(),
    expiresAtUtc: new Date(sampledAt + lifetimeMs).toISOString(),
  };
}
