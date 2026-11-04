import { SettlementError } from './settlement-client';
import type { SettlementWorkScope } from './settlement-work-client';

type ScopePageRow = {
  scope: { workspaceId: string; projectId: string | null };
  workspaceName: string;
  projectName: string | null;
};

export type SettlementScopeChoice = { scope: SettlementWorkScope; label: string };

// Each provider uses bounded pages. The caller asks for another page explicitly.
export async function discoverSettlementScopes(
  userId: string,
  pageCount: number,
  requestPage: (page: number, pageSize: number) => Promise<ScopePageRow[]>,
  paymentPage: (page: number, pageSize: number) => Promise<ScopePageRow[]>,
  account: () => Promise<string>
): Promise<{ choices: SettlementScopeChoice[]; hasMore: boolean }> {
  if (!Number.isSafeInteger(pageCount) || pageCount < 1) throw new SettlementError(400);
  const choices = new Map<string, SettlementScopeChoice>();
  let hasMore = false;

  for (const source of [requestPage, paymentPage]) {
    for (let page = 1; page <= pageCount; page++) {
      if (await account() !== userId) throw new SettlementError(403);
      const values = await source(page, 100);
      if (!Array.isArray(values) || values.length > 100) throw new SettlementError(503);
      for (const value of values) {
        if (!value.scope.projectId) continue;
        const scope = { workspaceId: value.scope.workspaceId, projectId: value.scope.projectId };
        choices.set(`${scope.workspaceId}:${scope.projectId}`, {
          scope,
          label: `${value.workspaceName} · ${value.projectName}`
        });
      }
      if (values.length < 100) break;
      if (page === pageCount) hasMore = true;
    }
  }

  return { choices: [...choices.values()], hasMore };
}
