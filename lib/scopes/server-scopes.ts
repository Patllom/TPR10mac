import 'server-only';
import { cookies } from 'next/headers';
import { fetchScopes } from './scope-fetch';
import { scopePath } from './scope-path';
import type { ScopeKey } from './scope-view';
export const technicalScopeUiEnabled = () => process.env.NODE_ENV === 'development' || process.env.TPR10_SCOPE_TEST_UI === 'true';
export async function readScopes(page = 1) {
  const token = (await cookies()).get('__Host-tpr10_session')?.value;
  return fetchScopes(process.env.TPR10_API_ORIGIN, token && /^[A-Za-z0-9_-]{43}$/.test(token) ? '__Host-tpr10_session=' + token : '', fetch, page);
}
export async function readScopeChoice(key: ScopeKey) {
  const path = scopePath(key);
  for (let page = 1; ; page++) {
    const result = await readScopes(page);
    const choice = result.items.find(item => scopePath(item.scope) === path);
    if (choice) return choice;
    if (page * result.pageSize >= result.total) return null;
  }
}
