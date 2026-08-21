# TPR-10 Corporate Website and Admin Platform Design

Date: 2026-08-22  
Status: Approved design draft, awaiting written-spec review

## 1. Objective

Build a bilingual corporate website and administration platform for TPR-10 Co., Ltd. The public website must establish trust, demonstrate relevant project experience, and convert visitors into consultation or quotation requests. The system must be simple enough to deliver in phases and structured so later sales, client portal, telemetry, and AI capabilities can be added without rebuilding the public website.

The first release will be installed on a company-owned server, developed with Next.js, use SQLite, run on port 3001 in development, and run on port 8001 in production.

## 2. Source Material and Brand Position

The source Company Profile presents TPR-10 as an end-to-end technology provider for government agencies, industrial clients, and private organizations. Its services fall into three principal groups:

1. Water Resources and Telemetering Support
2. Digital Solutions and AI
3. Infrastructure, Network, and Security

The website will give equal weight to public-sector and industrial/private-sector audiences. TPR-10 will be positioned as a technology partner for critical operational systems rather than as a supplier focused on only one industry.

## 3. Experience and Visual Direction

The approved visual direction is **Technical Grid**:

- Dark charcoal and black surfaces with the existing orange brand accent
- Strong typography, precise grid alignment, and restrained engineering motifs
- Generous empty space and selective light/motion to create a mysterious, premium feel
- Minimal content per viewport with progressive disclosure instead of dense brochure-style layouts
- Motion limited to subtle grid, signal, hover, and section-transition effects
- Respect for `prefers-reduced-motion`
- Responsive layouts for mobile, tablet, and desktop

The website will be available in Thai and English through a visible language switcher. Localized URLs will use `/th/...` and `/en/...` so each language can be indexed and shared independently.

## 4. Information Architecture

The public navigation will contain:

- Home
- Solutions
- Industries
- Projects
- About
- Insights
- Contact / Request for Quotation

Every public content page will have Thai and English variants. A missing translation remains unpublished in that locale and must not silently fall back to the other language on an indexable URL.

### Home page sequence

1. Brand proposition and two calls to action: consultation and solutions
2. Trust indicators serving both government and industry
3. Three core capability groups
4. Selected projects with problem, solution, and outcome framing
5. Reasons to choose TPR-10
6. Consultation / RFQ call to action

### Solution structure

Each solution page includes an overview, relevant capabilities, typical use cases, delivery approach, related projects, and a contextual consultation call to action.

### Project structure

Each project contains sector, service category, project summary, client disclosure level, challenge, approach, delivered scope, outcome, images, related solutions, and publication status. The system must support anonymized projects when the client cannot be named.

## 5. Phase Plan

### Phase 1 - Corporate Website and CMS

- Bilingual public website
- Services, industries, projects, company, insights, and contact pages
- Content management and media library
- RFQ and consultation forms
- Lead inbox and basic ownership/status workflow
- SEO controls, redirects, sitemap, and structured metadata
- User roles, review/publish workflow, and audit log
- Company-server deployment, monitoring, and backup/restore procedure

### Phase 2 - Sales and Operations

- Expanded lead pipeline and assignment rules
- Follow-up activities, reminders, and email notifications
- Operational reports and exports
- Workflow automation and integrations
- PostgreSQL migration if concurrency or automation requirements exceed SQLite's operating envelope

### Phase 3 - Client Portal

- Customer authentication and organization-based access
- Project status, documents, maintenance records, and support tickets
- Fine-grained permissions and customer activity history

### Phase 4 - Intelligence

- Operational and telemetry dashboards
- Organization knowledge search
- AI assistance constrained by user permissions and approved data sources

Each phase is independently releasable. Phase 1 will not implement placeholder portal, telemetry, or AI interfaces that have no working backend.

## 6. Phase 1 System Architecture

Phase 1 uses a modular Next.js application with clear internal boundaries:

- Public web module for localized, SEO-friendly pages
- Admin module for content, media, leads, users, settings, and audit history
- Authentication and role-authorization module
- Content service and publication workflow
- Lead/RFQ service
- Media service using server filesystem storage
- Notification service with an adapter for SMTP or a future provider
- Persistence layer behind repository interfaces
- SQLite database in WAL mode

The architecture is a modular monolith for Phase 1. This avoids unnecessary distributed-system complexity while keeping business modules isolated. Later phases may use the same application or extract a module only when actual scale or deployment needs justify it.

All database access must go through a migration-capable data layer. SQLite-specific behavior must not leak into UI or business rules, preserving a practical path to PostgreSQL.

## 7. Administration Platform

### Dashboard

The dashboard summarizes new RFQs, leads requiring follow-up, content awaiting review, incomplete translations, and recently published content.

### Content and publishing

Editors manage pages, solutions, industries, projects, insights, navigation, shared company data, and SEO fields. The workflow is:

`Draft -> Review -> Published`

Authorized users can preview drafts, schedule publication, unpublish content, and review revisions. Thai and English publication states are tracked independently.

### Leads and RFQs

Each submission records contact details, organization, sector, requested services, project description, approximate budget when supplied, preferred contact method, attachments, consent record, source page, status, owner, internal notes, and timestamps.

Lead status begins with:

- New
- Contacting
- Evaluating
- Quotation
- Won
- Lost
- Spam

Status changes and ownership changes are included in the audit history.

### Roles

- **Admin:** all system settings, users, content, leads, and audit records
- **Editor:** create and edit content, upload media, and submit content for review
- **Approver:** review, schedule, publish, and unpublish content
- **Sales:** view and update leads/RFQs without access to publishing or system administration

Authorization is enforced on the server for every protected operation. Hiding an interface control is not an authorization mechanism.

## 8. Core Data Domains

The initial data model includes:

- User, Role, Session
- LocalizedPage and SEO metadata
- Solution, Industry, Project, Insight
- MediaAsset
- Navigation and SiteSetting
- Lead, LeadAttachment, LeadActivity
- ContentRevision and AuditEvent

Localized content will use explicit Thai and English fields or related locale records under a shared content identity. The implementation must support querying publication state by locale and detecting incomplete translations.

Files are stored outside the application image in a configured persistent directory. Database and upload paths are set by environment variables and must resolve to explicit, writable locations.

## 9. Key Data Flows

### Public content request

1. The visitor requests a localized URL.
2. The server resolves locale and published content.
3. The page renders with localized metadata and structured data.
4. Missing or unpublished localized content returns an appropriate 404 response.

### RFQ submission

1. The visitor completes the form and consents to data processing.
2. Client validation provides immediate feedback.
3. Server validation, file checks, honeypot, and rate limiting run independently.
4. The valid request is written atomically to SQLite.
5. A notification attempt is queued or recorded for retry.
6. The visitor receives a reference number and confirmation.

If notification delivery fails after the database write, the lead remains saved and the failure is visible to administrators. The user is not asked to resubmit a successfully stored request.

### Content publication

1. An editor saves a locale-specific draft.
2. An approver reviews the draft and preview.
3. Publishing creates an immutable revision and updates the active version transactionally.
4. Relevant cached pages are revalidated.
5. The publication event is recorded in the audit log.

## 10. Error Handling and Operational Safety

- Form values remain available after recoverable validation or network errors.
- User-facing messages are concise and do not expose stack traces or database details.
- Server errors include a correlation ID in structured logs.
- Uploads are restricted by MIME type, verified file signature, size, and configured limits.
- Notification failures use bounded retries and surface an admin warning.
- Scheduled jobs use locking so a task cannot execute twice concurrently.
- SQLite uses WAL mode, a configured busy timeout, short transactions, and one production application instance in Phase 1.
- The application performs a startup check for database and upload-directory availability.

## 11. Security and Privacy

- HTTPS terminates at a reverse proxy; port 8001 is accessible only to the proxy or trusted internal network.
- Secure, HTTP-only, same-site session cookies
- Password hashing using an established memory-hard algorithm
- Server-side authorization for all admin actions
- CSRF protection for state-changing browser requests
- Rate limiting on authentication and public submission endpoints
- Honeypot protection, with CAPTCHA introduced only when risk thresholds require it
- Input validation and output encoding
- Security headers and restrictive content policy appropriate to required assets
- Audit records for authentication, content publication, role changes, lead access, and lead status updates
- Configurable data-retention and deletion process for lead information
- Secrets supplied through environment configuration and never stored in the repository

## 12. Deployment and Configuration

The system will be deployed to a company server using Docker Compose:

- Next.js application container
- Persistent database volume
- Persistent upload volume
- Reverse proxy configuration managed as part of the server environment

Required port behavior:

- Development: `3001`
- Production application: `8001`
- Public traffic: HTTPS through the reverse proxy

Health checks cover application availability, database access, and writable media storage. Production deploys use versioned images and migration steps. Rollback instructions must account for both application version and schema compatibility.

Backups include the SQLite database, uploaded media, and required configuration metadata. The backup process uses SQLite's supported online backup/checkpoint approach rather than copying a live database file blindly. Restore tests are part of release readiness.

## 13. Performance, SEO, and Accessibility

- Server-rendered or statically generated public content where appropriate
- Responsive images and controlled media sizes
- Minimal client JavaScript and no decorative animation that blocks content
- Canonical and alternate-language links for Thai and English pages
- XML sitemap, robots configuration, social metadata, and organization/service structured data
- Redirect management for changed slugs
- Semantic page landmarks and heading order
- Keyboard-accessible controls, visible focus, meaningful labels, and sufficient contrast
- Reduced-motion behavior and no essential information conveyed only through animation

## 14. Testing and Acceptance

### Automated tests

- Unit tests for validation, localization, permissions, content state, and lead transitions
- Integration tests for database repositories, publishing, authentication, RFQ creation, file handling, and notifications
- End-to-end tests for primary Thai and English journeys, admin roles, content publishing, and lead processing
- Migration tests against a copy of representative production-like data

### Manual release checks

- Responsive review across common mobile, tablet, and desktop widths
- Thai and English typography/content review
- Keyboard navigation and screen-reader smoke test
- Performance and metadata audit on representative pages
- Security smoke test for protected routes, uploads, rate limits, and session behavior
- Backup and restore rehearsal
- Production port, reverse proxy, HTTPS, health check, and persistent-volume validation

### Phase 1 acceptance criteria

Phase 1 is accepted when:

1. Authorized staff can create, review, preview, publish, and unpublish Thai and English content.
2. Visitors can navigate all public sections and switch languages without broken or misleading fallback content.
3. Consultation and RFQ submissions are stored reliably, assigned a reference, visible to Sales/Admin, and protected against common abuse.
4. Role restrictions are enforced by the server and covered by tests.
5. The application runs on development port 3001 and production port 8001 under the documented deployment configuration.
6. A backup can be restored into a working test deployment with content, leads, and media intact.
7. The approved Technical Grid design works responsively and meets the agreed accessibility and motion behavior.

## 15. Explicit Non-Goals for Phase 1

- Customer accounts or client portal
- Telemetry ingestion or real-time operational dashboards
- AI assistant or enterprise knowledge search
- Full CRM automation
- Multi-instance deployment
- PostgreSQL deployment unless testing reveals that SQLite cannot meet measured Phase 1 requirements

These capabilities remain planned extensions and must not expand the Phase 1 delivery scope without a separate approved design change.
