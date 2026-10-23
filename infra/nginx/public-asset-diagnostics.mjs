// Allowlisted output only: never return raw nginx errors, headers or query strings.
export function publicAssetErrors(log) {
  const records = [];
  let truncated = false;
  for (const line of log.split('\n')) {
    const request = /(?:^|,\s*)request: "GET (\/_next\/static\/[a-zA-Z0-9_./-]+)(?:\?[^" ]*)? HTTP\/[0-9.]+"/.exec(line);
    if (!request) continue;
    const pathname = request[1];
    // Reject ambiguous raw paths; never normalize a private path into an emitted one.
    if (pathname.slice(1).split('/').some(segment => !segment || segment === '.' || segment === '..')) continue;
    if (!/^\/_next\/static\/(?:chunks\/[a-zA-Z0-9_./-]+\.js|css\/[a-zA-Z0-9_./-]+\.css|media\/[a-zA-Z0-9_./-]+\.(?:woff2?|ttf|otf|png|jpe?g|gif|webp|avif|svg|ico))$/.test(pathname)) continue;
    const message = line.split(/,\s*(?:client|server|request|upstream|host|referrer):/, 1)[0];
    const category = message.includes('while connecting to upstream') ? 'upstream-connect'
      : message.includes('while reading response header from upstream') ? 'upstream-header'
        : message.includes('while reading upstream') ? 'upstream-body' : null;
    if (!category) continue;
    if (pathname.length > 256) { truncated = true; continue; }
    if (records.length === 50) { truncated = true; break; }
    records.push({ pathname, category });
  }
  if (truncated) records.push({ truncated: true });
  return records;
}
