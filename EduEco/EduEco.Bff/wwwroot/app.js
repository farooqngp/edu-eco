// Demo SPA for the BFF pattern: session cookie only, no tokens in the browser, anti-CSRF header on every data request.
(() => {
    'use strict';

    const CSRF = { 'X-CSRF': '1' };
    const userOutput = document.getElementById('user');
    const apiOutput = document.getElementById('api');
    const sessionLabel = document.getElementById('session');
    const loginButton = document.getElementById('login');
    const logoutButton = document.getElementById('logout');

    let currentUser = null;

    const show = (element, value) => { element.textContent = typeof value === 'string' ? value : JSON.stringify(value, null, 2); };

    async function loadUser() {
        const response = await fetch('/bff/user', { headers: CSRF, credentials: 'same-origin' });
        currentUser = response.ok ? await response.json() : null;

        loginButton.hidden = currentUser !== null;
        logoutButton.hidden = currentUser === null;
        sessionLabel.textContent = currentUser ? `${currentUser.name ?? currentUser.subject} · tenant ${currentUser.tenantId ?? '—'}` : 'signed out';
        show(userOutput, currentUser ?? 'Not signed in.');
    }

    async function callApi(path) {
        const response = await fetch(path, { headers: CSRF, credentials: 'same-origin' });
        const body = await response.text();
        show(apiOutput, `${response.status} ${response.statusText}\n\n${body}`);
        if (response.status === 401) {
            await loadUser(); // session ended server-side
        }
    }

    loginButton.addEventListener('click', () => { window.location.href = '/bff/login?returnUrl=/'; });
    logoutButton.addEventListener('click', () => { window.location.href = currentUser.logoutUrl; });
    document.querySelectorAll('[data-api]').forEach((button) =>
        button.addEventListener('click', () => callApi(button.dataset.api)));

    if (new URLSearchParams(window.location.search).has('login_error')) {
        document.getElementById('notice').hidden = false;
    }

    loadUser();
})();
