import { scopePath } from './scope-path';
import { scopeError, type ScopePage } from './scope-view';
export class ScopeHttpError extends Error {
  constructor(public readonly status: number) { super(scopeError(status)); }
}
export async function fetchScopes(origin: string | undefined, cookie: string, transport: typeof fetch = fetch, page = 1): Promise<ScopePage> {
  try {
    if (!origin || !Number.isSafeInteger(page) || page < 1 || page > 21474836) throw new ScopeHttpError(503);
    const url = new URL(origin);
    if (!['http:', 'https:'].includes(url.protocol) || url.pathname !== '/' || url.username || url.password || url.search || url.hash) throw new ScopeHttpError(503);
    const response = await transport(`${url.origin}/api/v1/scopes?page=${page}&pageSize=100`, { headers: { Cookie: cookie }, cache:'no-store', redirect:'error', signal:AbortSignal.timeout(5000) });
    if (!response.ok) throw new ScopeHttpError(response.status);
    const data: ScopePage = await response.json();
    if (!data || !Array.isArray(data.items) || !Number.isSafeInteger(data.total) || data.total < 0 || data.pageNumber !== page || data.pageSize !== 100) throw new ScopeHttpError(503);
    for (const item of data.items) {
      scopePath(item.scope);
      if (typeof item.workspaceName !== 'string' || !(item.projectName === null || typeof item.projectName === 'string') || !(item.siteName === null || typeof item.siteName === 'string') || !Array.isArray(item.capabilities) || !item.capabilities.every(x => typeof x === 'string')) throw new ScopeHttpError(503);
    }
    return data;
  } catch (error) { if (error instanceof ScopeHttpError) throw error; throw new ScopeHttpError(503); }
}
