'use strict';

(() => {
    const element = id => document.getElementById(id);
    const dashboardMode = new URLSearchParams(window.location.search).get('view') === 'dashboard';
    const viewName = dashboardMode ? 'Dashboard' : 'Map';
    let state;
    let instance;
    let loader;
    let busy = false;
    const message = text => {
        element('message').textContent = text;
        element('message').hidden = !text;
    };
    const status = text => { element('session-status').textContent = text; };
    const request = async (url, body) => fetch(url, {
        method: 'POST', credentials: 'same-origin', cache: 'no-store',
        headers: { 'X-CSRF-Token': state.csrfToken, 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body)
    });

    async function refresh() {
        const response = await fetch('/api/bootstrap', { credentials: 'same-origin', cache: 'no-store' });
        if (!response.ok) throw new Error('Could not load application settings. Refresh the page to try again.');
        state = await response.json();
        element('setup').hidden = state.configured;
        element('login').hidden = !state.configured || state.authenticated;
        element('workspace').hidden = !state.configured || !state.authenticated;
        element('missing-settings').replaceChildren(...state.missingSettings.map(name => {
            const item = document.createElement('li');
            item.textContent = name;
            return item;
        }));
        if (state.authenticated) {
            element('signed-in-as').textContent = 'Signed in as ' + state.username;
            element('map-title').textContent = dashboardMode
                ? state.dashboardId ? 'Dashboard ' + state.dashboardId : 'Dashboard'
                : 'Map ' + state.map.mapId + (state.map.editable ? ' - editing requested' : ' - read only');
            element('load-embed').textContent = 'Load ' + viewName.toLowerCase();
            element('load-embed').hidden = dashboardMode && !state.dashboardId;
            element('open-dashboard').hidden = dashboardMode || !state.dashboardId;
            element('open-map').hidden = !dashboardMode;
            element('azimap-map').setAttribute('aria-label', 'Embedded Azimap ' + viewName.toLowerCase());
        }
    }

    function loadRuntime() {
        if (window.Azimap) return Promise.resolve();
        if (loader) return loader;
        loader = new Promise((resolve, reject) => {
            const script = document.createElement('script');
            const base = state.map.assetBaseUrl.replace(/\/?$/, '/');
            script.src = new URL('dist/azimap-embed-loader.min.js', base).href;
            script.dataset.assetBaseUrl = base;
            script.onload = () => {
                if (window.Azimap) resolve();
                else { loader = null; script.remove(); reject(new Error('The loaded script did not provide the Azimap runtime. Check AssetBaseUrl.')); }
            };
            script.onerror = () => { loader = null; script.remove(); reject(new Error('Could not load the Azimap runtime. Check access to the configured Azimap Cloud asset URL.')); };
            document.head.appendChild(script);
        });
        return loader;
    }

    function startDashboard() {
        if (typeof window.Azimap.initDashboard !== 'function')
            throw new Error('The configured Azimap runtime does not support embedded dashboards.');
        return window.Azimap.initDashboard({
            target: '#azimap-map',
            dashboardId: state.dashboardId,
            apiBaseUrl: state.map.apiBaseUrl,
            assetBaseUrl: state.map.assetBaseUrl,
            layerContentApiUrl: state.map.layerContentApiUrl,
            async sessionProvider({ dashboardId }) {
                const response = await request('/azimap/dashboards/' + dashboardId + '/session');
                if (response.status === 401) throw new Error('Sign in again to open the dashboard.');
                if (response.status === 403) throw new Error('Your application or Azimap denied dashboard access.');
                if (response.status === 404) throw new Error('Dashboard session issuance is unavailable. Check the issuer URL and whether embed sessions are enabled.');
                if (!response.ok) throw new Error('Could not authorize the dashboard (HTTP ' + response.status + ').');
                return response.json();
            },
            onSessionError: error => {
                status('Dashboard session ended. Reload this page to reopen it.');
                message(error.message);
            }
        });
    }

    async function startEmbed() {
        if (busy || instance) return;
        if (dashboardMode && !state.dashboardId) {
            status('Set Azimap:DashboardId in the sample configuration to open a dashboard.');
            return;
        }
        busy = true;
        element('load-embed').disabled = true;
        element('logout').disabled = true;
        message('');
        status('Loading ' + viewName.toLowerCase() + '...');
        try {
            await loadRuntime();
            instance = await (dashboardMode ? startDashboard() : window.Azimap.init({
                target: '#azimap-map', ...state.map,
                auth: window.Azimap.customerBackend('/azimap'),
                onSessionError: error => {
                    status('Map interaction suspended. Unsaved edits are retained.');
                    message(error.message + ' If permissions changed, resolve unsaved work before reloading the page.');
                }
            }));
            status(dashboardMode ? 'Dashboard read session active. Automatic renewal is enabled.'
                : 'Read session active. Writes require a fresh single-use token. Automatic renewal is enabled.');
            element('renew').disabled = false;
        } catch (error) {
            status(viewName + ' could not be loaded.');
            message(error.message);
            element('load-embed').disabled = false;
        } finally {
            busy = false;
            element('logout').disabled = false;
        }
    }

    element('login-form').addEventListener('submit', async event => {
        event.preventDefault();
        element('sign-in').disabled = true;
        message('');
        try {
            const response = await request('/api/login', { username: element('username').value, password: element('password').value });
            if (!response.ok) throw new Error(response.status === 429 ? 'Too many sign-in attempts. Try again in a minute.' : 'Sign-in failed. Check the sample account details and refresh the page if needed.');
            element('password').value = '';
            await refresh(); // Issue a CSRF token for the newly authenticated identity.
            await startEmbed();
        } catch (error) { message(error.message); }
        finally { element('sign-in').disabled = false; }
    });
    element('load-embed').addEventListener('click', startEmbed);
    element('renew').addEventListener('click', async () => {
        element('renew').disabled = true;
        element('logout').disabled = true;
        message('');
        try { await instance.renewSession(); status('Read session renewed at ' + new Date().toLocaleTimeString() + '.'); }
        catch (error) { message(error.message); }
        finally { element('renew').disabled = false; element('logout').disabled = false; }
    });
    element('logout').addEventListener('click', async () => {
        if (!window.confirm(dashboardMode ? 'Sign out and close this dashboard?'
            : 'Sign out and close this map? Save any unsaved edits first.')) return;
        element('logout').disabled = true;
        try {
            const response = await request('/api/logout');
            if (!response.ok && response.status !== 401) throw new Error('Sign-out failed. Please try again.');
            instance?.destroy();
            window.location.reload();
        } catch (error) { message(error.message); element('logout').disabled = false; }
    });
    refresh().then(() => { if (state.configured && state.authenticated) return startEmbed(); }).catch(error => message(error.message));
})();
