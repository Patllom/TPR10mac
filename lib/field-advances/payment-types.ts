import type { RequestContent, RequestCommand } from './request-types';
export type PaymentAction = 'installments' | 'update' | 'submit' | 'review' | 'start' | 'record' | 'outcome' | 'mismatch' | 'cancel' | 'metadata' | 'takeover' | 'confirm' | 'ceiling-amendment' | 'ceiling-activate' | 'disposition' | 'finalize' | 'supplements';
export type PaymentSummary = {
  requestId: string; installmentId: string | null; familyVersion: number; requestVersion: number; installmentVersion: number | null;
  state: string; approved: string; spendable: string; reserved: string; paid: string; available: string; holdCodes: string[];
};
export type PaymentCommand = { commandId: string; familyVersion: number; requestVersion: number; installmentVersion: number | null; reason: string };
export type PaymentDestination = { channel: 'Cash' | 'Transfer'; recipientPersonId: string; bankCode: string | null; accountNumber: string | null; accountHolder: string | null };
export type EvidenceTicket = { id: string; sha256: string; length: number; contentType: string; state: string };
export type PaymentMetadata = { paidOn: string; reference: string | null; evidenceIds: string[] };
export type ActualPayment = PaymentMetadata & { amount: string; recipientPersonId: string };
export type PaymentOutcome = { outcome: string; actual: ActualPayment | null; evidenceIds: string[] };
export type PaymentProposalChange = { revisionId: string | null; approved: string | null; basis: string | null; outcome: string | null; state: string | null; reserved: string | null; metadata: PaymentMetadata | null; payment: PaymentOutcome | null };
export type PaymentProposal = { id: string; requestId: string; installmentId: string | null; kind: string; state: string; familyVersion: number; requestVersion: number; installmentVersion: number | null; reason: string; before: PaymentProposalChange; after: PaymentProposalChange; evidence: EvidenceTicket[] };
export type InstallmentDetail = { summary: PaymentSummary; revisions: { id: string; revisionNumber: number; isCurrent: boolean; amount: string; destination: PaymentDestination; reviewedAtUtc: string | null; reason: string }[]; proposals: PaymentProposal[]; evidence: EvidenceTicket[] };
export type SupplementInput = { content: RequestContent; meta: RequestCommand };

export type PaymentCatalog = { installments: PaymentSummary[]; proposals: PaymentProposal[]; evidence: EvidenceTicket[] };
