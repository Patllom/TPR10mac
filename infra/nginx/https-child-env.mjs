export function httpsChildEnv(env = process.env) {
  const childEnv = { ...env };
  // Playwright workers force FORCE_COLOR=1, so NO_COLOR must not reach them.
  // Keep explicit FORCE_COLOR precedence; translate NO_COLOR-only for direct children.
  if (childEnv.NO_COLOR !== undefined) {
    childEnv.FORCE_COLOR ??= '0';
    delete childEnv.NO_COLOR;
  }
  return childEnv;
}
