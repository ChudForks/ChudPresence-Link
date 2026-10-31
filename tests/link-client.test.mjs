import test from 'node:test';
import assert from 'node:assert/strict';
import { LinkClient, browserDisplayToLink, mobileDisplayToLink, settingFields } from '../examples/link-client.mjs';

test('accepts only local endpoints and restores identity alongside credentials', () => {
  assert.throws(() => new LinkClient('https://127.0.0.1:17879'));
  assert.throws(() => new LinkClient('http://example.com:17879'));
  assert.throws(() => new LinkClient('http://8.8.8.8:17879'));
  assert.throws(() => new LinkClient(undefined, { token: 'secret' }));
  assert.doesNotThrow(() => new LinkClient('http://192.168.1.10:17879'));
  assert.doesNotThrow(() => new LinkClient('http://[::1]:17879'));
  assert.equal(browserDisplayToLink.artist, mobileDisplayToLink.artist);
  assert.equal(browserDisplayToLink.track, mobileDisplayToLink.track);
});

test('discovery checks pinned identity before transmitting credentials', async () => {
  const previous = globalThis.fetch;
  let calls = 0;
  globalThis.fetch = async (url, options) => {
    calls++;
    assert.equal(options.headers.Authorization, undefined);
    assert.equal(options.redirect, 'error');
    return Response.json({ name: 'ChudPresence Link', protocolVersion: 1, serverId: 'unexpected' });
  };
  try {
    const client = new LinkClient(undefined, { token: 'secret', serverId: 'expected' });
    await assert.rejects(() => client.settings(), /identity changed/);
    assert.equal(calls, 1);
  } finally { globalThis.fetch = previous; }
});

test('lease reservation renewal preserves increasing presence sequences', async () => {
  const previous = globalThis.fetch;
  const sequences = [];
  let reservations = 0;
  globalThis.fetch = async (url, options) => {
    if (url.endsWith('/v1/info')) return Response.json({ name: 'ChudPresence Link', protocolVersion: 1, serverId: 'server' });
    if (url.endsWith('/v1/presence/lease')) {
      const body = JSON.parse(options.body);
      assert.deepEqual(body, reservations++ === 0 ? {} : { leaseId: 'lease' });
    } else if (url.endsWith('/v1/presence')) sequences.push(JSON.parse(options.body).sequence);
    return Response.json({ leaseId: 'lease', expiresAt: '2026-10-26T12:00:30Z', heartbeatSeconds: 10, fallbackGraceSeconds: 5 });
  };
  try {
    const client = new LinkClient(undefined, { token: 'secret', serverId: 'server' });
    await client.acquire(); await client.publish(null); await client.acquire(); await client.publish(null);
    assert.deepEqual(sequences, [0, 1]);
  } finally { globalThis.fetch = previous; }
});

test('status, publishing, release and unpair check identity before sending credentials', async () => {
  const previous = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (url, options) => {
    calls.push(url);
    assert.equal(options.headers.Authorization, undefined);
    return Response.json({ name: 'ChudPresence Link', protocolVersion: 1, serverId: 'replacement-server' });
  };
  try {
    for (const operation of ['status', 'publish', 'release', 'unpair']) {
      const client = new LinkClient(undefined, { token: 'secret', serverId: 'expected' });
      client.lease = { leaseId: 'active-lease' };
      await assert.rejects(() => client[operation](null), /identity changed/);
      assert.equal(client.token, 'secret');
      assert.equal(client.lease.leaseId, 'active-lease');
    }
    assert.deepEqual(calls, Array(4).fill('http://127.0.0.1:17879/v1/info'));
  } finally { globalThis.fetch = previous; }
});

test('discovery rejects a missing server identity before submitting a pairing code', async () => {
  const previous = globalThis.fetch;
  let calls = 0;
  globalThis.fetch = async () => {
    calls++;
    return Response.json({ name: 'ChudPresence Link', protocolVersion: 1 });
  };
  try {
    const client = new LinkClient();
    await assert.rejects(() => client.pair('12345678', 'Phone', 'mobile'), /Unsupported Link server/);
    assert.equal(calls, 1);
    assert.equal(client.token, '');
  } finally { globalThis.fetch = previous; }
});

test('empty framework errors retain their HTTP status', async () => {
  const previous = globalThis.fetch;
  globalThis.fetch = async () => new Response('', { status: 413 });
  try {
    const client = new LinkClient();
    await assert.rejects(() => client.status(), e => e.status === 413 && /413/.test(e.message));
  } finally { globalThis.fetch = previous; }
});

test('service inventory and discovery never enable sync without an explicit selection', async () => {
  const previous = globalThis.fetch;
  const writes = [];
  const service = { id: 'unknown-radio', name: 'Unknown Radio', display: 'creator', displayChoices: ['app', 'creator', 'title'] };
  globalThis.fetch = async (url, options) => {
    if (url.endsWith('/v1/info')) return Response.json({ name: 'ChudPresence Link', protocolVersion: 1, serverId: 'server' });
    if (options.method !== 'GET') writes.push({ url, body: JSON.parse(options.body) });
    return Response.json({ revision: 1, services: [{ ...service, shared: true, syncEnabled: false, syncVersion: 0 }] });
  };
  try {
    const client = new LinkClient(undefined, { token: 'secret', serverId: 'server' });
    await client.reportServices([service]);
    const discovery = await client.services();
    assert.equal(discovery.services[0].syncEnabled, false);
    assert.equal(writes.length, 1);
    assert.deepEqual(writes[0].body, { services: [service] });
    await client.setServiceSync(service.id, { enabled: true, baseVersion: 0, sourceDeviceId: 'phone' });
    assert.equal(writes[1].url, 'http://127.0.0.1:17879/v1/services/unknown-radio/sync');
    assert.deepEqual(writes[1].body, { enabled: true, baseVersion: 0, sourceDeviceId: 'phone' });
  } finally { globalThis.fetch = previous; }
});

test('selective sync forwards selected fields, explicit false values, and partial patches', async () => {
  const previous = globalThis.fetch;
  const writes = [];
  globalThis.fetch = async (url, options) => {
    if (url.endsWith('/v1/info')) return Response.json({ name: 'ChudPresence Link', protocolVersion: 1, serverId: 'server' });
    writes.push({ url, method: options.method, body: JSON.parse(options.body) });
    return Response.json({ revision: 2, services: {} });
  };
  try {
    const client = new LinkClient(undefined, { token: 'secret', serverId: 'server' });
    await client.setServiceSync('video', { enabled: true, baseVersion: 1, fields: ['enabled', 'artwork'],
      initialSettings: { enabled: false, artwork: false } });
    assert.deepEqual(writes[0].body, { enabled: true, baseVersion: 1, fields: ['enabled', 'artwork'],
      initialSettings: { enabled: false, artwork: false } });
    await client.changeSettings({ video: { baseVersion: 2, artwork: false } });
    assert.deepEqual(writes[1].body, { changes: { video: { baseVersion: 2, artwork: false } } });
    assert.equal(writes[1].method, 'PATCH');
    await client.setServiceSync('video', { enabled: true, baseVersion: 3, sourceDeviceId: 'phone', fields: settingFields });
    assert.deepEqual(writes[2].body.fields, ['display', 'enabled', 'showWhilePaused', 'artwork', 'playbackProgress', 'actionButtons']);
  } finally { globalThis.fetch = previous; }
});
