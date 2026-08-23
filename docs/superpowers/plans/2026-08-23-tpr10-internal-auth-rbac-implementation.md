# TPR10 Internal Authentication and RBAC Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a secure internal employee login system with Admin, Content, and Sales roles, temporary-password onboarding, database-backed sessions, immediate revocation, user administration, and audited server-side authorization.

**Architecture:** Build a JavaScript Next.js App Router modular monolith. Authentication uses Argon2id password hashes and opaque 32-byte session tokens whose SHA-256 digests are stored in SQLite; protected entry points resolve the session through one server-only authorization layer and enforce an explicit immutable permission map. Admin account operations, authentication mutations, rate limiting, and audit writes run through focused domain services and database transactions rather than UI components accessing tables directly.

**Tech Stack:** Next.js App Router, JavaScript, React, Tailwind CSS, Drizzle ORM, SQLite/better-sqlite3, Zod, Argon2id, Vitest, Testing Library, Playwright

**Spec:** `docs/superpowers/specs/2026-08-23-tpr10-internal-auth-rbac-design.md`

## Global Constraints

- The first release is for TPR10 employees only; there is no registration or customer login.
- The only roles are `admin`, `content`, and `sales`.
- Admin creates accounts and issues server-generated temporary passwords.
- A temporary-password account must change its password before accessing business features.
- Sessions use a 32-byte opaque token, store only its SHA-256 digest, expire after 8 idle hours or 24 absolute hours, and have no Remember me option.
- Passwords use Argon2id with at least 19 MiB memory, 2 iterations, and parallelism 1; benchmark the selected values on the production server before release.
- Five failed attempts for the same normalized-email/source pair within 15 minutes trigger throttling.
- Every protected data read and mutation enforces permission on the server; hidden navigation is not authorization.
- Role changes, suspension, password reset, and sign-out-everywhere revoke all active sessions immediately.
- Production cookies are `HttpOnly`, `Secure`, `SameSite=Lax`, and scoped to `/`.
- Development runs on port `3001`; production application runtime uses port `8001`.
- Public and internal UI use the TPR10 Editorial Grid design: warm ivory, slate ink, restrained orange, serif display text, sans-serif interface text, no gradients or glow effects.
- Preserve the user's untracked `Doc/` and `docs/anthropic.design.md` files; do not stage them in implementation commits.

## Planned File Structure

```text
src/
  app/
    admin/
      login/page.js                    credential form
      login/actions.js                 login Server Action
      change-password/page.js          mandatory password screen
      change-password/actions.js       password-change Server Action
      forbidden/page.js                branded 403 state
      (protected)/layout.js             full-session boundary and navigation
      (protected)/page.js               role-aware dashboard
      (protected)/content/page.js       Content-permission section entry
      (protected)/leads/page.js         Sales-permission section entry
      (protected)/audit/page.js         Admin authentication audit list
      (protected)/profile/page.js       self-service password management
      (protected)/profile/actions.js    current-password change action
      (protected)/users/page.js         Admin-only user list
      (protected)/users/actions.js      Admin account mutations
    globals.css                         Tailwind import and editorial tokens
    layout.js                           root metadata and fonts
    page.js                             minimal public home
  components/admin/
    admin-shell.js                      role-aware navigation shell
    login-form.js                       accessible login form state
    password-form.js                    password rules and submission state
    user-admin.js                       user table and account dialogs
  db/
    client.js                           SQLite initialization and Drizzle handle
    schema/auth.js                      user, session, and login-attempt tables
    schema/audit.js                     audit event table
    schema/index.js                     schema export
  features/auth/
    config.js                           durations, cookie name, Argon2 parameters
    email.js                            normalization and identity digest
    password.js                         policy, hashing, and verification
    permissions.js                      roles, permission keys, immutable matrix
    session-repository.js               session persistence and revocation
    session-service.js                  issue, resolve, rotate, and cookie helpers
    authorize.js                        requireUser and requirePermission
    login-service.js                    login, logout, mandatory password change
    errors.js                           typed authentication/domain errors
    origin.js                           same-origin mutation guard
  features/users/
    user-repository.js                  employee persistence
    user-service.js                     create/reset/role/suspend/reactivate rules
  features/rate-limit/
    login-rate-limit.js                 SQLite-backed attempt window
  features/audit/
    audit-writer.js                     append-only event helper
  lib/
    constants.js                        approved ports and roles
    env.js                              validated server environment
    logging.js                          correlation IDs and redacted error logging
tests/
  unit/
    auth/                               pure password, permission, and session rules
    users/                              lifecycle and final-Admin rules
  integration/
    db/                                 SQLite initialization and migrations
    auth/                               login, session, throttling, authorization
    users/                              account management and audit transactions
  e2e/
    support/                            deterministic users and login helpers
    internal-auth.spec.js               onboarding and session journeys
    role-access.spec.js                 role matrix and forbidden routes
drizzle/                                generated SQL migrations
scripts/
  bootstrap-admin.js                    one-time initial Admin creation
  cleanup-auth-history.js               bounded login-attempt cleanup
```

---

### Task 1: Bootstrap the JavaScript Next.js Application and Test Harness

**Files:**
- Create: `package.json`
- Create: `next.config.mjs`
- Create: `jsconfig.json`
- Create: `eslint.config.mjs`
- Create: `vitest.config.mjs`
- Create: `playwright.config.js`
- Create: `src/app/layout.js`
- Create: `src/app/page.js`
- Create: `src/app/globals.css`
- Create: `src/lib/constants.js`
- Create: `tests/unit/foundation.test.js`
- Modify: `.gitignore`

**Interfaces:**
- Produces: `APP_PORTS`, `ROLES`, and npm scripts used by every later task.
- Produces: Tailwind/editorial design tokens shared by public and admin screens.

- [ ] **Step 1: Scaffold Next.js into the existing documentation repository**

Run:

```bash
scaffold_dir="$(mktemp -d)"
pnpm create next-app@latest "$scaffold_dir" --js --tailwind --eslint --app --src-dir --use-pnpm --import-alias '@/*' --yes
rsync -a --exclude='.git' --exclude='.gitignore' "$scaffold_dir"/ ./
rm -rf "$scaffold_dir"
pnpm add drizzle-orm better-sqlite3 zod argon2
pnpm add -D drizzle-kit vitest jsdom @vitejs/plugin-react @testing-library/react @testing-library/jest-dom @playwright/test
```

Expected: existing `Doc/` and `docs/` remain unchanged and `package.json` names the package `tpr10-platform`.

- [ ] **Step 2: Write the failing foundation test**

Create `tests/unit/foundation.test.js`:

```js
import { describe, expect, it } from 'vitest';
import { APP_PORTS, ROLES } from '@/lib/constants';

describe('application foundation', () => {
  it('locks the approved runtime ports and roles', () => {
    expect(APP_PORTS).toEqual({ development: 3001, production: 8001 });
    expect(ROLES).toEqual(['admin', 'content', 'sales']);
  });
});
```

- [ ] **Step 3: Configure Vitest and verify the test fails**

Create `vitest.config.mjs` with the `@` alias pointing to `src`:

```js
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

const currentDir = path.dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  plugins: [react()],
  resolve: { alias: { '@': path.join(currentDir, 'src') } },
  test: { environment: 'node', setupFiles: [] },
});
```

Then run:

```bash
pnpm vitest run tests/unit/foundation.test.js
```

Expected: FAIL because `src/lib/constants.js` does not exist.

- [ ] **Step 4: Add constants, scripts, and editorial tokens**

Create `src/lib/constants.js`:

```js
export const APP_PORTS = Object.freeze({ development: 3001, production: 8001 });
export const ROLES = Object.freeze(['admin', 'content', 'sales']);
```

Set these scripts in `package.json`:

```json
{
  "scripts": {
    "dev": "next dev -p 3001",
    "build": "next build",
    "start": "next start -p 8001",
    "lint": "eslint .",
    "test": "vitest run",
    "test:unit": "vitest run tests/unit",
    "test:integration": "vitest run tests/integration",
    "test:e2e": "playwright test"
  }
}
```

In `src/app/globals.css`, import Tailwind and declare `--canvas: #faf9f5`, `--ink: #141413`, `--muted: #5e5d59`, `--hairline: #d1cfc5`, `--surface: #f0eee6`, `--oat: #e3dacc`, and `--brand-orange: #ed5a24`. Set body to the ivory canvas with slate text and no gradients.

- [ ] **Step 5: Verify the application foundation**

Run:

```bash
pnpm test:unit && pnpm lint && pnpm build
```

Expected: all commands exit 0 and Next.js reports the root route.

- [ ] **Step 6: Commit the foundation**

```bash
git add package.json pnpm-lock.yaml next.config.mjs jsconfig.json eslint.config.mjs vitest.config.mjs playwright.config.js src tests .gitignore
git commit -m "chore: bootstrap TPR10 JavaScript application"
```

### Task 2: Add Validated Environment and SQLite Authentication Schema

**Files:**
- Create: `.env.example`
- Create: `drizzle.config.js`
- Create: `src/lib/env.js`
- Create: `src/db/client.js`
- Create: `src/db/schema/auth.js`
- Create: `src/db/schema/audit.js`
- Create: `src/db/schema/index.js`
- Create: `tests/integration/db/auth-schema.test.js`
- Create: `drizzle/0000_auth_foundation.sql`

**Interfaces:**
- Produces: `parseEnv(values)`, `createDatabase(path)`, and `getDb()`.
- Produces: `users`, `sessions`, `loginAttempts`, and `auditEvents` Drizzle tables.

- [ ] **Step 1: Write failing database initialization tests**

Create `tests/integration/db/auth-schema.test.js` that opens a database in a temporary directory and asserts:

```js
expect(sqlite.pragma('journal_mode', { simple: true }).toLowerCase()).toBe('wal');
expect(sqlite.pragma('busy_timeout', { simple: true })).toBe(5000);
expect(sqlite.pragma('foreign_keys', { simple: true })).toBe(1);
```

Insert one user, then assert duplicate `normalized_email` and duplicate `token_digest` values fail their unique constraints. Assert invalid `role` and `status` values fail database check constraints.

- [ ] **Step 2: Run the schema test to verify missing-module failure**

Run:

```bash
pnpm vitest run tests/integration/db/auth-schema.test.js
```

Expected: FAIL because `@/db/client` and schema exports do not exist.

- [ ] **Step 3: Implement environment validation and SQLite initialization**

Implement `parseEnv(values)` with Zod to require absolute production paths and secrets of at least 32 bytes:

```js
export function createDatabase(path) {
  const sqlite = new Database(path);
  sqlite.pragma('journal_mode = WAL');
  sqlite.pragma('busy_timeout = 5000');
  sqlite.pragma('foreign_keys = ON');
  return { sqlite, db: drizzle(sqlite, { schema }) };
}
```

`.env.example` must contain `DATABASE_PATH`, `SESSION_SECRET`, `IDENTITY_DIGEST_SECRET`, `APP_ORIGIN`, and `NODE_ENV` without real secret values.

- [ ] **Step 4: Define the four authentication tables and indexes**

In `src/db/schema/auth.js`, define UUID text primary keys and integer millisecond timestamps. Use database checks for roles `admin|content|sales`, user statuses `invited|active|suspended`, boolean fields, and non-empty token digests. Add indexes on `users.status`, `sessions.user_id`, `sessions.expires_at`, and `login_attempts.attempted_at`.

In `src/db/schema/audit.js`, define append-only event rows with nullable actor/target IDs, required event type and outcome, JSON metadata text, and occurred-at time.

- [ ] **Step 5: Generate and inspect the migration**

Run:

```bash
pnpm drizzle-kit generate
rg -n "CREATE TABLE|CHECK|UNIQUE|CREATE INDEX" drizzle
```

Expected: the migration creates all four tables, role/status checks, both unique constraints, and the required indexes. Rename the generated file to `drizzle/0000_auth_foundation.sql` if Drizzle emits a different descriptive suffix; update its journal metadata consistently.

- [ ] **Step 6: Run database tests and commit**

Run:

```bash
pnpm vitest run tests/integration/db/auth-schema.test.js && pnpm lint
```

Expected: PASS.

```bash
git add .env.example drizzle.config.js drizzle src/lib/env.js src/db tests/integration/db
git commit -m "feat: add authentication database foundation"
```

### Task 3: Implement Email Normalization and Argon2id Password Rules

**Files:**
- Create: `src/features/auth/config.js`
- Create: `src/features/auth/email.js`
- Create: `src/features/auth/password.js`
- Create: `src/features/auth/errors.js`
- Create: `src/features/auth/origin.js`
- Create: `src/lib/logging.js`
- Create: `tests/unit/auth/email.test.js`
- Create: `tests/unit/auth/password.test.js`
- Create: `tests/unit/auth/origin.test.js`

**Interfaces:**
- Produces: `normalizeEmail(value)`, `digestIdentity(secret, value)`, `validateNewPassword(input)`, `hashPassword(password)`, `verifyPassword(hash, password)`, `assertAllowedOrigin(origin, expectedOrigin)`, `createCorrelationId()`, and `logServerError(logger, error, context)`.
- Produces: `ValidationError`, `AuthenticationError`, `ForbiddenError`, and `ConflictError`.

- [ ] **Step 1: Write failing email and password tests**

Test these exact rules:

```js
expect(normalizeEmail('  Staff.Name@TPR10.CO.TH ')).toBe('staff.name@tpr10.co.th');
expect(validateNewPassword({ password: 'short1!', email: 'a@tpr10.co.th' }).ok).toBe(false);
expect(validateNewPassword({ password: 'staff.name2026!', email: 'staff.name@tpr10.co.th' }).ok).toBe(false);
expect(validateNewPassword({ password: 'correct horse battery 7', email: 'staff@tpr10.co.th' }).ok).toBe(true);
```

Also assert the same password produces distinct Argon2 hashes and both verify successfully; a wrong password returns false.

Create `origin.test.js` to assert the canonical `APP_ORIGIN` is allowed, a missing or different origin is rejected for browser mutations, and subdomains or prefix matches are not accepted.

- [ ] **Step 2: Run tests to verify missing exports**

Run:

```bash
pnpm vitest run tests/unit/auth/email.test.js tests/unit/auth/password.test.js
```

Expected: FAIL on unresolved auth modules.

- [ ] **Step 3: Implement configuration and password policy**

Export frozen settings from `config.js`:

```js
export const AUTH_CONFIG = Object.freeze({
  sessionCookie: 'tpr10_session',
  idleTimeoutMs: 8 * 60 * 60 * 1000,
  absoluteTimeoutMs: 24 * 60 * 60 * 1000,
  loginWindowMs: 15 * 60 * 1000,
  maxFailedAttempts: 5,
  argon2: { memoryCost: 19456, timeCost: 2, parallelism: 1, type: argon2.argon2id },
});
```

`validateNewPassword` requires at least 12 characters, at least one digit or symbol, rejects a case-insensitive email local-part match, and rejects `password`, `password123`, `123456789012`, `qwerty123456`, and `admin123456`.

- [ ] **Step 4: Implement hashing and identity digests**

Use `argon2.hash(password, AUTH_CONFIG.argon2)` and `argon2.verify(hash, password)`. Use HMAC-SHA-256 with `IDENTITY_DIGEST_SECRET` for normalized-email and source-address correlation; never use the plain email or address as a login-attempt key.

Implement `assertAllowedOrigin` by parsing both values with `URL` and requiring exact protocol, hostname, and effective port equality. `createCorrelationId` returns `randomUUID()`. `logServerError` writes structured event name, correlation ID, and safe operation metadata while rejecting password, token, cookie, and authorization fields.

- [ ] **Step 5: Run tests and commit**

```bash
pnpm vitest run tests/unit/auth && pnpm lint
git add src/features/auth src/lib/logging.js tests/unit/auth
git commit -m "feat: add password and identity security rules"
```

### Task 4: Implement the Fixed Role Permission Matrix

**Files:**
- Create: `src/features/auth/permissions.js`
- Create: `tests/unit/auth/permissions.test.js`

**Interfaces:**
- Produces: `PERMISSIONS`, `ROLE_PERMISSIONS`, `isRole(value)`, and `can(role, permission)`.

- [ ] **Step 1: Write the failing exact-matrix test**

Create a table-driven test for all permission keys:

```js
const expected = {
  admin: ['dashboard:view', 'content:read', 'content:write', 'content:publish', 'media:manage', 'leads:read', 'leads:update', 'users:manage', 'audit:read', 'settings:manage'],
  content: ['dashboard:view', 'content:read', 'content:write', 'content:publish', 'media:manage'],
  sales: ['dashboard:view', 'leads:read', 'leads:update'],
};

for (const [role, allowed] of Object.entries(expected)) {
  for (const permission of PERMISSIONS) {
    expect(can(role, permission)).toBe(allowed.includes(permission));
  }
}
expect(can('unknown', 'dashboard:view')).toBe(false);
expect(can('admin', 'unknown:permission')).toBe(false);
```

- [ ] **Step 2: Run the permission test and verify failure**

Run: `pnpm vitest run tests/unit/auth/permissions.test.js`

Expected: FAIL because the permission module does not exist.

- [ ] **Step 3: Implement a frozen permission map**

Define `PERMISSIONS` from the ten keys in the spec. Build each role's allowed set once, freeze all exported arrays/objects, and make `can` return a strict boolean without throwing for unknown inputs.

- [ ] **Step 4: Verify and commit**

```bash
pnpm vitest run tests/unit/auth/permissions.test.js && pnpm lint
git add src/features/auth/permissions.js tests/unit/auth/permissions.test.js
git commit -m "feat: define internal role permissions"
```

### Task 5: Add Session Persistence, Rotation, and Cookie Primitives

**Files:**
- Create: `src/features/auth/session-repository.js`
- Create: `src/features/auth/session-service.js`
- Create: `tests/unit/auth/session-rules.test.js`
- Create: `tests/integration/auth/session.test.js`

**Interfaces:**
- Consumes: `AUTH_CONFIG`, `sessions`, and `users`.
- Produces: `createSession({ db, userId, restricted, now, metadata })`, `resolveSession({ db, token, now })`, `rotateSession({ db, sessionId, userId, restricted, now, metadata })`, `revokeSession(db, token)`, `revokeUserSessions(db, userId)`, `setSessionCookie(cookieStore, token, expiresAt)`, and `clearSessionCookie(cookieStore)`.

- [ ] **Step 1: Write failing session-rule tests**

Test that a session is rejected when idle time is greater than 8 hours, absolute age is greater than 24 hours, `revoked_at` is set, or the owning user is suspended. Test that a restricted session contains `restrictedToPasswordChange: true`.

- [ ] **Step 2: Write failing integration tests for token storage**

Create a user and session, then assert:

```js
expect(rawToken).toMatch(/^[A-Za-z0-9_-]{43}$/);
expect(stored.tokenDigest).toBe(createHash('sha256').update(rawToken).digest('hex'));
expect(JSON.stringify(stored)).not.toContain(rawToken);
```

Assert rotation revokes the old row and produces a different token. Assert `revokeUserSessions` invalidates every active session for that user and no other user.

- [ ] **Step 3: Run tests to verify missing session modules**

Run:

```bash
pnpm vitest run tests/unit/auth/session-rules.test.js tests/integration/auth/session.test.js
```

Expected: FAIL on unresolved session imports.

- [ ] **Step 4: Implement opaque database sessions**

Generate tokens with `randomBytes(32).toString('base64url')`, persist only SHA-256 digests, and update `last_active_at` no more than once every five minutes to avoid a write per request. Resolve sessions with a join to the user so suspension and role changes are observed immediately.

- [ ] **Step 5: Implement cookie helpers with injectable storage**

`setSessionCookie` must call:

```js
cookieStore.set(AUTH_CONFIG.sessionCookie, token, {
  httpOnly: true,
  secure: process.env.NODE_ENV === 'production',
  sameSite: 'lax',
  path: '/',
  expires: expiresAt,
});
```

`clearSessionCookie` sets the same cookie name and path with an expired date. Keeping `cookieStore` injectable makes cookie flags unit-testable without starting Next.js.

- [ ] **Step 6: Verify session behavior and commit**

```bash
pnpm vitest run tests/unit/auth/session-rules.test.js tests/integration/auth/session.test.js && pnpm lint
git add src/features/auth/session-repository.js src/features/auth/session-service.js tests/unit/auth/session-rules.test.js tests/integration/auth/session.test.js
git commit -m "feat: add revocable database sessions"
```

### Task 6: Add Audit Writing and SQLite-Backed Login Throttling

**Files:**
- Create: `src/features/audit/audit-writer.js`
- Create: `src/features/rate-limit/login-rate-limit.js`
- Create: `scripts/cleanup-auth-history.js`
- Create: `tests/integration/auth/rate-limit.test.js`
- Create: `tests/integration/auth/audit.test.js`

**Interfaces:**
- Consumes: `auditEvents`, `loginAttempts`, `digestIdentity`, and `AUTH_CONFIG`.
- Produces: `writeAudit(tx, event)`, `checkLoginLimit(db, identity)`, `recordLoginAttempt(db, attempt)`, and `deleteExpiredLoginAttempts(db, cutoff)`.

- [ ] **Step 1: Write failing throttle-window tests**

At a fixed clock, record five failed attempts for the same email/source digest and assert the next check returns `{ allowed: false, retryAfterSeconds: 900 }`. Assert four attempts remain allowed, a different source remains allowed, an attempt older than 15 minutes is ignored, and successful login removes the active failure window for that email/source pair.

- [ ] **Step 2: Write failing audit redaction tests**

Write login-success, login-failure, logout, password-change, temporary-password-issued, role-changed, account-suspended, account-reactivated, sessions-revoked, and authorization-blocked events. Assert required actor, target, type, outcome, and timestamp values are stored. Assert metadata serialization rejects keys named `password`, `temporaryPassword`, `token`, `cookie`, and `authorization`.

- [ ] **Step 3: Run tests to verify failure**

Run:

```bash
pnpm vitest run tests/integration/auth/rate-limit.test.js tests/integration/auth/audit.test.js
```

Expected: FAIL on unresolved modules.

- [ ] **Step 4: Implement rate limiting and append-only audit writing**

Query only the 15-minute window using indexed timestamps. Return the retry duration derived from the oldest active failed attempt. Write audit metadata through an allowlist serializer so secrets cannot enter JSON metadata.

- [ ] **Step 5: Implement bounded 30-day cleanup**

`scripts/cleanup-auth-history.js` deletes at most 5,000 `login_attempts` rows older than 30 days per invocation, exits nonzero on database failure, and prints only the deleted row count. Add npm script `auth:cleanup` for this command.

- [ ] **Step 6: Verify and commit**

```bash
pnpm vitest run tests/integration/auth/rate-limit.test.js tests/integration/auth/audit.test.js && pnpm lint
git add src/features/audit src/features/rate-limit scripts/cleanup-auth-history.js tests/integration/auth package.json
git commit -m "feat: audit authentication and throttle login attempts"
```

### Task 7: Implement Employee Account Administration

**Files:**
- Create: `src/features/users/user-repository.js`
- Create: `src/features/users/user-service.js`
- Create: `tests/unit/users/account-rules.test.js`
- Create: `tests/integration/users/user-service.test.js`
- Create: `scripts/bootstrap-admin.js`

**Interfaces:**
- Consumes: password hashing, user/session repositories, permission checks, and audit writer.
- Produces: `createEmployee(context, input)`, `issueTemporaryPassword(context, userId)`, `changeUserRole(context, userId, role)`, `suspendUser(context, userId)`, `reactivateUser(context, userId)`, and `bootstrapFirstAdmin(input)`.

- [ ] **Step 1: Write failing lifecycle and final-Admin unit tests**

Assert invited accounts require password change, suspended accounts cannot authenticate, and these operations fail with `ConflictError`:

```js
await expect(suspendUser(ctxWithOnlyAdmin, onlyAdmin.id)).rejects.toMatchObject({ code: 'FINAL_ADMIN' });
await expect(changeUserRole(ctxWithOnlyAdmin, onlyAdmin.id, 'content')).rejects.toMatchObject({ code: 'FINAL_ADMIN' });
```

Assert invalid roles are rejected before the repository is called.

- [ ] **Step 2: Write failing integration tests for Admin mutations**

Assert `createEmployee` normalizes email, creates `status='invited'`, sets `must_change_password=true`, returns a plaintext temporary password exactly once, and stores only its Argon2id hash. Assert duplicate normalized email fails clearly. Assert reset, role change, and suspension revoke all user sessions and append audit events in the same transaction.

- [ ] **Step 3: Run tests to verify failure**

Run:

```bash
pnpm vitest run tests/unit/users tests/integration/users
```

Expected: FAIL because user service modules do not exist.

- [ ] **Step 4: Implement repositories and transactional services**

Generate temporary passwords with `randomBytes(18).toString('base64url')`. Every public service accepts a context containing `{ db, actor, now }`, requires `users:manage`, and performs user mutation, session revocation, and audit insert inside one SQLite transaction.

`reactivateUser` leaves `must_change_password` unchanged. `issueTemporaryPassword` always sets it true. No service returns `password_hash`.

- [ ] **Step 5: Implement first-Admin bootstrap**

`scripts/bootstrap-admin.js` accepts email and display name from explicit CLI flags, reads the temporary password from a masked prompt, refuses to run when any Admin exists, hashes immediately, and prints the new user ID without echoing the password. Add npm script `auth:bootstrap-admin`.

- [ ] **Step 6: Verify and commit**

```bash
pnpm vitest run tests/unit/users tests/integration/users && pnpm lint
git add src/features/users tests/unit/users tests/integration/users scripts/bootstrap-admin.js package.json
git commit -m "feat: manage internal employee accounts"
```

### Task 8: Implement Login, Logout, Authorization, and Mandatory Password Change

**Files:**
- Create: `src/features/auth/login-service.js`
- Create: `src/features/auth/authorize.js`
- Create: `tests/integration/auth/login-service.test.js`
- Create: `tests/integration/auth/authorize.test.js`

**Interfaces:**
- Consumes: user repository, password verification, rate limiting, database sessions, permission map, and audit writer.
- Produces: `login(context, credentials)`, `logout(context)`, `changeMandatoryPassword(context, input)`, `changeOwnPassword(context, input)`, `loadCurrentActor(context)`, `requireUser(context)`, and `requirePermission(context, permission)`.

- [ ] **Step 1: Write failing generic-login tests**

For wrong password, unknown email, suspended account, and throttled request, assert the public result is exactly:

```js
{ ok: false, code: 'INVALID_CREDENTIALS', message: 'อีเมลหรือรหัสผ่านไม่ถูกต้อง กรุณาลองอีกครั้ง' }
```

Assert none of these results expose whether the account exists. Assert successful invited-user login creates only a restricted session; successful active-user login creates a full session.

- [ ] **Step 2: Write failing password-transition tests**

Assert a restricted session cannot call `requirePermission('dashboard:view')`. Assert valid mandatory password change updates the hash, clears `must_change_password`, changes invited status to active, revokes all sessions, and issues a new full session atomically. Two concurrent submissions produce one success and one expired-session result. Assert `changeOwnPassword` requires the current password, rejects the existing password as the replacement, revokes all sessions, and creates one replacement full session.

- [ ] **Step 3: Write failing authorization tests**

Assert missing, expired, revoked, and suspended sessions throw `AuthenticationError` and clear an invalid presented cookie. Assert authenticated users without the permission write an `authorization-blocked` audit event and throw `ForbiddenError`. Assert each allowed role/permission pair from Task 4 returns an actor object without `passwordHash` or `tokenDigest`.

- [ ] **Step 4: Run tests to verify missing implementation**

Run:

```bash
pnpm vitest run tests/integration/auth/login-service.test.js tests/integration/auth/authorize.test.js
```

Expected: FAIL on unresolved service imports.

- [ ] **Step 5: Implement login and authorization services**

Perform login in this order: normalize/validate input, derive identity digests, check limit, load user, verify password or a constant fallback hash, record attempt, write audit, and issue the appropriate session. Use a precomputed Argon2id fallback hash for unknown emails so unknown and wrong-password timing stay comparable. If the success audit insert fails, roll back session creation and return a correlated generic server error.

`requirePermission` always resolves current database state before calling `can(actor.role, permission)`. It never trusts a role carried only by client input or an unverified cookie payload. Missing, expired, revoked, or suspended-session resolution clears the presented invalid cookie; denied authenticated access appends an `authorization-blocked` audit event before throwing `ForbiddenError`.

- [ ] **Step 6: Implement logout and mandatory password rotation**

Logout revokes the presented database session before clearing the cookie and succeeds idempotently when the cookie is already missing. Mandatory and self-service password changes validate confirmation and policy, then update user, revoke sessions, write audit, and insert the replacement session inside one transaction before setting the replacement cookie. Self-service change additionally verifies the current password and rejects reuse of the current password.

- [ ] **Step 7: Run integration tests and commit**

```bash
pnpm vitest run tests/integration/auth && pnpm lint
git add src/features/auth tests/integration/auth
git commit -m "feat: authenticate and authorize employees"
```

### Task 9: Build the Login and Mandatory Password-Change Experience

**Files:**
- Create: `src/app/admin/login/page.js`
- Create: `src/app/admin/login/actions.js`
- Create: `src/app/admin/change-password/page.js`
- Create: `src/app/admin/change-password/actions.js`
- Create: `src/components/admin/login-form.js`
- Create: `src/components/admin/password-form.js`
- Create: `tests/unit/auth/login-actions.test.js`
- Create: `tests/unit/auth/password-actions.test.js`

**Interfaces:**
- Consumes: `login`, `logout`, `changeMandatoryPassword`, and Next.js `cookies()`.
- Produces: `/admin/login` and `/admin/change-password` routes with accessible Server Action forms.

- [ ] **Step 1: Write failing Server Action tests**

Mock the domain services and assert empty/invalid fields return field errors, every credential failure returns the approved generic message, successful restricted login redirects to `/admin/change-password`, and successful full login redirects to `/admin`.

Assert password confirmation mismatch does not call the service and clears both password values from returned state.
Assert a foreign or missing Origin is rejected before a state-changing service is called. Assert unexpected server failures return a correlation ID and log only redacted structured context.

- [ ] **Step 2: Run action tests to verify failure**

Run:

```bash
pnpm vitest run tests/unit/auth/login-actions.test.js tests/unit/auth/password-actions.test.js
```

Expected: FAIL because route actions do not exist.

- [ ] **Step 3: Implement Login Server Action and page**

Validate Origin against `APP_ORIGIN`, then validate FormData with Zod on the server. Preserve normalized email after validation failure but never return password. Render visible labels, `autocomplete="username"`, `autocomplete="current-password"`, an `aria-live="polite"` generic error region, and the guidance “ลืมรหัสผ่าน? ติดต่อผู้ดูแลระบบ”. Do not render registration or social-login controls. Unexpected failures display a generic message plus correlation ID and write only redacted structured logs.

- [ ] **Step 4: Implement mandatory password page and action**

The page resolves the session and redirects full sessions to `/admin`; missing sessions go to `/admin/login`. Restricted sessions see new-password and confirmation fields using `autocomplete="new-password"`, the four approved password rules, submit action, and logout. Do not render business navigation.

- [ ] **Step 5: Match the approved TPR10 Editorial Grid visual system**

Use Tailwind utilities backed by the Task 1 tokens: ivory background, slate text, oat brand panel, restrained orange slash, Georgia-class display copy, sans-serif form text, 8px controls, 16px surfaces, hairline borders, visible focus rings, no gradient, no glow, and no decorative animation required for comprehension.

- [ ] **Step 6: Verify UI actions and production build**

```bash
pnpm vitest run tests/unit/auth/login-actions.test.js tests/unit/auth/password-actions.test.js && pnpm lint && pnpm build
```

Expected: all commands exit 0 and both routes build.

- [ ] **Step 7: Commit**

```bash
git add src/app/admin/login src/app/admin/change-password src/components/admin/login-form.js src/components/admin/password-form.js tests/unit/auth
git commit -m "feat: add internal login and password onboarding"
```

### Task 10: Build Protected Admin Shell, Role Dashboards, and 403 Handling

**Files:**
- Create: `src/app/admin/(protected)/layout.js`
- Create: `src/app/admin/(protected)/page.js`
- Create: `src/app/admin/(protected)/content/page.js`
- Create: `src/app/admin/(protected)/leads/page.js`
- Create: `src/app/admin/(protected)/audit/page.js`
- Create: `src/app/admin/(protected)/profile/page.js`
- Create: `src/app/admin/(protected)/profile/actions.js`
- Create: `src/app/admin/forbidden/page.js`
- Create: `src/components/admin/admin-shell.js`
- Create: `tests/unit/auth/admin-shell.test.js`
- Create: `tests/integration/auth/protected-routes.test.js`

**Interfaces:**
- Consumes: `requireUser`, `requirePermission`, and `can`.
- Produces: authenticated `/admin`, role-aware navigation, and branded forbidden response.

- [ ] **Step 1: Write failing navigation tests**

Render `AdminShell` for each role. Assert Admin sees Dashboard, Content, Leads, Users, and Audit; Content sees only Dashboard and Content; Sales sees only Dashboard and Leads. All roles see Profile and Logout; Profile contains the self-service password form. Do not render links for domain routes that this plan does not create.

- [ ] **Step 2: Write failing protected-route integration tests**

Request the protected page handlers with missing and role-specific sessions. Assert missing session redirects to `/admin/login`; Content requesting `/admin/leads` and Sales requesting `/admin/content` produce 403 without invoking the protected repository; Admin may open `/admin/audit`; allowed requests invoke their repository once.

- [ ] **Step 3: Run tests to verify failure**

Run:

```bash
pnpm vitest run tests/unit/auth/admin-shell.test.js tests/integration/auth/protected-routes.test.js
```

Expected: FAIL because shell and protected routes do not exist.

- [ ] **Step 4: Implement protected layout and role dashboards**

The protected layout resolves a full session; restricted sessions redirect to `/admin/change-password`. Build navigation from permission predicates, not duplicated role lists. The dashboard displays content metrics for Content, lead metrics for Sales, and both plus user/audit summaries for Admin. `/admin/content` and `/admin/leads` are permission-guarded section-entry pages that describe the authorized scope without implementing domain CRUD; `/admin/audit` lists the authentication events implemented in this plan.

- [ ] **Step 5: Implement branded 403 handling**

Catch typed `ForbiddenError` at the protected route boundary or redirect to `/admin/forbidden`. The 403 page states that the signed-in account lacks access, reveals no resource identifiers, and links safely back to `/admin`.

- [ ] **Step 6: Implement active-user password change on Profile**

Render current password, new password, and confirmation fields with the approved password rules. The Server Action validates Origin, calls `changeOwnPassword`, never returns password values, shows one generic current-password error, and redirects to `/admin` with the rotated session after success. The shell logout action validates Origin before revoking the session.

- [ ] **Step 7: Verify and commit**

```bash
pnpm vitest run tests/unit/auth/admin-shell.test.js tests/integration/auth/protected-routes.test.js && pnpm lint && pnpm build
git add 'src/app/admin/(protected)' src/app/admin/forbidden src/components/admin/admin-shell.js tests/unit/auth/admin-shell.test.js tests/integration/auth/protected-routes.test.js
git commit -m "feat: protect role-aware admin workspace"
```

### Task 11: Build Admin User Management

**Files:**
- Create: `src/app/admin/(protected)/users/page.js`
- Create: `src/app/admin/(protected)/users/actions.js`
- Create: `src/components/admin/user-admin.js`
- Create: `tests/unit/users/user-actions.test.js`
- Create: `tests/integration/users/user-route.test.js`

**Interfaces:**
- Consumes: user administration services and `requirePermission('users:manage')`.
- Produces: Admin-only employee list, create, reset, role, suspend, and reactivate operations.

- [ ] **Step 1: Write failing action authorization tests**

For every mutation, assert Content and Sales callers receive forbidden without calling the user service. Assert a foreign or missing Origin is rejected before authorization or mutation. Assert Admin validation rejects malformed email, empty display name, unknown role, and attempts to suspend or demote the final Admin.

- [ ] **Step 2: Write failing successful-action tests**

Assert create/reset actions return the generated temporary password only in the immediate success state, do not include `passwordHash`, and replace it with a neutral success state on the next request. Assert role, suspend, and reactivate actions revalidate `/admin/users`.

- [ ] **Step 3: Run tests to verify failure**

Run:

```bash
pnpm vitest run tests/unit/users/user-actions.test.js tests/integration/users/user-route.test.js
```

Expected: FAIL because the user route and actions do not exist.

- [ ] **Step 4: Implement Admin-only page and actions**

The list shows display name, email, role, status, password-change requirement, last login, and active-session count. Create and reset dialogs clearly state that the one-time temporary password must be delivered through an approved internal channel. Require explicit confirmation for suspension and role change.

- [ ] **Step 5: Prevent temporary-password leakage**

Do not place temporary passwords in URLs, logs, analytics, toast persistence, localStorage, or database fields. Render the value from immediate Server Action state with a Copy button and a “ปิดแล้วจะไม่สามารถดูรหัสนี้ซ้ำได้” warning. Clear action state when the dialog closes.

- [ ] **Step 6: Verify and commit**

```bash
pnpm vitest run tests/unit/users tests/integration/users && pnpm lint && pnpm build
git add 'src/app/admin/(protected)/users' src/components/admin/user-admin.js tests/unit/users tests/integration/users
git commit -m "feat: add Admin employee management"
```

### Task 12: Add End-to-End Role, Revocation, and Accessibility Coverage

**Files:**
- Create: `tests/e2e/support/database.js`
- Create: `tests/e2e/support/auth.js`
- Create: `tests/e2e/internal-auth.spec.js`
- Create: `tests/e2e/role-access.spec.js`
- Modify: `playwright.config.js`

**Interfaces:**
- Consumes: completed internal auth application and isolated test database.
- Produces: deterministic browser-level acceptance coverage.

- [ ] **Step 1: Create deterministic E2E database helpers**

Implement `seedEmployee({ role, status, mustChangePassword, password })`, `suspendEmployee(id)`, and `changeEmployeeRole(id, role)` against a dedicated temporary SQLite database. Each test worker gets an isolated database and application port; helpers never connect to development or production paths.

- [ ] **Step 2: Implement onboarding and session E2E tests**

Cover this journey:

1. Admin logs in.
2. Admin creates a Content employee and sees the temporary password once.
3. Content logs in with it and is redirected to mandatory password change.
4. Direct `/admin` access redirects back until password change succeeds.
5. The old temporary password fails and the new password reaches the Content dashboard.
6. Logout returns to Login and the old session cookie no longer opens `/admin`.

- [ ] **Step 3: Implement the exact role-access E2E matrix**

Assert Content can open `/admin/content` and receives 403 for `/admin/leads`, `/admin/users`, and `/admin/audit`. Assert Sales can open `/admin/leads` and receives 403 for `/admin/content`, `/admin/users`, and `/admin/audit`. Assert Admin can open all four routes. Keep `media:manage` and `settings:manage` coverage in the exhaustive unit and integration permission tests because their domain pages are outside this plan.

- [ ] **Step 4: Implement immediate revocation E2E tests**

Sign in in one browser context, then use an Admin context to suspend the user, reset its password, and change its role in three independent tests. The signed-in context's next protected request must redirect to Login each time. Verify reactivation does not restore an old session. In a separate test, change the active user's own password, verify the current browser receives a rotated working session, and verify another signed-in browser is revoked.

- [ ] **Step 5: Implement keyboard and accessible-name checks**

Use `Tab` to traverse email, password, Login, password-change fields, logout, navigation, Admin dialogs, and 403 recovery in logical order. Assert visible focus and accessible names. Assert generic credential errors are announced through the live region and the password field is empty after failure.

- [ ] **Step 6: Run the complete browser suite and commit**

Run:

```bash
pnpm exec playwright install chromium
pnpm test:e2e
```

Expected: every onboarding, role, revocation, and accessibility scenario passes.

```bash
git add tests/e2e playwright.config.js
git commit -m "test: cover internal authentication journeys"
```

### Task 13: Perform Security Regression, Documentation, and Release Verification

**Files:**
- Create: `docs/runbooks/internal-auth.md`
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-08-22-tpr10-corporate-platform-design.md`
- Modify: `docs/superpowers/plans/2026-08-22-tpr10-phase1-implementation.md`

**Interfaces:**
- Consumes: all completed authentication modules and tests.
- Produces: operator instructions and consistent project documentation using only Admin, Content, and Sales roles.

- [ ] **Step 1: Update superseded role documentation**

Replace the old Admin/Editor/Approver/Sales matrix with Admin/Content/Sales. State explicitly that Content can publish. Update seeded-account examples, E2E role examples, navigation expectations, and file references so no executable instruction still creates `editor` or `approver` roles.

- [ ] **Step 2: Write the internal authentication runbook**

Document exact commands for environment setup, migrations, first-Admin bootstrap, employee creation, temporary-password reset, suspension/reactivation, sign-out-everywhere, 30-day login-attempt cleanup, audit inspection, Argon2 benchmark, development port 3001, production port 8001, and emergency recovery when the final Admin cannot sign in.

- [ ] **Step 3: Run secret and stale-role scans**

Run:

```bash
rg -n "password\s*=|SESSION_SECRET=.+|IDENTITY_DIGEST_SECRET=.+|BEGIN (RSA|OPENSSH) PRIVATE KEY" --glob '!pnpm-lock.yaml' .
rg -n "editor|approver" src tests scripts README.md docs/superpowers/specs/2026-08-22-tpr10-corporate-platform-design.md docs/superpowers/plans/2026-08-22-tpr10-phase1-implementation.md
```

Expected: the secret scan finds no committed values; the stale-role scan finds only historical migration commentary that cannot affect runtime behavior.

- [ ] **Step 4: Run the full verification suite**

Run:

```bash
pnpm test:unit && pnpm test:integration && pnpm test:e2e && pnpm lint && pnpm build
```

Expected: every command exits 0. The build includes `/admin/login`, `/admin/change-password`, `/admin`, `/admin/users`, and `/admin/forbidden`.

- [ ] **Step 5: Verify production cookie and revocation behavior manually**

Start the production build on port 8001 behind the configured HTTPS reverse proxy. Confirm the session cookie has `HttpOnly`, `Secure`, `SameSite=Lax`, and `Path=/`; no token appears in URLs or browser storage; logout invalidates the database row; and suspending a currently signed-in test employee blocks its next request.

- [ ] **Step 6: Commit release documentation**

```bash
git add README.md docs/runbooks/internal-auth.md docs/superpowers/specs/2026-08-22-tpr10-corporate-platform-design.md docs/superpowers/plans/2026-08-22-tpr10-phase1-implementation.md
git commit -m "docs: add internal authentication operations"
```
