import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Readable } from 'node:stream';
import { createAzimapBackend } from './adapter.mjs';

function harness(overrides = {}) {
    const calls = [];
    const handler = createAzimapBackend({
        issuerBaseUrl: 'https://issuer.example/', apiKey: 'server-secret',
        authenticate: async request => request.headers.cookie === 'signed-test-session' ? { id: 'trusted-user' } : null,
        csrf: { issue: async () => 'csrf', validate: async request => request.headers['x-csrf-token'] === 'csrf' },
        authorize: async (user, mapId, operations) => mapId === 12 && operations.every(x => x.layerId === 31 && x.operation === 'Edit')
            ? { mapId, layerIds: [31], editable: true, canExport: false, externalUserId: user.id } : null,
        fetchImpl: async (url, options) => {
            calls.push({ url: String(url), ...options, body: JSON.parse(options.body) });
            return { ok: true, json: async () => ({ accessToken: 'child', expiresInSeconds: 120, mapId: 12,
                scopeKey: 'scope', editable: true, canExport: false, apiKey: 'must-not-leak' }) };
        }, ...overrides
    });
    const invoke = async (path, body = {}, headers = {}, method = 'POST') => {
        const request = Readable.from([JSON.stringify(body)]);
        Object.assign(request, { url: path, method, headers: { cookie: 'signed-test-session', 'x-csrf-token': 'csrf', ...headers } });
        const response = { headers: {}, setHeader(k, v) { this.headers[k] = v; }, end(value) { this.body = value; } };
        await handler(request, response);
        return response;
    };
    return { calls, invoke };
}

test('authentication and CSRF are required before contacting Azimap', async () => {
    const h = harness();
    assert.equal((await h.invoke('/azimap/maps/12/write-token', {}, { cookie: '' })).statusCode, 401);
    assert.equal((await h.invoke('/azimap/maps/12/session', {}, { 'x-csrf-token': '' })).statusCode, 400);
    assert.equal(h.calls.length, 0);
});
test('CSRF endpoint uses the customer hook and disables caching', async () => {
    const r = await harness().invoke('/azimap/csrf', {}, {}, 'GET');
    assert.equal(r.statusCode, 200);
    assert.equal(r.headers['Cache-Control'], 'no-store');
    assert.equal(JSON.parse(r.body).csrfToken, 'csrf');
});
test('session grants and identity come only from the customer policy', async () => {
    const h = harness();
    const r = await h.invoke('/azimap/maps/12/session', { layerIds: [999], externalUserId: 'forged', canExport: true });
    assert.equal(r.statusCode, 200);
    assert.deepEqual(h.calls[0].body, { mapId: 12, layerIds: [31], readWrite: true, canExport: false, externalUserId: 'trusted-user' });
    assert.equal(h.calls[0].headers.Authorization, 'Bearer server-secret');
    assert.ok(!r.body.includes('server-secret') && !r.body.includes('must-not-leak'));
});
test('each write rechecks permission and returns only an explicit write DTO', async () => {
    let allowed = true;
    const h = harness({ authorize: async () => allowed ? { mapId: 12, layerIds: [31], editable: true, externalUserId: 'trusted-user' } : null });
    const body = { sessionToken: 'read-only', operations: [{ layerId: 31, operation: 'Edit' }] };
    const r = await h.invoke('/azimap/maps/12/write-token', body);
    assert.equal(r.statusCode, 200);
    assert.deepEqual(JSON.parse(r.body), { accessToken: 'child', expiresInSeconds: 120 });
    assert.equal(h.calls[0].url, 'https://issuer.example/api/embed/write-tokens');
    assert.equal(h.calls[0].body.sessionToken, 'read-only');
    allowed = false;
    assert.equal((await h.invoke('/azimap/maps/12/write-token', body)).statusCode, 403);
    assert.equal(h.calls.length, 1);
});
test('unknown, empty and out-of-scope intents are rejected', async () => {
    const h = harness();
    for (const operations of [[], [{ layerId: 31, operation: 'Admin' }]])
        assert.equal((await h.invoke('/azimap/maps/12/write-token', { sessionToken: 'read', operations })).statusCode, 400);
    assert.equal((await h.invoke('/azimap/maps/12/write-token', { sessionToken: 'read', operations: [{ layerId: 999, operation: 'Edit' }] })).statusCode, 403);
    assert.equal(h.calls.length, 0);
});
test('upstream denial and network failures never expose response bodies', async () => {
    const h = harness({ fetchImpl: async () => ({ ok: false, status: 403, text: async () => 'secret' }) });
    const r = await h.invoke('/azimap/maps/12/session');
    assert.equal(r.statusCode, 403);
    assert.equal(r.body, '');
    const failed = harness({ fetchImpl: async () => { throw new Error('secret'); } });
    assert.equal((await failed.invoke('/azimap/maps/12/session')).statusCode, 503);
});
test('customer hooks cannot be omitted', () => {
    assert.throws(() => createAzimapBackend({ issuerBaseUrl: 'https://issuer.example', apiKey: 'key' }), /hooks are required/);
});
