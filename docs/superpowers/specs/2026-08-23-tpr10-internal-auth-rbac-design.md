# TPR10 Internal Authentication and Role Access Design

Date: 2026-08-23  
Status: Awaiting Review

## 1. Objective

Add secure authentication and role-based access control for TPR10 employees who manage the website, published content, and sales enquiries. The first release is for internal staff only. It does not provide customer accounts, public registration, social login, or a customer portal.

The system uses email and password credentials. An Admin creates every account and assigns one of three fixed roles: `admin`, `content`, or `sales`.

## 2. Approved Scope

The feature includes:

- Employee login and logout
- Admin-created accounts only; no self-registration
- Temporary passwords for new or reset accounts
- Mandatory password change before a temporary-password account can use protected features
- Fixed role permissions for Admin, Content, and Sales
- Database-backed sessions with immediate revocation
- Account suspension and reactivation
- Password reset by an Admin issuing a new temporary password
- Audit records for security-sensitive actions
- Role-aware dashboard navigation and server-enforced authorization

The feature excludes:

- Customer login or customer portal
- Google Workspace, Microsoft 365, OAuth, magic links, and passkeys
- Self-service password recovery by email
- Custom per-user permission toggles
- Multi-factor authentication in the first release
- Public account registration

## 3. Technology and Application Context

The implementation uses:

- Next.js App Router
- JavaScript
- Tailwind CSS
- SQLite through the application's migration-capable data layer
- Argon2id for password hashing
- Server Actions and Route Handlers for protected mutations and endpoints
- Secure HTTP cookies for session transport

The application remains a modular monolith. Authentication, session management, authorization, user administration, and audit writing are separate focused modules with server-only boundaries.

## 4. Roles and Permissions

The application has exactly three fixed roles.

### Admin

Admin can:

- Access the full internal dashboard
- Create, edit, publish, unpublish, and manage media for website content
- View and update leads and RFQs
- Create employee accounts
- Assign or change roles
- issue temporary passwords
- Suspend and reactivate accounts
- Revoke all sessions for a user
- View audit events
- Manage system settings

### Content

Content can:

- Access the content-focused dashboard
- Create, edit, publish, and unpublish website content
- Manage project entries and media
- Change their own password
- Log out their own session

Content cannot access leads, RFQs, user administration, audit events, or system settings.

### Sales

Sales can:

- Access the sales-focused dashboard
- View and update leads and RFQs
- Add internal notes and update approved lead workflow fields
- Change their own password
- Log out their own session

Sales cannot access content management, publishing, media management, user administration, audit events, or system settings.

### Enforcement Rule

Navigation and controls are hidden when a role cannot use them, but hiding UI is not authorization. Every protected page, Server Action, Route Handler, repository operation, and sensitive data read performs a server-side authorization check close to the data source. Unauthorized authenticated access returns a 403 screen without exposing protected data. Unauthenticated access redirects to the login page.

## 5. Authentication Architecture

### Password verification

The login form accepts normalized email and password values. The server validates input shape, applies rate limits, loads the user by normalized email, checks account state, and verifies the password with Argon2id.

Passwords are never encrypted or stored as plaintext. Each Argon2id hash includes its algorithm parameters and unique salt. The implementation uses at least the current OWASP minimum Argon2id configuration and measures the selected configuration on the production server before release.

Login failure always returns the same generic message whether the email is unknown, the password is wrong, or the account is not available. Logs and user-facing errors never contain a plaintext password or session token.

### Database session

After successful authentication, the server creates a cryptographically random 32-byte token. The raw token exists only in the browser cookie. SQLite stores a SHA-256 digest of the token with the user ID, creation time, last-active time, absolute expiry time, revocation time, and restricted-session state.

The cookie is:

- `HttpOnly`
- `Secure` in production
- `SameSite=Lax`
- scoped to the application root
- omitted from JavaScript-readable storage

Session tokens rotate after successful login, after mandatory password change, and after other authentication-level changes. Logout revokes the database session before clearing the cookie.

### Session duration

- Idle timeout: 8 hours
- Absolute lifetime: 24 hours
- No Remember me option
- Expiry is enforced by the server, not by client timers

Changing a role, suspending an account, resetting a password, or using “sign out everywhere” revokes all of that user's active sessions immediately.

## 6. Temporary Password Flow

1. Admin creates an account or resets an existing account.
2. The server generates or accepts a temporary password only for the current Admin workflow, hashes it immediately, and never stores its plaintext value.
3. The user signs in with the temporary password.
4. The server creates a restricted session marked `must_change_password`.
5. The restricted session can access only the password-change page, the password-change action, and logout.
6. The user enters and confirms a new password that passes the password policy.
7. The server hashes the new password, clears the temporary-password state, revokes all existing sessions, and creates a new unrestricted session atomically.
8. The system records the password-change event without storing password content.

A temporary password cannot be used as a normal full-access session. Reusing an already consumed temporary password fails.

## 7. Account Lifecycle

An account has these persistent states:

- `invited`: created with a temporary password and not yet activated
- `active`: may use features allowed by its role after completing any required password change
- `suspended`: cannot authenticate; all sessions are revoked

`must_change_password` is a security flag rather than a separate persistent account status. When this flag is true, a successful credential check creates only a restricted session. The account becomes fully usable only after the flag is cleared by a successful password-change transaction.

Accounts are suspended rather than deleted so audit history and content authorship remain attributable. Email addresses are unique after normalization. The application prevents suspension of the final active Admin and prevents an Admin from removing their own Admin role when doing so would leave no active Admin.

## 8. Login Protection

The application limits failed login attempts by normalized email and source address. Five failed attempts within 15 minutes trigger a temporary wait response. The server records the event and continues using a generic login error so the response does not confirm whether the account exists.

Successful login clears the applicable failure counter. Rate-limit storage must work in the single-instance SQLite deployment and must not rely only on process memory.

All authentication requests require HTTPS in production. State-changing browser requests use the framework's origin protections plus explicit validation. Deployment configuration restricts allowed origins to the canonical application origin.

## 9. Authorization Design

Authorization uses an explicit immutable permission map rather than role-name comparisons scattered through pages.

Required permission keys are:

- `dashboard:view`
- `content:read`
- `content:write`
- `content:publish`
- `media:manage`
- `leads:read`
- `leads:update`
- `users:manage`
- `audit:read`
- `settings:manage`

The authorization module exposes focused server-only interfaces:

- `requireUser()` returns the active authenticated user or redirects/rejects.
- `requirePermission(permission)` returns the active user when allowed or throws a typed forbidden result.
- `can(role, permission)` is a pure permission-map query used by server rendering and unit tests.

Repositories that return sensitive lead, user, audit, or settings data are called only after the relevant permission check. Server Actions and Route Handlers repeat authorization at their own entry point even when the calling page was already protected.

## 10. Data Model

### User

- `id`
- `email`
- `normalized_email` with a unique constraint
- `display_name`
- `password_hash`
- `role`: `admin`, `content`, or `sales`
- `status`: `invited`, `active`, or `suspended`
- `must_change_password`
- `password_changed_at`
- `last_login_at`
- `created_at`
- `updated_at`
- `created_by_user_id`

### Session

- `id`
- `user_id`
- `token_digest` with a unique constraint
- `restricted_to_password_change`
- `created_at`
- `last_active_at`
- `expires_at`
- `revoked_at`
- `source_address_digest`
- `user_agent_summary`

### LoginAttempt

- `id`
- `normalized_email_digest`
- `source_address_digest`
- `succeeded`
- `attempted_at`

Raw source addresses are not required for rate-limit history. A keyed digest may be stored so attempts can be correlated without retaining the raw address longer than operationally necessary.

Login-attempt rows older than 30 days are deleted by a bounded scheduled cleanup. Audit events follow the application's separate audit-retention policy.

### AuditEvent

The existing audit domain records:

- actor user ID when known
- event type
- target user ID when applicable
- outcome
- non-sensitive metadata
- occurred-at time

Required authentication event types include login success, login failure, logout, password change, temporary-password issuance, role change, account suspension/reactivation, session revocation, and blocked authorization attempt.

## 11. User Experience

The internal interface follows the approved TPR10 Editorial Grid system: warm ivory canvas, slate text, restrained orange accents, serif editorial headings, sans-serif interface text, and no gradients or glow effects.

### Login page

- TPR10 identity and “Internal access only” context
- Email and password fields with visible labels
- Generic error region announced accessibly
- Primary Login action
- “Forgot password? Contact your administrator” guidance
- No registration or social-login links

### Mandatory password-change page

- Explains that the supplied password was temporary
- Shows password requirements before submission
- Accepts new password and confirmation
- Does not provide access to dashboard navigation
- Allows logout

### Role-aware dashboard

- Admin sees system-wide summaries and all navigation
- Content sees content, project, translation, and media summaries
- Sales sees lead and RFQ summaries
- Menu visibility follows the permission map
- Direct navigation to a disallowed page returns the branded 403 screen

### 403 screen

- States that the signed-in account cannot access the requested area
- Does not reveal protected page content or internal identifiers
- Offers a safe return to the role dashboard

## 12. Error Handling

- Invalid credentials use one generic response.
- Validation errors preserve the email field but never preserve the password field.
- Database or hashing failures return a correlation ID and a generic user message.
- Session lookup failures clear invalid cookies and redirect to Login.
- An expired or revoked session cannot be refreshed.
- Concurrent mandatory-password submissions allow only one successful transition.
- Account-management conflicts, including final-Admin protection, return explicit Admin-facing explanations.
- Audit-write failure on a sensitive mutation causes the mutation to fail transactionally when attribution is required.

## 13. Testing Strategy

### Unit tests

- Exact permission matrix for all three roles and permission keys
- Email normalization
- Password policy
- Session expiry and restricted-session rules
- Account lifecycle transitions
- Final-Admin protection
- Generic authentication error mapping

### Integration tests

- Argon2id password creation and verification
- Successful login creates a database session and secure cookie attributes
- Wrong password, unknown email, suspended account, and throttled request share the approved generic response
- Temporary-password login produces only a restricted session
- Password change rotates the session and consumes the temporary password
- Role change, password reset, and suspension revoke existing sessions
- Every protected service rejects missing permissions
- Rate limiting works using SQLite-backed state
- Required audit events are written without secrets

### End-to-end tests

- Admin logs in, creates a Content account, issues a temporary password, and the Content user changes it before reaching the dashboard
- Content can publish content but receives 403 for lead and user routes
- Sales can update a lead but receives 403 for content and user routes
- Admin can suspend a signed-in account and its next request returns to Login
- Keyboard-only Login, password change, logout, and 403 recovery
- Error messages and form fields have accessible names and visible focus states

## 14. Acceptance Criteria

The feature is accepted when:

1. Only Admin-created employee accounts can sign in.
2. Temporary-password users cannot access protected business features before setting a new password.
3. Admin, Content, and Sales permissions match this specification on pages, Server Actions, Route Handlers, services, and sensitive reads.
4. Role changes, suspension, password reset, and sign-out-everywhere revoke sessions immediately.
5. Passwords use Argon2id and raw session tokens are never stored in the database or logs.
6. Login attempts are rate-limited without revealing whether an email exists.
7. Required authentication and authorization events appear in the audit history without secrets.
8. Unit, integration, end-to-end, accessibility, lint, and production build checks pass.

## 15. Relationship to Existing Plans

This specification replaces the four-role authentication design in the earlier corporate platform documents. The prior `editor` and `approver` roles are merged into the single `content` role, which can create, edit, publish, and unpublish content. Any implementation plan must update old role matrices, seeded accounts, navigation, tests, and documentation so only `admin`, `content`, and `sales` remain.
