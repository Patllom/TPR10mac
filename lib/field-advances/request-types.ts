import type { FinanceScope, PolicySnapshot } from './policy-types';
export type { FinanceScope } from './policy-types';
export type RequestMode = 'mine' | 'delegated' | 'review' | 'report';
export type RequestState = 'Draft' | 'AwaitingOwner' | 'PendingApproval' | 'Held' | 'Returned' | 'Approved' | 'Rejected' | 'Withdrawn';
export interface RequestCommand { commandId: string; expectedVersion: number; reason: string }
export interface EstimateLineInput { id: string; categoryId: string; description: string; mode: 'QuantityRate' | 'LumpSum'; quantity: string | null; unitRate: string | null; lumpSum: string | null }
export interface RequestContent { kind: 'Individual' | 'Team'; purpose: string; fieldStart: string; fieldEnd: string; neededOn: string; teamPersonIds: string[]; lines: EstimateLineInput[] }
export interface CreateRequest { scope: FinanceScope; ownerPersonId: string; content: RequestContent }
export interface EstimateLine { id: string; categoryId: string; description: string; mode: 'QuantityRate' | 'LumpSum'; quantity: string | null; unitRate: string | null; amount: string }
export interface Estimate { lines: EstimateLine[]; total: string }
export interface RequestStep { id: string; levelCode: string; order: number; assigneePersonId: string | null; holdCode: string | null }
export interface RequestView { id: string; revisionId: string; version: number; state: RequestState; scope: FinanceScope; ownerPersonId: string; delegatePersonId: string | null; content: RequestContent; estimate: Estimate | null; holdCode: string | null; currentStep: RequestStep | null }
export interface ConfirmationQuote { requestId: string; revisionId: string; version: number; confirmationHash: string; estimate: Estimate; policy: PolicySnapshot; levels: string[] }
export interface RequestDecision { outcome: 'Approve' | 'Return' | 'Reject'; stepId: string }
export interface RequestHistory { eventId: string; revisionId: string; action: string; actor: string | null; time: string; reason: string; stepId: string | null; version: number; metadata: Record<string, unknown> }
export interface RequestScopeChoice { scope: FinanceScope; workspaceName: string; projectName: string; capabilities: string[] }
export interface RequestPersonChoice { id: string; name: string }
// Consumers render current, never receipt. Receipt is retained command evidence only.
export interface RequestCommandResult { receipt: RequestView; current: RequestView }
export interface QuotedRequest { request: RequestView; quote: ConfirmationQuote }
