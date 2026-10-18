export type CorrectionState = 0 | 1 | 2 | 3 | 4 | 5;
export type CorrectionAction = 'save' | 'submit' | 'supervisor-approve' | 'hr-approve' | 'return' | 'reroute';
export type CorrectionAudience = 'mine' | 'supervisor' | 'hr';
export type CorrectionSite = { workspaceId: string; projectId: string; siteId: string };
export type CorrectionProposal = { workDate: string; inUtc: string; outUtc: string | null; inSite: CorrectionSite; outSite: CorrectionSite | null; reason: string };
export type SaveCorrection = { dayId: string | null; originalWorkDate: string; expectedDayVersion: number | null; proposal: CorrectionProposal };
export type CorrectionCommand = { commandId: string; expectedVersion: number; revision: number; reason: string | null };
export type CreateCorrection = { commandId: string; input: SaveCorrection };
export type ReviseCorrection = { input: SaveCorrection; command: CorrectionCommand };
export type CorrectionAcknowledgment = { id: string; commandId: string };
export type CorrectionComparison = { dayId: string | null; baselineMatches: boolean; original: { inUtc: string | null; outUtc: string | null } | null; current: { version: number; workDate: string; inUtc: string; outUtc: string | null; inSite: CorrectionSite | null; outSite: CorrectionSite | null } | null };
export type CorrectionHistory = {
  revisions: { revision: number; baselineDayVersion: number | null; proposal: CorrectionProposal; createdAtUtc: string }[];
  routes: { revision: number; supervisorId: string; hrCandidateIds: string[]; createdAtUtc: string }[];
  approvals: { revision: number; step: 'Supervisor' | 'Hr'; actorId: string; outcome: 'Approved' | 'Returned'; reason: string | null; occurredAtUtc: string }[];
  reroutes: { revision: number; actorId: string; reason: string; occurredAtUtc: string }[];
};
export type CorrectionView = {
  id: string; employeeId: string; originalWorkDate: string; version: number; state: CorrectionState; deadlineUtc: string;
  currentRevision: number; proposal: CorrectionProposal; baselineDayVersion: number | null; actions: CorrectionAction[];
  readBasis: 'Own' | 'Supervisor' | 'Hr' | 'WorkflowSupervisor' | 'WorkflowHr'; history?: CorrectionHistory;
  comparison?: CorrectionComparison;
};
export type CorrectionPage = { items: CorrectionView[]; total: number; pageNumber: number; pageSize: number };
