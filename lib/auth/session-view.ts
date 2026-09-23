import { safeReturnPath } from './safe-return-path';

export type SessionStage = 'PasswordChangeRequired' | 'MfaEnrollmentRequired' | 'MfaChallengeRequired' | 'Active';
export interface SessionView {
  userId: string;
  stage: SessionStage;
  permissions: string[];
  mfaVerifiedAtUtc: string | null;
}
export function isSessionView(value: unknown): value is SessionView {
  if (!value || typeof value !== 'object') return false;
  const item = value as SessionView;
  return typeof item.userId === 'string' && item.userId.length > 0
    && ['PasswordChangeRequired', 'MfaEnrollmentRequired', 'MfaChallengeRequired', 'Active'].includes(item.stage)
    && Array.isArray(item.permissions) && item.permissions.every(p => typeof p === 'string')
    && (item.mfaVerifiedAtUtc === null || typeof item.mfaVerifiedAtUtc === 'string');
}
export function sessionDestination(session: Pick<SessionView, 'stage'>, returnTo: string | null): string {
  const safe = safeReturnPath(returnTo);
  if (session.stage === 'PasswordChangeRequired') return '/auth/change-password?returnTo=' + encodeURIComponent(safe);
  if (session.stage !== 'Active') return '/auth/mfa?returnTo=' + encodeURIComponent(safe);
  return safe;
}
