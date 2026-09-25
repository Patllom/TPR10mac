'use client';
import { useEffect, useRef, useState } from 'react';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import { DirectoryError, readDirectory, type DirectoryQuery } from '@/lib/attendance/directory-client';
import { type DirectoryPage, type DirectoryOption } from '@/lib/attendance/directory-view';
export function useDirectoryQuery(resource: DirectoryQuery, query: string | null) {
  const generation = useRef(0);
  const [revision, setRevision] = useState(0);
  const key = resource + '?' + query;
  const [state, setState] = useState<{ key: string; data?: DirectoryPage | DirectoryPage<DirectoryOption>; status?: number; pending: boolean }>({ key: '', pending: true });
  useEffect(() => {
    const current = ++generation.current;
    const controller = new AbortController();
    let disposed = false;
    const valid = () => !disposed && current === generation.current;
    const clear = () => { generation.current++; controller.abort(); setState({ key, pending: false }); };
    const unsubscribe = subscribeAuthChanges(clear);
    window.addEventListener('pagehide', clear);
    setState({ key, pending: query !== null });
    if (query !== null) void readDirectory(resource, new URLSearchParams(query), controller.signal).then(data => {
      if (valid()) setState({ key, data, pending: false });
    }).catch(error => {
      if (!valid()) return;
      const status = error instanceof DirectoryError ? error.status : 503;
      setState({ key, status, pending: false });
      if (status === 401) window.location.replace('/login');
    });
    return () => { disposed = true; controller.abort(); unsubscribe(); window.removeEventListener('pagehide', clear); };
  }, [resource, query, key, revision]);
  const reload = () => { generation.current++; setState({ key, pending: query !== null }); setRevision(x => x + 1); };
  return { data: state.key === key ? state.data : undefined, status: state.key === key ? state.status : undefined, pending: state.key !== key || state.pending, reload };
}
