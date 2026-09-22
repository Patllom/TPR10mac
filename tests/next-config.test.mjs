import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const path = require.resolve('../next.config.js');
function load(origin) {
  delete require.cache[path];
  if (origin === undefined) delete process.env.TPR10_API_ORIGIN;
  else process.env.TPR10_API_ORIGIN = origin;
  return require(path);
}
test('API rewrite preserves the API path on the private origin', async () => {
  assert.deepEqual(await load('http://127.0.0.1:5080').rewrites(), [
    { source: '/api/:path*', destination: 'http://127.0.0.1:5080/api/:path*' },
  ]);
});
test('UI-only mode has no API rewrite', async () => {
  assert.deepEqual(await load(undefined).rewrites(), []);
});
for (const value of ['ftp://host', 'http://host/base', 'http://host?x=1', 'http://host#part', 'http://user:pass@host', 'bad-url']) {
  test('reject unsafe API origin: ' + value, () => {
    assert.throws(() => load(value), /TPR10_API_ORIGIN/);
  });
}
test('normalizes a trailing slash', async () => {
  assert.equal((await load('http://127.0.0.1:5080/').rewrites())[0].destination,
    'http://127.0.0.1:5080/api/:path*');
});
