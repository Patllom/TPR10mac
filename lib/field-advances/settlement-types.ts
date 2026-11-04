export type SettlementCommand = { commandId: string; requestVersion: number; familyVersion: number; entityVersion: number | null; reason: string; confirmationHash?: string };
export type SettlementCommandResult = { requestId: string; entityId: string | null; state: string; requestVersion: number; familyVersion: number; entityVersion: number | null };
export type SettlementTotals = { ownerReceived: string; approvedExpense: string; confirmedRefund: string; pendingRefund: string; ownerOutstanding: string; wrongRecipientOutstanding: string; hasOpenCase: boolean };
export type SettlementSummary = { declaration?: SettlementDeclarationView | null; requestId: string; state: string; requestVersion: number; familyVersion: number; totals: SettlementTotals; holds: string[]; allowedActions: string[] };
export type SettlementRow = { allowedActions?: string[]; submittedAtUtc?: string | null; submissionOnTime?: boolean | null; id: string; requestId: string; version: number; state: string; reason: string;
 document?: SettlementDocument | null; refund?: SettlementRefund | null; round?: SettlementRoundInfo | null; adjustment?: SettlementAdjustment | null;
 incident?: SettlementIncident | null; correction?: SettlementCorrection | null; payment?: SettlementPayment | null; excess?: SettlementExcess | null;
 recovery?: SettlementRecovery | null; completion?: SettlementCompletion | null; duplicate?: SettlementDuplicate | null };
export type SettlementPage = { items: SettlementRow[]; nextCursor: string | null; requestVersion: number; familyVersion: number };

export type SettlementAllocation = { documentId: string; amount: string };
export type SettlementLine = { id: string; sourceLineId: string | null; categoryId: string; description: string; incurredOn: string; quantity: string | null; rate: string | null; lumpSum: string | null; amount: string; allocations: SettlementAllocation[] };
export type SettlementDocument = { id: string; version: number; state: string; issuer: string; documentNumber: string | null; hasNoNumber: boolean; issuedOn: string; amount: string; evidenceIds: string[]; evidenceType?: string | null };
export type SettlementProof = { id: string; state: string; sha256: string; length: number; contentType: string };
export type SettlementPreview = { requestId: string; roundId: string; revisionId: string; requestVersion: number; familyVersion: number; roundVersion: number; revisionVersion: number; confirmationHash: string; total: string; approved: string; lines: SettlementLine[]; documents: SettlementDocument[]; evidence: SettlementProof[] };
// Totals/completeness/settlement refer only to this bounded scan window, never the entire family.
export type SettlementFamily = { requestId: string; requests: Omit<SettlementSummary, 'requestVersion' | 'familyVersion'>[]; pageTotals: SettlementTotals; totalsScope: 'visible-page'; pageComplete: boolean; pageSettled: boolean; nextCursor: string | null; familyVersion: number };

export type SettlementRound = Omit<SettlementPreview, 'confirmationHash' | 'approved'> & { state: string; currentStepId: string | null; currentStepVersion: number | null; allowedActions?: string[]; returnReview?: SettlementReturnReview | null; submittedAtUtc?: string | null; submissionOnTime?: boolean | null };
export type SettlementRefund = { amount: string; returnedOn: string; channel: string; companyRecipient: string; evidenceIds: string[]; adjustmentCaseId: string | null };
export type SettlementRoundInfo = { kind: string; sourceRoundId: string | null; adjustmentCaseId: string | null; revisionId: string | null; revisionVersion: number | null; total: string | null };
export type SettlementAdjustment = { kind: string; rootRefundId: string | null; rootExpenseRoundId: string | null; baselineVersion: number | null; baselineAmount: string | null; externalPaidAmount: string; evidenceIds: string[] };
export type SettlementIncident = { installmentId: string; attemptId: string; ownerReceived: string; wrongRecipientReceived: string; evidenceIds: string[] };
export type SettlementCorrection = { refundId: string; supersedesId: string | null; baseVersion: number; beforeAmount: string; afterAmount: string; delta: string; adjustmentCaseId: string | null; evidenceIds: string[] };
export type SettlementPayment = { parentId: string; supersedesId: string | null; amount: string; paidOn: string; evidenceIds: string[] };
export type SettlementExcess = { documentId: string; sourceRevisionId: string; amount: string; claimedAmount: string; capAmount: string; acceptedPaymentIds: string[] };
export type SettlementRecovery = { resolutionId: string; returnKind: string; amount: string; returnedOn: string; evidenceIds: string[] };
export type SettlementCompletion = { completedOn: string; clearingDueOn: string; refundDueOn: string; clearingDays: number; supersedesId: string | null; previousCompletedOn: string | null };
export type SettlementDuplicate = { documentId: string; documentVersion: number };

export type SettlementDeclarationView = { id: string; version: number; isWithdrawal: boolean; declaredAtUtc: string; workCompletionId: string; completionOnTime: boolean | null };
export type SettlementReturnReview = { revisionId: string; stepId: string; stepVersion: number; reason: string; affectedLineIds: string[]; returnedAtUtc: string | null; correctionDueOn: string | null };
