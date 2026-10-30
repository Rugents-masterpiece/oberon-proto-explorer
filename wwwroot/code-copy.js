(() => {
    // Browser-side clicks retain user activation; no code goes to the server.
    if (window.oberonCodeCopyInitialized) return;
    window.oberonCodeCopyInitialized = true;
    const timers = new WeakMap();

    function copyViaSelection(text) {
        const active = document.activeElement;
        const selection = window.getSelection();
        const ranges = selection ? Array.from({ length: selection.rangeCount }, (_, i) => selection.getRangeAt(i).cloneRange()) : [];
        const transfer = document.createElement('textarea');
        transfer.value = text;
        transfer.readOnly = true;
        transfer.tabIndex = -1;
        transfer.setAttribute('aria-hidden', 'true');
        transfer.style.cssText = 'position:fixed;left:-10000px;top:0;opacity:0';
        document.body.append(transfer);
        try {
            transfer.focus({ preventScroll: true });
            transfer.select();
            // Plain LAN HTTP has no navigator.clipboard; keep this synchronous.
            return document.execCommand('copy');
        } finally {
            transfer.remove();
            if (active instanceof HTMLElement && active.isConnected) active.focus({ preventScroll: true });
            if (selection) {
                selection.removeAllRanges();
                for (const range of ranges) {
                    if (range.startContainer.isConnected && range.endContainer.isConnected) selection.addRange(range);
                }
            }
        }
    }

    async function writeClipboard(text) {
        if (navigator.clipboard?.writeText) {
            try {
                await navigator.clipboard.writeText(text);
                return;
            } catch {
                // Some browsers deny the modern API but allow a user-click copy.
            }
        }
        if (!copyViaSelection(text)) throw new Error('Clipboard copy was denied');
    }

    function feedback(panel, message, state) {
        const bubble = panel.querySelector('.code-copy-feedback');
        clearTimeout(timers.get(bubble));
        bubble.textContent = message;
        bubble.dataset.state = state;
        timers.set(bubble, setTimeout(() => {
            bubble.textContent = '';
            delete bubble.dataset.state;
        }, state === 'success' ? 2200 : 6000));
    }

    document.addEventListener('click', async (event) => {
        const button = event.target instanceof Element ? event.target.closest('[data-copy-code]') : null;
        if (!button) return;
        const panel = button.closest('.code-panel');
        const source = panel.querySelector(':scope > pre, :scope > textarea');
        if (!source) return;
        const text = source instanceof HTMLTextAreaElement ? source.value : source.textContent;
        try {
            await writeClipboard(text);
            feedback(panel, 'Skopírované!', 'success');
        } catch {
            feedback(panel, 'Kopírovanie zlyhalo. Použite Ctrl+C.', 'error');
        }
    });
})();
