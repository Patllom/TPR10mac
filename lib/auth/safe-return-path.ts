export function safeReturnPath(value: string | null): string {
  if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//') || /[\\\x00-\x20\x7f]/.test(value)) return '/portal';
  try {
    const url = new URL(value, 'https://tpr10.invalid');
    const decoded = decodeURIComponent(url.pathname);
    if (/[\\\x00-\x20\x7f]/.test(decoded) || /%(?:2f|5c|25)/i.test(url.pathname)) return '/portal';
    return url.origin === 'https://tpr10.invalid' && (url.pathname === '/portal' || url.pathname.startsWith('/portal/'))
      ? url.pathname + url.search : '/portal';
  } catch { return '/portal'; }
}
