// Invalidation only: no identity, cookie, token or private payload crosses windows.
const name = 'tpr10-auth-change';
const listeners = new Set<() => void>();
let channel: BroadcastChannel | undefined;
let listening = false;
const changed = () => { for (const listener of listeners) listener(); };
const storageChanged = (event: StorageEvent) => { if (event.key === name && event.newValue) changed(); };

export function subscribeAuthChanges(listener: () => void): () => void {
  if (typeof window === 'undefined') return () => {};
  if (!listening) {
    listening = true;
    if (typeof BroadcastChannel !== 'undefined') {
      channel = new BroadcastChannel(name);
      channel.onmessage = event => { if (event.data === 'changed') changed(); };
    } else window.addEventListener('storage', storageChanged);
  }
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
    if (!listeners.size) {
      channel?.close(); channel = undefined;
      window.removeEventListener('storage', storageChanged); listening = false;
    }
  };
}

export function publishAuthChange() {
  if (typeof window === 'undefined') return;
  if (typeof BroadcastChannel !== 'undefined') {
    // The sender completes its own login/MFA/logout flow; only other windows invalidate.
    const sender = channel ?? new BroadcastChannel(name);
    sender.postMessage('changed');
    if (sender !== channel) sender.close();
  } else {
    try { window.localStorage.setItem(name, crypto.randomUUID()); } catch { /* Storage may be blocked; focus revalidation remains. */ }
  }
}
