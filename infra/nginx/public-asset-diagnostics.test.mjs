import test from 'node:test';
import assert from 'node:assert/strict';
import { publicAssetErrors } from './public-asset-diagnostics.mjs';

test('public asset errors retain only path and allowlisted failure phase', () => {
  const lines = [
    'upstream timed out while connecting to upstream, request: "GET /_next/static/chunks/439.js?token=secret HTTP/1.1", host: "private", referrer: "private"',
    'upstream timed out while reading response header from upstream, request: "GET /_next/static/css/style.css HTTP/1.1"',
    'upstream timed out while reading upstream, request: "GET /_next/static/chunks/a.js HTTP/1.1"',
    'upstream timed out while connecting to upstream, request: "GET /api/v1/auth/session?token=secret HTTP/1.1"',
    'upstream timed out while connecting to upstream, request: "POST /_next/static/chunks/a.js HTTP/1.1"',
    'unclassified secret, request: "GET /_next/static/chunks/a.js HTTP/1.1"',
  ].join('\n');
  assert.deepEqual(publicAssetErrors(lines), [
    { pathname: '/_next/static/chunks/439.js', category: 'upstream-connect' },
    { pathname: '/_next/static/css/style.css', category: 'upstream-header' },
    { pathname: '/_next/static/chunks/a.js', category: 'upstream-body' },
  ]);
});

const error = pathname => `upstream timed out while connecting to upstream, request: "GET ${pathname} HTTP/1.1"`;

test('rejects traversal, ambiguous paths and non-static assets without emitting their identifiers', () => {
  const rejected = [
    '/_next/static/../../api/private/customer-123',
    '/_next/static/chunks/../private/customer-123.js',
    '/_next/static/chunks/./secret.js', '/_next/static/chunks//secret.js',
    '/_next/static/chunks/%2e%2e/secret.js', '/_next/static/chunks/%252e%252e/secret.js',
    '/_next/static/chunks/secret\\private.js', '/_next/static/private/customer-123',
    '/_next/static/chunks/private.json', '/_next/static/css/private.js',
  ];
  assert.deepEqual(publicAssetErrors(rejected.map(error).join('\n')), []);
  assert.deepEqual(publicAssetErrors(error('/_next/static/chunks/app/page-a1.js')), [
    { pathname: '/_next/static/chunks/app/page-a1.js', category: 'upstream-connect' },
  ]);
});

test('phase phrases in metadata do not classify the error or override its actual phase', () => {
  const request = 'request: "GET /_next/static/chunks/a.js HTTP/1.1"';
  assert.deepEqual(publicAssetErrors(`unclassified, client: while connecting to upstream, ${request}, referrer: "https://example.test/while reading upstream"`), []);
  assert.deepEqual(publicAssetErrors(`failed while reading upstream, client: while connecting to upstream, ${request}`), [
    { pathname: '/_next/static/chunks/a.js', category: 'upstream-body' },
  ]);
});

test('bounds output to fifty records plus one sanitized truncation indicator', () => {
  const line = error('/_next/static/chunks/a.js');
  const record = { pathname: '/_next/static/chunks/a.js', category: 'upstream-connect' };
  assert.deepEqual(publicAssetErrors(Array(50).fill(line).join('\n')), Array(50).fill(record));
  assert.deepEqual(publicAssetErrors(Array(5000).fill(line).join('\n')), [...Array(50).fill(record), { truncated: true }]);
});

test('accepts a 256-character public path and never emits an overlong path', () => {
  const pathname = '/_next/static/chunks/' + 'a'.repeat(232) + '.js';
  assert.equal(pathname.length, 256);
  assert.deepEqual(publicAssetErrors(error(pathname)), [{ pathname, category: 'upstream-connect' }]);
  assert.deepEqual(publicAssetErrors(error(pathname + 'x.js')), [{ truncated: true }]);
  assert.deepEqual(publicAssetErrors(error(pathname + 'x.js') + '\n' + error('/_next/static/css/a.css')), [
    { pathname: '/_next/static/css/a.css', category: 'upstream-connect' }, { truncated: true },
  ]);
});
