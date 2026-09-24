'use client';
import { useEffect, useRef, useState } from 'react';
import { scopeError } from '@/lib/scopes/scope-view';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
export function useScopeQuery<T>(path: string | null) {
  const generation = useRef(0);
  const [reloadKey, setReloadKey] = useState(0);
  const [state, setState] = useState<{key: string | null; data?: T; error?: string; pending: boolean}>({key:null,pending:false});
  useEffect(() => {
    const current=++generation.current;
    let disposed=false;
    const isCurrent=()=>!disposed&&current===generation.current;
    const controller=new AbortController();
    setState({key:path,pending:!!path});
    const clear=()=>{generation.current++; controller.abort(); setState({key:path,pending:false});};
    const unsubscribe=subscribeAuthChanges(clear);
    window.addEventListener('pagehide',clear);
    if (path) void (async()=>{
      try {
        const response=await fetch(path,{cache:'no-store',credentials:'same-origin',redirect:'error',signal:AbortSignal.any([controller.signal,AbortSignal.timeout(5000)])});
        if (!isCurrent()) return;
        if (response.status===401) {clear(); window.location.replace('/login'); return;}
        if (!response.ok) {setState({key:path,pending:false,error:scopeError(response.status)}); return;}
        const data: T=await response.json();
        if (isCurrent()) setState({key:path,pending:false,data});
      } catch {if(isCurrent()) setState({key:path,pending:false,error:scopeError(503)});}
    })();
    return ()=>{disposed=true; controller.abort(); unsubscribe(); window.removeEventListener('pagehide',clear);};
  },[path,reloadKey]);
  function reload() {generation.current++; setState({key:path,pending:!!path}); setReloadKey(x=>x+1);}
  return {data:state.key===path?state.data:undefined,error:state.key===path?state.error:undefined,pending:state.key!==path||state.pending,reload};
}
