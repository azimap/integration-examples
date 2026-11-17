const allowedOperations = new Set(['Create', 'CreateGeometry', 'Edit', 'Delete']);

// Mount behind the customer's own authentication and CSRF implementations.
// All hooks are mandatory. Neither browser fields nor the read token grant permissions.
export function createAzimapBackend({ issuerBaseUrl, apiKey, authenticate, csrf, authorize,
    prefix = '/azimap', fetchImpl = fetch }) {
    const issuer = new URL(issuerBaseUrl);
    const loopback = ['localhost', '127.0.0.1', '[::1]'].includes(issuer.hostname);
    if (!(issuer.protocol === 'https:' || issuer.protocol === 'http:' && loopback)
        || issuer.username || issuer.password || issuer.search || issuer.hash || !apiKey)
        throw new Error('Configure a server-held key and an HTTPS issuer (HTTP loopback is allowed).');
    if (typeof authenticate !== 'function' || typeof authorize !== 'function'
        || typeof csrf?.issue !== 'function' || typeof csrf?.validate !== 'function')
        throw new Error('Authentication, CSRF and authorization hooks are required.');
    issuer.pathname = issuer.pathname.replace(/\/?$/, '/');
    return async function handleAzimap(request, response) {
        const path = new URL(request.url, 'http://customer.invalid').pathname;
        if (!path.startsWith(prefix + '/')) return false;
        response.setHeader('Cache-Control', 'no-store');
        const send = (status, body) => {
            response.statusCode = status;
            if (body !== undefined) response.setHeader('Content-Type', 'application/json');
            response.end(body === undefined ? '' : JSON.stringify(body));
            return true;
        };
        try {
            const user = await authenticate(request);
            if (!user) return send(401);
            if (request.method === 'GET' && path === prefix + '/csrf')
                return send(200, { csrfToken: await csrf.issue(request, response, user) });
            const match = path.slice(prefix.length).match(/^\/maps\/(\d+)\/(session|write-token)$/);
            if (!match || request.method !== 'POST') return send(404);
            if (!await csrf.validate(request, user)) return send(400);
            const mapId = Number(match[1]);
            if (!Number.isSafeInteger(mapId) || mapId <= 0) return send(400);
            const writing = match[2] === 'write-token';
            let text = '';
            for await (const chunk of request) {
                text += chunk;
                if (Buffer.byteLength(text) > 65536) return send(413);
            }
            let input;
            try { input = JSON.parse(text || '{}'); } catch { return send(400); }
            const operations = writing ? input?.operations : [];
            if (writing && (typeof input?.sessionToken !== 'string' || !input.sessionToken.trim()
                || !Array.isArray(operations) || !operations.length || operations.length > 128
                || operations.some(x => !Number.isSafeInteger(x?.layerId) || x.layerId <= 0 || !allowedOperations.has(x.operation))))
                return send(400);
            const grant = await authorize(user, mapId, operations);
            if (!grant || grant.mapId !== mapId || !Array.isArray(grant.layerIds) || !grant.layerIds.length
                || grant.layerIds.some(id => !Number.isSafeInteger(id) || id <= 0)
                || typeof grant.externalUserId !== 'string' || !grant.externalUserId.trim()
                || writing && (grant.editable !== true || operations.some(x => !grant.layerIds.includes(x.layerId))))
                return send(403);
            const body = writing
                ? { mapId, sessionToken: input.sessionToken, operations: operations.map(x => ({ layerId: x.layerId, operation: x.operation })), externalUserId: grant.externalUserId }
                : { mapId, layerIds: [...new Set(grant.layerIds)], readWrite: grant.editable === true, canExport: grant.canExport === true, externalUserId: grant.externalUserId };
            const upstream = await fetchImpl(new URL(writing ? 'api/embed/write-tokens' : 'api/embed/sessions', issuer), {
                method: 'POST', headers: { Authorization: 'Bearer ' + apiKey, 'Content-Type': 'application/json' },
                body: JSON.stringify(body), signal: AbortSignal.timeout(20000), redirect: 'error'
            });
            if (!upstream.ok) return send(upstream.status);
            let result;
            try { result = await upstream.json(); } catch { return send(502); }
            if (!result || typeof result.accessToken !== 'string' || !result.accessToken.trim()
                || !Number.isFinite(result.expiresInSeconds) || result.expiresInSeconds <= 0
                || !writing && result.mapId !== mapId) return send(502);
            return send(200, writing
                ? { accessToken: result.accessToken, expiresInSeconds: result.expiresInSeconds }
                : { accessToken: result.accessToken, expiresInSeconds: result.expiresInSeconds, mapId: result.mapId,
                    scopeKey: result.scopeKey, editable: result.editable === true, canExport: result.canExport === true });
        } catch {
            // Never return upstream bodies, errors, headers or credentials.
            return send(503);
        }
    };
}
