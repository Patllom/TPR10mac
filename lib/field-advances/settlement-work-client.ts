import { currentAccount } from './policy-client';
import { validId } from './policy-view';
import { SettlementError } from './settlement-client';
import type { SettlementRow } from './settlement-types';

export type SettlementWorkScope = { workspaceId: string; projectId: string };
export type SettlementWorkItem = {
  workspaceId: string;
  projectId: string;
  requestId: string;
  entityId: string;
  roundId: string | null;
  revisionId: string | null;
  entityVersion: number;
  requestVersion: number;
  familyVersion: number;
  state: string;
  allowedActions: string[];
  detail: SettlementRow | null;
};
export type SettlementWorkPage = { items: SettlementWorkItem[]; nextCursor: string | null };
export type SettlementComparisonDocument = {
  requestId: string;
  documentId: string;
  version: number;
  issuer: string;
  documentNumber: string | null;
  hasNoNumber: boolean;
  issuedOn: string;
  amount: string;
};
export type SettlementDuplicateComparison = {
  caseId: string;
  caseVersion: number;
  current: SettlementComparisonDocument;
  other: SettlementComparisonDocument;
};

const lanes: Record<string, string[]> = {
  rounds: ['round-approve', 'round-check'],
  refunds: ['refund-review'],
  incidents: ['incident-review', 'recovery-propose', 'incident-release', 'shortfall-prepare'],
  recoveries: ['recovery-review'],
  corrections: ['correction-review'],
  'case-payments': ['case-payment-review'],
  'excess-payments': ['excess-payment-review'],
  completion: ['completion-review'],
  duplicates: ['duplicate-review'],
  excess: ['excess-disposition']
};
const safeVersion = (value: unknown) => typeof value === 'number' && Number.isSafeInteger(value) && value >= 0;
const safeMoney = (value: unknown) => typeof value === 'string' && /^-?\d+(?:\.\d{1,2})?$/.test(value);

export function createSettlementWorkClient(userId: string, transport: typeof fetch = fetch) {
  if (!validId(userId)) throw new SettlementError(400);

  return {
    async downloadEvidence(lane: string, requestId: string, entityId: string, evidenceId: string,
      signal?: AbortSignal): Promise<Blob> {
      if (!['refunds', 'incidents', 'recoveries', 'corrections', 'case-payments', 'excess-payments'].includes(lane)
        || !validId(requestId) || !validId(entityId) || !validId(evidenceId)) throw new SettlementError(400);
      const bounded = signal ? AbortSignal.any([signal, AbortSignal.timeout(15000)]) : AbortSignal.timeout(15000);
      if (await currentAccount(transport, bounded) !== userId) throw new SettlementError(403);
      const response = await transport(`/api/v1/field-advances/settlement-work/${lane}/${requestId}/${entityId}/evidence/${evidenceId}`, {
        method: 'GET', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: bounded
      });
      if (!response.ok) throw new SettlementError(response.status);
      const type = response.headers.get('content-type')?.split(';')[0].trim();
      if (!['image/png', 'image/jpeg', 'application/pdf'].includes(type ?? '')) throw new SettlementError(503);
      const blob = await response.blob();
      if (blob.size < 1 || blob.size > 20_000_000) throw new SettlementError(503);
      if (await currentAccount(transport, bounded) !== userId) throw new SettlementError(403);
      return blob;
    },
    async duplicateComparison(requestId: string, caseId: string, signal?: AbortSignal): Promise<SettlementDuplicateComparison> {
      if (!validId(requestId) || !validId(caseId)) throw new SettlementError(400);
      const bounded = signal ? AbortSignal.any([signal, AbortSignal.timeout(15000)]) : AbortSignal.timeout(15000);
      if (await currentAccount(transport, bounded) !== userId) throw new SettlementError(403);
      const response = await transport(`/api/v1/field-advances/settlement-work/duplicates/${requestId}/${caseId}/comparison`, {
        method: 'GET', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: bounded
      });
      if (!response.ok) throw new SettlementError(response.status);
      const raw: unknown = await response.json();
      if (await currentAccount(transport, bounded) !== userId) throw new SettlementError(403);
      if (!raw || typeof raw !== 'object' || Array.isArray(raw)) throw new SettlementError(503);
      const value = raw as Record<string, unknown>;
      if (value.caseId !== caseId || !safeVersion(value.caseVersion)) throw new SettlementError(503);
      const documents = [value.current, value.other];
      for (const document of documents) {
        if (!document || typeof document !== 'object' || Array.isArray(document)) throw new SettlementError(503);
        const row = document as Record<string, unknown>;
        if (!validId(row.requestId) || !validId(row.documentId) || !safeVersion(row.version)
          || typeof row.issuer !== 'string' || row.issuer.length > 2000
          || !(row.documentNumber === null || typeof row.documentNumber === 'string')
          || typeof row.hasNoNumber !== 'boolean' || typeof row.issuedOn !== 'string'
          || !/^\d{4}-\d{2}-\d{2}$/.test(row.issuedOn) || !safeMoney(row.amount)) {
          throw new SettlementError(503);
        }
      }
      if ((value.current as SettlementComparisonDocument).requestId !== requestId
        || (value.other as SettlementComparisonDocument).requestId === requestId) throw new SettlementError(503);
      return value as SettlementDuplicateComparison;
    },
    async queue(scope: SettlementWorkScope, lane: string, take = 25, cursor?: string, signal?: AbortSignal): Promise<SettlementWorkPage> {
      if (!validId(scope.workspaceId) || !validId(scope.projectId) || !Object.hasOwn(lanes, lane)
        || !Number.isInteger(take) || take < 1 || take > 50
        || cursor !== undefined && (typeof cursor !== 'string' || cursor.length === 0 || cursor.length > 2048)) {
        throw new SettlementError(400);
      }
      const bounded = signal ? AbortSignal.any([signal, AbortSignal.timeout(15000)]) : AbortSignal.timeout(15000);
      if (await currentAccount(transport, bounded) !== userId) throw new SettlementError(403);
      const query = new URLSearchParams({ workspaceId: scope.workspaceId, projectId: scope.projectId, lane, take: String(take) });
      if (cursor) query.set('cursor', cursor);
      const response = await transport(`/api/v1/field-advances/settlement-work?${query}`, {
        method: 'GET', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: bounded
      });
      if (!response.ok) throw new SettlementError(response.status);
      const page: unknown = await response.json();
      if (await currentAccount(transport, bounded) !== userId) throw new SettlementError(403);
      if (!page || typeof page !== 'object' || Array.isArray(page)) throw new SettlementError(503);
      const value = page as Record<string, unknown>;
      if (!Array.isArray(value.items) || value.items.length > take
        || !(value.nextCursor === null || typeof value.nextCursor === 'string' && value.nextCursor.length > 0 && value.nextCursor.length <= 2048)) {
        throw new SettlementError(503);
      }
      const seen = new Set<string>();
      for (const raw of value.items) {
        if (!raw || typeof raw !== 'object' || Array.isArray(raw)) throw new SettlementError(503);
        const item = raw as Record<string, unknown>;
        if (item.workspaceId !== scope.workspaceId || item.projectId !== scope.projectId
          || !validId(item.requestId) || !validId(item.entityId)
          || (lane === 'rounds' ? !validId(item.roundId) || !validId(item.revisionId)
            : item.roundId !== null || item.revisionId !== null)
          || !safeVersion(item.entityVersion) || !safeVersion(item.requestVersion) || !safeVersion(item.familyVersion)
          || !(item.state === 'Pending' || lane === 'incidents' && item.state === 'Accepted')
          || !Array.isArray(item.allowedActions) || item.allowedActions.length < 1
          || item.allowedActions.length > (lane === 'incidents' ? 3 : 1)
          || !item.allowedActions.every(action => lanes[lane].includes(action))
          || new Set(item.allowedActions).size !== item.allowedActions.length
          || seen.has(item.entityId as string)) {
          throw new SettlementError(503);
        }
        if (lane === 'incidents' && (item.state === 'Pending') !== item.allowedActions.includes('incident-review')) {
          throw new SettlementError(503);
        }
        seen.add(item.entityId as string);
        if (lane !== 'rounds') {
          const detail = item.detail as Record<string, unknown> | null;
          if (!detail || detail.id !== item.entityId || detail.requestId !== item.requestId
            || detail.version !== item.entityVersion || detail.state !== item.state) throw new SettlementError(503);
          const refund = detail.refund as Record<string, unknown> | undefined;
          const incident = detail.incident as Record<string, unknown> | undefined;
          if (refund && !safeMoney(refund.amount)
            || incident && (!safeMoney(incident.ownerReceived) || !safeMoney(incident.wrongRecipientReceived))) {
            throw new SettlementError(503);
          }
        }
      }
      return value as SettlementWorkPage;
    }
  };
}
