(() => {
    let widgetId;
    let widgetContainer;
    window.renderTurnstile = (containerId, siteKey) => new Promise((resolve) => {
        const deadline = Date.now() + 10000;
        const tryRender = () => {
            const container = document.getElementById(containerId);
            if (!container || Date.now() >= deadline) { resolve(false); return; }
            if (!window.turnstile?.render) { setTimeout(tryRender, 250); return; }
            if (widgetContainer === container && widgetId !== undefined) { resolve(true); return; }
            try {
                widgetId = undefined;
                widgetContainer = container;
                widgetId = window.turnstile.render(container, { sitekey: siteKey });
                resolve(true);
            } catch (error) {
                console.warn("Could not render Cloudflare Turnstile:", error);
                resolve(false);
            }
        };
        tryRender();
    });
    window.getTurnstileToken = () => {
        try { return widgetId !== undefined && widgetContainer?.isConnected ? window.turnstile.getResponse(widgetId) : ""; }
        catch { return ""; }
    };
    window.resetTurnstile = () => {
        try { if (window.turnstile && widgetId !== undefined && widgetContainer?.isConnected) window.turnstile.reset(widgetId); }
        catch { /* Turnstile can recover on the next render */ }
    };
})();
