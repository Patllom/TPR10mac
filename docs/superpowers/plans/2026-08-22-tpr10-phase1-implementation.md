# TPR-10 Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the bilingual TPR-10 corporate website, content administration, and RFQ workflow as a production-ready Next.js application for a company-owned server.

**Architecture:** Use a modular Next.js monolith with public and admin route groups, server-only domain services, Drizzle repositories over SQLite, and persistent filesystem media storage. Server actions and route handlers call domain services; no UI component accesses the database directly. Public pages read only published locale records, while admin operations enforce role permissions on the server and append audit events.

**Tech Stack:** Next.js App Router, TypeScript, React, CSS Modules, next-intl, Drizzle ORM, SQLite/better-sqlite3, Zod, Argon2, Vitest, Testing Library, Playwright, Nodemailer, Docker Compose, Caddy or the company's existing reverse proxy

**Spec:** `docs/superpowers/specs/2026-08-22-tpr10-corporate-platform-design.md`

## Global Constraints

- The application runs on port `3001` in development and port `8001` in production.
- Production is one application instance installed on a company-owned server.
- SQLite runs in WAL mode with a busy timeout and short transactions.
- Public content is available at explicit `/th/...` and `/en/...` URLs; unpublished translations return 404 and never silently fall back.
- The visual direction is Technical Grid: dark charcoal/black, TPR-10 orange, strong typography, restrained motion, and responsive layouts.
- All protected actions enforce authorization on the server.
- Uploaded files and the SQLite database live in explicit persistent paths configured by environment variables.
- Public traffic reaches the application through HTTPS on a reverse proxy; production port `8001` is not exposed directly to the public internet.
- Phase 1 excludes customer accounts, telemetry ingestion, operational dashboards, AI assistants, full CRM automation, and multi-instance deployment.

## Planned File Structure

```text
src/
  app/
    [locale]/(public)/             localized public routes and layouts
    admin/                         authenticated administration routes
    api/health/route.ts            deployment health check
    api/media/[id]/route.ts        authorized/public media delivery
  components/public/               Technical Grid presentation components
  components/admin/                admin navigation, tables, forms, status UI
  features/auth/                   credentials, sessions, role authorization
  features/content/                content types, publication, localization
  features/leads/                  RFQ validation, persistence, workflow
  features/media/                  safe file validation and filesystem storage
  features/notifications/          notification adapter and retry state
  features/audit/                  immutable audit event writer
  db/schema/                       Drizzle table definitions by domain
  db/client.ts                     SQLite initialization and pragmas
  i18n/                            locale routing and interface messages
  lib/                             environment, result, logging, security helpers
tests/
  unit/                            pure rule and validation tests
  integration/                     SQLite/service/route integration tests
  e2e/                             Playwright public and admin journeys
drizzle/                           generated SQL migrations
scripts/                           seed, backup, restore, and bootstrap scripts
public/                            immutable public brand assets only
deploy/                            Docker, Compose, health check, proxy example
```

---

### Task 1: Bootstrap the Next.js Application and Quality Tooling

**Files:**
- Create: `package.json`
- Create: `next.config.ts`
- Create: `tsconfig.json`
- Create: `vitest.config.ts`
- Create: `playwright.config.ts`
- Create: `src/app/layout.tsx`
- Create: `src/app/globals.css`
- Create: `src/app/page.tsx`
- Create: `tests/unit/smoke.test.ts`
- Modify: `.gitignore`

**Interfaces:**
- Produces: npm scripts `dev`, `build`, `start`, `lint`, `typecheck`, `test`, `test:integration`, and `test:e2e`.
- Produces: CSS design tokens used by every later public and admin component.

- [ ] **Step 1: Scaffold the application and pin dependencies**

Run:

```bash
scaffold_dir="$(mktemp -d)"
pnpm create next-app@latest "$scaffold_dir" --ts --eslint --app --src-dir --use-pnpm --no-tailwind --import-alias '@/*'
rsync -a --exclude='.git' --exclude='.gitignore' "$scaffold_dir"/ ./
rm -rf "$scaffold_dir"
pnpm add next-intl drizzle-orm better-sqlite3 zod argon2 nodemailer
pnpm add -D drizzle-kit vitest @vitejs/plugin-react jsdom @testing-library/react @testing-library/jest-dom @playwright/test @types/better-sqlite3 @types/nodemailer
```

Keep the existing `Doc/` and `docs/` directories. Resolve scaffold prompts so the package is named `tpr10-platform`.

- [ ] **Step 2: Write the failing script and token smoke test**

Create `tests/unit/smoke.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { APP_PORTS, LOCALES } from '@/lib/constants';

describe('application constants', () => {
  it('locks approved ports and locales', () => {
    expect(APP_PORTS).toEqual({ development: 3001, production: 8001 });
    expect(LOCALES).toEqual(['th', 'en']);
  });
});
```

- [ ] **Step 3: Run the test and verify the missing module failure**

Run: `pnpm vitest run tests/unit/smoke.test.ts`

Expected: FAIL because `@/lib/constants` does not exist.

- [ ] **Step 4: Add constants, scripts, and design tokens**

Create `src/lib/constants.ts`:

```ts
export const APP_PORTS = { development: 3001, production: 8001 } as const;
export const LOCALES = ['th', 'en'] as const;
export type Locale = (typeof LOCALES)[number];
```

Set scripts in `package.json`:

```json
{
  "scripts": {
    "dev": "next dev -p 3001",
    "build": "next build",
    "start": "next start -p 8001",
    "lint": "eslint .",
    "typecheck": "tsc --noEmit",
    "test": "vitest run",
    "test:integration": "vitest run tests/integration",
    "test:e2e": "playwright test"
  }
}
```

Add `--bg`, `--surface`, `--surface-raised`, `--text`, `--muted`, `--line`, `--brand-orange`, `--focus`, spacing, radius, and type-scale tokens to `src/app/globals.css`. Use `#ff5b1c` for the brand orange and ensure the body defaults to the approved dark surface.

- [ ] **Step 5: Verify the foundation**

Run: `pnpm test && pnpm lint && pnpm typecheck && pnpm build`

Expected: all commands exit 0 and the build reports the root route.

- [ ] **Step 6: Commit**

```bash
git add package.json pnpm-lock.yaml next.config.ts tsconfig.json vitest.config.ts playwright.config.ts src tests .gitignore
git commit -m "chore: bootstrap TPR-10 Next.js platform"
```

### Task 2: Environment Validation and SQLite Foundation

**Files:**
- Create: `src/lib/env.ts`
- Create: `src/db/client.ts`
- Create: `src/db/schema/users.ts`
- Create: `src/db/schema/content.ts`
- Create: `src/db/schema/leads.ts`
- Create: `src/db/schema/audit.ts`
- Create: `src/db/schema/index.ts`
- Create: `drizzle.config.ts`
- Create: `.env.example`
- Create: `tests/integration/db/client.test.ts`

**Interfaces:**
- Produces: `env` with `DATABASE_PATH`, `UPLOAD_DIR`, `SESSION_SECRET`, `APP_ORIGIN`, `SMTP_*` values.
- Produces: `getDb(): AppDatabase` and Drizzle schema exports used by all repositories.

- [ ] **Step 1: Write failing SQLite initialization tests**

Create `tests/integration/db/client.test.ts` that creates a temporary database and asserts:

```ts
const modes = database.pragma('journal_mode') as Array<{ journal_mode: string }>;
expect(modes[0].journal_mode.toLowerCase()).toBe('wal');
expect(database.pragma('busy_timeout', { simple: true })).toBe(5000);
```

Also assert that invalid or relative production database paths are rejected by the environment schema.

- [ ] **Step 2: Run the database test and verify failure**

Run: `pnpm vitest run tests/integration/db/client.test.ts`

Expected: FAIL because `src/db/client.ts` and `src/lib/env.ts` do not exist.

- [ ] **Step 3: Implement environment parsing and database initialization**

Implement `createDatabase(path: string)` in `src/db/client.ts`:

```ts
export function createDatabase(path: string) {
  const sqlite = new Database(path);
  sqlite.pragma('journal_mode = WAL');
  sqlite.pragma('busy_timeout = 5000');
  sqlite.pragma('foreign_keys = ON');
  return { sqlite, db: drizzle(sqlite, { schema }) };
}
```

Define UUID text primary keys, integer timestamps, foreign keys, unique slugs per locale, and indexes for publication state, lead status, owner, and event timestamps. Use separate locale records under one shared content identity.

- [ ] **Step 4: Generate the initial migration and verify schema**

Run:

```bash
pnpm drizzle-kit generate
pnpm vitest run tests/integration/db/client.test.ts
pnpm typecheck
```

Expected: migration generated; tests and typecheck pass.

- [ ] **Step 5: Commit**

```bash
git add src/lib/env.ts src/db drizzle drizzle.config.ts .env.example tests/integration/db
git commit -m "feat: add SQLite schema and environment validation"
```

### Task 3: Credentials, Sessions, and Server-Side Roles

**Files:**
- Create: `src/features/auth/types.ts`
- Create: `src/features/auth/password.ts`
- Create: `src/features/auth/session.ts`
- Create: `src/features/auth/authorize.ts`
- Create: `src/features/auth/repository.ts`
- Create: `src/app/admin/login/page.tsx`
- Create: `src/app/admin/login/actions.ts`
- Create: `src/app/admin/layout.tsx`
- Create: `tests/unit/auth/authorize.test.ts`
- Create: `tests/integration/auth/session.test.ts`

**Interfaces:**
- Produces: `Role = 'admin' | 'editor' | 'approver' | 'sales'`.
- Produces: `requireUser(): Promise<AuthUser>` and `requireRole(...roles: Role[]): Promise<AuthUser>`.
- Produces: `createSession(userId: string)` and `deleteCurrentSession()`.

- [ ] **Step 1: Write authorization and session failure tests**

Cover this exact matrix in `authorize.test.ts`:

```ts
expect(can('admin', 'users:manage')).toBe(true);
expect(can('editor', 'content:edit')).toBe(true);
expect(can('editor', 'content:publish')).toBe(false);
expect(can('approver', 'content:publish')).toBe(true);
expect(can('sales', 'leads:update')).toBe(true);
expect(can('sales', 'content:edit')).toBe(false);
```

In the integration test, verify expired, revoked, and unknown session tokens are rejected and valid tokens return the correct user.

- [ ] **Step 2: Run tests and verify missing implementation failures**

Run: `pnpm vitest run tests/unit/auth tests/integration/auth`

Expected: FAIL on unresolved auth imports.

- [ ] **Step 3: Implement password, session, and authorization services**

Use Argon2id for passwords. Store only a SHA-256 digest of a random 32-byte session token, set the raw token in a secure HTTP-only same-site cookie, and rotate the token after login. Implement permissions as an explicit readonly map in `authorize.ts`; route handlers and server actions call `requireRole` before loading protected data.

- [ ] **Step 4: Implement login and protected admin layout**

Validate credentials with Zod, return a generic invalid-credentials response, rate-limit by normalized username plus source address, and redirect authenticated users to `/admin`. The admin layout calls `requireUser()` before rendering navigation.

- [ ] **Step 5: Run auth and regression checks**

Run: `pnpm vitest run tests/unit/auth tests/integration/auth && pnpm lint && pnpm typecheck`

Expected: all commands exit 0.

- [ ] **Step 6: Commit**

```bash
git add src/features/auth src/app/admin tests/unit/auth tests/integration/auth
git commit -m "feat: add secure admin authentication and roles"
```

### Task 4: Bilingual Routing and Published Content Service

**Files:**
- Create: `src/i18n/routing.ts`
- Create: `src/i18n/request.ts`
- Create: `src/middleware.ts`
- Create: `messages/th.json`
- Create: `messages/en.json`
- Create: `src/features/content/types.ts`
- Create: `src/features/content/repository.ts`
- Create: `src/features/content/service.ts`
- Create: `src/app/[locale]/(public)/layout.tsx`
- Create: `src/app/[locale]/(public)/[...slug]/page.tsx`
- Create: `tests/unit/content/publication.test.ts`
- Create: `tests/integration/content/published-content.test.ts`

**Interfaces:**
- Produces: `getPublishedPage(locale: Locale, slug: string[]): Promise<PublishedPage | null>`.
- Produces: `listPublishedProjects(locale: Locale, limit?: number): Promise<ProjectSummary[]>`.
- Consumes: shared content and locale tables from Task 2.

- [ ] **Step 1: Write failing locale and publication tests**

Test that a Thai published record is returned for `/th`, an unpublished English translation returns `null`, a missing locale is rejected, and slugs are unique only within their locale.

- [ ] **Step 2: Verify tests fail before implementation**

Run: `pnpm vitest run tests/unit/content tests/integration/content`

Expected: FAIL because content services do not exist.

- [ ] **Step 3: Implement strict localized content reads**

Implement `getPublishedPage` with a query requiring the requested locale, `status = 'published'`, and `publishedAt <= now`. Do not query another locale as fallback. Call `notFound()` when the service returns `null`.

- [ ] **Step 4: Add locale routing and metadata**

Use next-intl for interface strings. Generate canonical and alternate-language metadata only for published variants. Add locale-aware Open Graph data and preserve locale during internal navigation.

- [ ] **Step 5: Verify localized behavior**

Run: `pnpm vitest run tests/unit/content tests/integration/content && pnpm build`

Expected: all tests pass and Next.js builds `/th` and `/en` route handling.

- [ ] **Step 6: Commit**

```bash
git add src/i18n src/middleware.ts messages src/features/content src/app/'[locale]' tests/unit/content tests/integration/content
git commit -m "feat: add strict bilingual content routing"
```

### Task 5: Technical Grid Public Website

**Files:**
- Create: `src/components/public/site-header.tsx`
- Create: `src/components/public/site-footer.tsx`
- Create: `src/components/public/hero.tsx`
- Create: `src/components/public/capability-grid.tsx`
- Create: `src/components/public/project-grid.tsx`
- Create: `src/components/public/contact-cta.tsx`
- Create: `src/components/public/public.module.css`
- Create: `src/app/[locale]/(public)/page.tsx`
- Create: public pages for `solutions`, `industries`, `projects`, `about`, `insights`, and `contact`
- Create: `tests/unit/public/site-header.test.tsx`
- Create: `tests/e2e/public-navigation.spec.ts`

**Interfaces:**
- Consumes: `getPublishedPage` and `listPublishedProjects` from Task 4.
- Produces: reusable presentation components with locale-preserving links and no database dependency.

- [ ] **Step 1: Write navigation and reduced-motion tests**

Assert that the header exposes all seven approved navigation destinations, the language switcher maps the current path to the alternate locale, and decorative motion is disabled within `@media (prefers-reduced-motion: reduce)`.

- [ ] **Step 2: Run component tests and verify failure**

Run: `pnpm vitest run tests/unit/public/site-header.test.tsx`

Expected: FAIL because public components are absent.

- [ ] **Step 3: Implement the approved homepage composition**

Build the sections in this order: proposition, dual calls to action, trust indicators, three capabilities, selected projects, TPR-10 proof points, and consultation CTA. Use semantic landmarks, one `h1`, visible focus, responsive images, and CSS-only decorative grids. Copy comes from localized content records rather than hard-coded page paragraphs.

- [ ] **Step 4: Implement remaining public templates**

Each service page renders overview, capabilities, use cases, delivery approach, projects, and contact CTA. Each project renders sector, challenge, approach, scope, outcome, media, and related solutions while respecting anonymization fields.

- [ ] **Step 5: Run component, browser, and build checks**

Run:

```bash
pnpm vitest run tests/unit/public
pnpm build
pnpm exec playwright test tests/e2e/public-navigation.spec.ts --project=chromium
```

Expected: tests pass at desktop and mobile viewports, both locales navigate without broken links, and the build exits 0.

- [ ] **Step 6: Commit**

```bash
git add src/components/public src/app/'[locale]' tests/unit/public tests/e2e/public-navigation.spec.ts
git commit -m "feat: build bilingual Technical Grid website"
```

### Task 6: Content Administration and Publishing Workflow

**Files:**
- Create: `src/features/content/admin-schema.ts`
- Create: `src/features/content/actions.ts`
- Create: `src/features/content/publication.ts`
- Create: `src/features/audit/service.ts`
- Create: `src/app/admin/content/page.tsx`
- Create: `src/app/admin/content/[id]/page.tsx`
- Create: `src/components/admin/content-editor.tsx`
- Create: `src/components/admin/locale-status.tsx`
- Create: `tests/unit/content/workflow.test.ts`
- Create: `tests/integration/content/publish.test.ts`

**Interfaces:**
- Produces: `saveDraft(input, actor)`, `submitForReview(id, locale, actor)`, `publishContent(id, locale, actor)`, and `unpublishContent(id, locale, actor)`.
- Consumes: `requireRole` and audit/database interfaces from Tasks 2 and 3.

- [ ] **Step 1: Write failing workflow tests**

Test allowed transitions `draft -> review -> published`, reject `draft -> published` for Editor, allow Approver publication, create an immutable revision on publication, and record actor, locale, content ID, and timestamp in the audit event.

- [ ] **Step 2: Verify workflow tests fail**

Run: `pnpm vitest run tests/unit/content/workflow.test.ts tests/integration/content/publish.test.ts`

Expected: FAIL on missing workflow functions.

- [ ] **Step 3: Implement transactional publication**

Within one short SQLite transaction: validate current state, insert revision snapshot, update active localized record, and append the audit event. After commit, revalidate only the affected public paths.

- [ ] **Step 4: Build content list and bilingual editor**

Show independent TH/EN state, translation completeness, updated time, author, and next permitted action. Preserve unsaved form values on validation errors and expose draft preview only through an authenticated, short-lived preview token.

- [ ] **Step 5: Verify workflow and permissions**

Run: `pnpm vitest run tests/unit/content tests/integration/content && pnpm typecheck && pnpm lint`

Expected: all commands exit 0.

- [ ] **Step 6: Commit**

```bash
git add src/features/content src/features/audit src/app/admin/content src/components/admin tests/unit/content tests/integration/content
git commit -m "feat: add bilingual content publishing workflow"
```

### Task 7: Secure Media Library

**Files:**
- Create: `src/features/media/types.ts`
- Create: `src/features/media/validation.ts`
- Create: `src/features/media/storage.ts`
- Create: `src/features/media/actions.ts`
- Create: `src/app/admin/media/page.tsx`
- Create: `src/app/api/media/[id]/route.ts`
- Create: `tests/unit/media/validation.test.ts`
- Create: `tests/integration/media/storage.test.ts`

**Interfaces:**
- Produces: `storeMedia(file, actor): Promise<MediaAsset>` and `openMedia(id, access): Promise<ReadableStream>`.
- Consumes: `UPLOAD_DIR`, role authorization, media table, and audit writer.

- [ ] **Step 1: Write failing file-validation tests**

Accept configured JPEG, PNG, WebP, and PDF signatures within size limits. Reject extension/signature mismatches, executable formats, path traversal names, empty files, and files exceeding the configured maximum.

- [ ] **Step 2: Run tests and verify failure**

Run: `pnpm vitest run tests/unit/media tests/integration/media`

Expected: FAIL because validation and storage are missing.

- [ ] **Step 3: Implement safe persistent storage**

Generate server-side UUID filenames, retain the sanitized display name as metadata, write to a temporary file in the upload volume, fsync, then rename atomically. Never concatenate a user filename into a filesystem path.

- [ ] **Step 4: Implement media administration and delivery**

Require Editor or higher to upload. Public media delivery resolves only published references; admin previews require authentication. Set explicit content type, download disposition for PDFs, caching headers, and `X-Content-Type-Options: nosniff`.

- [ ] **Step 5: Verify media behavior**

Run: `pnpm vitest run tests/unit/media tests/integration/media && pnpm lint && pnpm typecheck`

Expected: all commands exit 0.

- [ ] **Step 6: Commit**

```bash
git add src/features/media src/app/admin/media src/app/api/media tests/unit/media tests/integration/media
git commit -m "feat: add secure persistent media library"
```

### Task 8: RFQ Intake, Lead Workflow, and Notifications

**Files:**
- Create: `src/features/leads/schema.ts`
- Create: `src/features/leads/service.ts`
- Create: `src/features/leads/repository.ts`
- Create: `src/features/leads/rate-limit.ts`
- Create: `src/features/notifications/types.ts`
- Create: `src/features/notifications/smtp.ts`
- Create: `src/features/notifications/service.ts`
- Create: `src/app/[locale]/(public)/contact/actions.ts`
- Create: `src/components/public/rfq-form.tsx`
- Create: `src/app/admin/leads/page.tsx`
- Create: `src/app/admin/leads/[id]/page.tsx`
- Create: `tests/unit/leads/schema.test.ts`
- Create: `tests/integration/leads/create-lead.test.ts`
- Create: `tests/e2e/rfq.spec.ts`

**Interfaces:**
- Produces: `createLead(input, requestContext): Promise<{ reference: string }>`.
- Produces: `updateLeadStatus(id, status, actor)` and `assignLead(id, ownerId, actor)`.
- Produces: `NotificationAdapter.send(message): Promise<NotificationResult>`.

- [ ] **Step 1: Write failing validation and persistence tests**

Test required consent, normalized email/phone, bounded descriptions, allowed service categories, attachment validation, honeypot rejection, rate-limit rejection, unique human-readable references, and atomic persistence of the lead plus initial activity.

- [ ] **Step 2: Run lead tests and verify failure**

Run: `pnpm vitest run tests/unit/leads tests/integration/leads`

Expected: FAIL on missing lead modules.

- [ ] **Step 3: Implement reliable lead creation**

Validate again on the server. Within one short transaction create the lead and initial activity, then commit. Attempt notification after persistence; store notification state as `pending`, `sent`, or `failed`. Return success with the reference even when SMTP fails after storage.

- [ ] **Step 4: Build the public RFQ form and admin lead screens**

The form retains non-file values after validation errors, explains upload limits, and shows the saved reference on success. The admin list supports approved statuses, owner assignment, internal notes, chronological activity, and role checks that allow only Admin and Sales to access lead details.

- [ ] **Step 5: Run lead and browser tests**

Run:

```bash
pnpm vitest run tests/unit/leads tests/integration/leads
pnpm exec playwright test tests/e2e/rfq.spec.ts --project=chromium
```

Expected: valid RFQ reaches admin with a reference; invalid, spam, and rate-limited cases show localized messages; simulated SMTP failure does not lose the lead.

- [ ] **Step 6: Commit**

```bash
git add src/features/leads src/features/notifications src/app/'[locale]' src/app/admin/leads src/components/public/rfq-form.tsx tests/unit/leads tests/integration/leads tests/e2e/rfq.spec.ts
git commit -m "feat: add reliable RFQ and lead workflow"
```

### Task 9: Dashboard, SEO, Sitemap, and Redirects

**Files:**
- Create: `src/features/content/seo.ts`
- Create: `src/features/content/redirects.ts`
- Create: `src/app/sitemap.ts`
- Create: `src/app/robots.ts`
- Create: `src/app/admin/page.tsx`
- Create: `src/app/admin/seo/page.tsx`
- Create: `src/components/admin/dashboard-summary.tsx`
- Create: `tests/unit/content/seo.test.ts`
- Create: `tests/integration/dashboard/summary.test.ts`

**Interfaces:**
- Produces: `buildPageMetadata(page, locale)` and `resolveRedirect(locale, slug)`.
- Produces: `getDashboardSummary(user): Promise<DashboardSummary>` filtered by permissions.

- [ ] **Step 1: Write failing SEO and dashboard tests**

Assert canonical URLs, published alternate-language URLs only, social metadata, organization/service structured data, redirect chains collapsed to one hop, and a dashboard response that omits lead metrics for Editor/Approver roles.

- [ ] **Step 2: Run tests and verify failure**

Run: `pnpm vitest run tests/unit/content/seo.test.ts tests/integration/dashboard/summary.test.ts`

Expected: FAIL because metadata and summary services are absent.

- [ ] **Step 3: Implement SEO and redirect services**

Generate sitemap entries only for published locale variants. Validate redirects against loops and prevent a redirect target from pointing to an unpublished page. Emit JSON-LD from typed objects serialized by React, not raw untrusted markup.

- [ ] **Step 4: Implement the approved admin dashboard**

Show new RFQs, leads requiring follow-up, content awaiting review, incomplete translations, and recent publication activity. Query only aggregates needed by the current user's permissions.

- [ ] **Step 5: Verify metadata and dashboard behavior**

Run: `pnpm vitest run tests/unit/content tests/integration/dashboard && pnpm build`

Expected: all tests pass and sitemap/robots routes build successfully.

- [ ] **Step 6: Commit**

```bash
git add src/features/content src/app/sitemap.ts src/app/robots.ts src/app/admin src/components/admin tests/unit/content tests/integration/dashboard
git commit -m "feat: add admin dashboard and SEO controls"
```

### Task 10: Production Deployment, Health Checks, and Backup/Restore

**Files:**
- Create: `Dockerfile`
- Create: `deploy/compose.yml`
- Create: `deploy/Caddyfile.example`
- Create: `src/app/api/health/route.ts`
- Create: `scripts/backup.sh`
- Create: `scripts/restore.sh`
- Create: `scripts/bootstrap-admin.ts`
- Create: `tests/integration/health/route.test.ts`
- Create: `tests/integration/operations/backup-restore.test.ts`
- Modify: `.env.example`
- Modify: `README.md`

**Interfaces:**
- Produces: unauthenticated `/api/health` with non-sensitive application, database, and upload-volume status.
- Produces: `backup.sh <destination>` and `restore.sh <backup-directory>` with explicit validated paths.

- [ ] **Step 1: Write failing health and restore tests**

Assert health returns 200 only when SQLite is readable and the upload directory is writable, otherwise 503 with component names but no paths or secrets. Create a fixture database/media set, back it up, restore it into empty explicit paths, and compare row counts plus SHA-256 file hashes.

- [ ] **Step 2: Run operational tests and verify failure**

Run: `pnpm vitest run tests/integration/health tests/integration/operations`

Expected: FAIL because health and scripts are absent.

- [ ] **Step 3: Implement container and Compose configuration**

Use a multi-stage non-root image. Bind the app to port `8001`, mount explicit database and upload volumes, add a health check, set restart policy, and do not publish SQLite or upload volumes through another service. The proxy example forwards HTTPS traffic to the application inside the deployment network.

- [ ] **Step 4: Implement safe backup, restore, and bootstrap operations**

Use SQLite `.backup` or the runtime backup API, checkpoint WAL safely, copy media with checksums, and write a manifest. Restore refuses non-empty targets unless an explicit `--replace` flag is supplied and validates the manifest before changing active paths. `bootstrap-admin.ts` refuses to run when an Admin already exists.

- [ ] **Step 5: Verify production image and recovery**

Run:

```bash
pnpm vitest run tests/integration/health tests/integration/operations
docker compose -f deploy/compose.yml build
docker compose -f deploy/compose.yml up -d
curl --fail http://127.0.0.1:8001/api/health
docker compose -f deploy/compose.yml down
```

Expected: tests pass, image builds, health returns 200, and containers stop cleanly without deleting persistent volumes.

- [ ] **Step 6: Commit**

```bash
git add Dockerfile deploy scripts src/app/api/health tests/integration/health tests/integration/operations .env.example README.md
git commit -m "ops: add production deployment and recovery workflow"
```

### Task 11: End-to-End Security, Accessibility, and Release Acceptance

**Files:**
- Create: `tests/e2e/admin-roles.spec.ts`
- Create: `tests/e2e/content-publishing.spec.ts`
- Create: `tests/e2e/accessibility.spec.ts`
- Create: `tests/e2e/security.spec.ts`
- Create: `tests/e2e/production-ports.spec.ts`
- Create: `tests/e2e/support/auth.ts`
- Create: `tests/e2e/support/fixtures.ts`
- Create: `docs/operations/release-checklist.md`
- Create: `docs/operations/backup-restore.md`
- Create: `docs/operations/deployment.md`

**Interfaces:**
- Consumes: the complete Phase 1 application.
- Produces: executable acceptance coverage and operator-facing release procedures.

- [ ] **Step 1: Write acceptance tests for the approved behavior**

Create `tests/e2e/support/auth.ts` with `loginAs(page, role)` that signs in seeded accounts through `/admin/login`, and create `tests/e2e/support/fixtures.ts` with `seedContentState()` and `seedLeadState()` helpers that insert deterministic fixture rows through the test database connection.

Implement the role-publication case as:

```ts
test('editor cannot publish but approver can', async ({ page }) => {
  const content = await seedContentState({ th: 'review', en: 'draft' });
  await loginAs(page, 'editor');
  await page.goto(`/admin/content/${content.id}`);
  await expect(page.getByRole('button', { name: 'เผยแพร่' })).toHaveCount(0);
  await page.getByRole('button', { name: 'ออกจากระบบ' }).click();
  await loginAs(page, 'approver');
  await page.goto(`/admin/content/${content.id}`);
  await page.getByRole('button', { name: 'เผยแพร่' }).click();
  await expect(page.getByText('เผยแพร่แล้ว')).toBeVisible();
});
```

Add concrete tests that request the seeded unpublished English URL and expect 404 while the Thai URL returns 200; sign in as Sales and expect `/admin/content` to return the forbidden screen; inject a notification adapter that returns `{ ok: false, retryable: true }`, submit an RFQ, and verify its reference in `/admin/leads`; and press `Tab` through the public header and RFQ form while asserting each expected accessible name and visible focus indicator in order.

- [ ] **Step 2: Run the acceptance suite and capture genuine failures**

Run: `pnpm test:e2e`

Expected: tests expose any remaining permission, localization, responsive, focus, or deployment mismatch before release.

- [ ] **Step 3: Fix only failures required by the acceptance criteria**

For each failure, add the smallest unit or integration regression test, implement the correction in the owning feature module, then rerun the focused test before returning to the full suite.

- [ ] **Step 4: Write executable operator documentation**

Document exact commands for environment setup, migration, first Admin creation, build, start on port `8001`, proxy verification, backup, restore rehearsal, log access, and rollback. Include expected success output and explicit persistent paths from the deployment environment.

- [ ] **Step 5: Run the complete verification gate**

Run:

```bash
pnpm lint
pnpm typecheck
pnpm test
pnpm test:integration
pnpm build
pnpm test:e2e
docker compose -f deploy/compose.yml config
git status --short
```

Expected: every quality command exits 0, Compose configuration is valid, and `git status --short` contains no unintended files.

- [ ] **Step 6: Commit**

```bash
git add tests/e2e docs/operations src
git commit -m "test: complete Phase 1 release acceptance"
```

## Delivery Checkpoint

At the end of Task 11, compare the running system with every acceptance criterion in the linked design specification. Record the production image identifier, applied migration identifier, backup rehearsal timestamp, and final verification commands in the release notes before any production deployment.
