'use client';
import { useEffect, useRef, useState } from 'react';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import { readStorage, StorageError } from '@/lib/attendance/storage-client';
import { type StorageData, type StorageView } from '@/lib/attendance/storage-view';
export function useStorageQuery(offset: number, sourceOffset: number, targetOffset: number) {
  const generation = useRef(0);
  const [revision, setRevision] = useState(0);
  const key = `${offset}:${sourceOffset}:${targetOffset}`;
  const [state, setState] = useState<{ key: string; data?: StorageData & { choices: StorageView[] }; status?: number }>({ key });
  useEffect(() => {
    const current = ++generation.current, controller = new AbortController();
    const cancel = () => { generation.current++; controller.abort(); };
    const clear = () => { generation.current++; controller.abort(); setState({ key }); };
    const unsubscribe = subscribeAuthChanges(clear);
    window.addEventListener('pagehide', clear);
    setState({ key });
    const extra = [...new Set([sourceOffset, targetOffset])].filter(x => x !== offset);
    void Promise.all([readStorage('locations', offset, controller.signal), readStorage('options', offset, controller.signal), readStorage('write-target', 0, controller.signal), readStorage('health', offset, controller.signal), readStorage('migrations', offset, controller.signal), Promise.all(extra.map(x => readStorage('locations', x, controller.signal)))]).then(([locations, options, target, health, migrations, selectedPages]) => {
      if (generation.current === current) setState({ key, data: { locations, options, 'write-target': target, health, migrations, choices: [...locations.items, ...selectedPages.flatMap(x => x.items)] } });
    }).catch(error => {
      if (generation.current !== current) return;
      controller.abort();
      const status = error instanceof StorageError ? error.status : 503;
      setState({ key, status });
      if (status === 401) window.location.replace('/login');
    });
    return () => { cancel(); unsubscribe(); window.removeEventListener('pagehide', clear); };
  }, [offset, sourceOffset, targetOffset, key, revision]);
  const reload = () => { generation.current++; setState({ key }); setRevision(x => x + 1); };
  return { data: state.key === key ? state.data : undefined, status: state.key === key ? state.status : undefined, reload };
}
