// WebAuthn passkey ceremonies. No inline script (CSP script-src 'self').
(() => {
    'use strict';

    const supported = typeof window.PublicKeyCredential !== 'undefined'
        && typeof PublicKeyCredential.parseRequestOptionsFromJSON === 'function'
        && typeof PublicKeyCredential.parseCreationOptionsFromJSON === 'function';

    const antiforgeryToken = (form) => form.querySelector('input[name="__RequestVerificationToken"]')?.value ?? '';

    const showError = (form, message) => {
        const target = form.querySelector('[data-passkey-error]');
        if (target) {
            target.textContent = message;
            target.hidden = false;
        }
    };

    async function fetchOptions(form, url) {
        const response = await fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'RequestVerificationToken': antiforgeryToken(form) },
        });
        if (!response.ok) {
            throw new Error(`Passkey options request failed (${response.status}).`);
        }
        return response.json();
    }

    function bind(selector, ceremony) {
        document.querySelectorAll(selector).forEach((button) => {
            const form = button.closest('form');
            if (!supported) {
                showError(form, 'This browser does not support passkeys.');
                button.disabled = true;
                return;
            }

            button.addEventListener('click', async () => {
                button.disabled = true;
                try {
                    const options = await fetchOptions(form, button.dataset.optionsUrl);
                    const credential = await ceremony(options);
                    form.querySelector('input[name="credentialJson"]').value = JSON.stringify(credential);
                    form.submit();
                } catch (error) {
                    showError(form, error?.name === 'NotAllowedError'
                        ? 'The passkey operation was cancelled.'
                        : 'The passkey operation failed. Try again.');
                    button.disabled = false;
                }
            });
        });
    }

    bind('[data-passkey-login]', (options) =>
        navigator.credentials.get({ publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(options) }));

    bind('[data-passkey-register]', (options) =>
        navigator.credentials.create({ publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(options) }));
})();
