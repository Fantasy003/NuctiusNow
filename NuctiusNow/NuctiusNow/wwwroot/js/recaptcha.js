(() => {
    let widgetId;
    let widgetContainer;
    window.onRecaptchaLoaded = () => {};
    window.renderRecaptchaCheckbox = (containerId, siteKey) => new Promise((resolve) => {
        const deadline = Date.now() + 10000;
        const tryRender = () => {
            const container = document.getElementById(containerId);
            if (!container || Date.now() >= deadline) { resolve(false); return; }
            if (!window.grecaptcha?.render) { setTimeout(tryRender, 250); return; }
            if (widgetContainer === container && widgetId !== undefined) { resolve(true); return; }
            try {
                widgetId = undefined;
                widgetContainer = container;
                widgetId = window.grecaptcha.render(container, { sitekey: siteKey });
                resolve(true);
            } catch (error) {
                console.warn("Could not render reCAPTCHA:", error);
                resolve(false);
            }
        };
        tryRender();
    });
    window.getRecaptchaToken = () => {
        try {
            return widgetId !== undefined && widgetContainer?.isConnected
                ? window.grecaptcha.getResponse(widgetId) : "";
        } catch { return ""; }
    };
    window.resetRecaptchaCheckbox = () => {
        if (window.grecaptcha && widgetId !== undefined && widgetContainer?.isConnected)
            window.grecaptcha.reset(widgetId);
    };
})();
