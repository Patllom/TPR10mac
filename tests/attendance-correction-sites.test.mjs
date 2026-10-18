import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { loadTs } from './helpers/load-ts.mjs';
const path = 'lib/attendance/correction-site-client.ts';
const id = '11111111-1111-4111-8111-111111111111';
const site = { workspaceId: id, projectId: id, siteId: id };
const at = '2026-09-25T01:00:45.123456Z';
function client() { assert.ok(existsSync(path), 'historical site client missing'); return loadTs(path); }
test('historical choices fetch exact instants with bounded private transport and never choose another owner', async () => {
  const c = client(), old = global.fetch, calls = [];
  global.fetch = async (url, options) => { calls.push([url, options]); return Response.json({ items: [{site,label:'เก่า'}], total:1,pageNumber:1,pageSize:100,atUtc:at }); };
  try {
    assert.deepEqual(await c.loadCorrectionSites(at, null, new AbortController().signal), [{site,label:'เก่า'}]);
    const [url, opts] = calls[0]; assert.equal(url, '/api/v1/attendance/corrections/sites?at=2026-09-25T01%3A00%3A45.123456Z&page=1&pageSize=100');
    assert.equal(opts.cache, 'no-store'); assert.equal(opts.credentials,'same-origin'); assert.equal(opts.redirect,'error'); assert.ok(opts.signal);
  } finally { global.fetch = old; }
});
test('historical choices reject malformed responses, wrong instants and unauthorized responses', async () => {
  const c = client(), old = global.fetch;
  const good = {items:[{site,label:'เก่า'}],total:1,pageNumber:1,pageSize:100,atUtc:at};
  try {
    for (const body of [{...good,atUtc:'2026-09-26T01:00:00Z'}, {...good,items:[{site:{...site,path:'/private'},label:'เก่า'}]}, {...good,total:0}]) {
      global.fetch = async () => Response.json(body);
      await assert.rejects(() => c.loadCorrectionSites(at,null,new AbortController().signal));
    }
    global.fetch = async () => new Response(null,{status:403});
    await assert.rejects(() => c.loadCorrectionSites(at,null,new AbortController().signal));
  } finally {global.fetch=old;}
});
