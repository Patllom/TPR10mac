export interface FinanceScope { workspaceId: string; projectId: string | null }
export interface ApprovalBand { order: number; levelCode: string; upperInclusive: string }
export interface CategoryRule { id: string; name: string; active: boolean; unit: string | null; standardRate: string | null; evidenceTypes: string[] }
export interface PolicyContent { clearingDays: number; bands: ApprovalBand[]; categories: CategoryRule[] }
export type PolicyMode = 'Company' | 'Override' | 'Inherit';
export interface PolicyCommandOptions { mode: PolicyMode; sourcePolicyRevisionId: string | null; baseCompanyVersionId: string | null; expectedActiveRevisionId: string | null; expectedActiveVersion: number; expectedContentHash: string | null }
export interface CommandMeta { commandId: string; expectedVersion: number; reason: string; policy: PolicyCommandOptions }
export interface PolicyView { id: string; scope: FinanceScope; state: string; version: number; baseCompanyVersionId: string | null; content: PolicyContent; contentHash: string; mode: PolicyMode; sourcePolicyRevisionId: string | null; expectedActiveRevisionId: string | null; expectedActiveVersion: number; activeRevisionId?: string | null; activeVersion?: number | null }
export interface PolicySnapshot { companyVersionId: string; projectVersionId: string | null; content: PolicyContent; contentHash: string; scope: FinanceScope; requiresReview: boolean }
export interface CompanyRevision { id: string; content: PolicyContent; contentHash: string }
export interface CompanyContext { basis: CompanyRevision; latest: CompanyRevision }
export interface PolicyHistory { id: string; policyRevisionId: string; outcome: string; reviewedAt: string; reviewedVersion: number | null; contentHash: string | null; content: PolicyContent | null; reviewerId: string; reason: string }
export interface ScopeChoice { scope: FinanceScope; workspaceName: string; projectName: string | null; capabilities: string[] }
