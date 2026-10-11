// Translate presentation text without replacing Blazor-owned elements or changing
// form values, URLs, stored articles, or the source used by the recommendation engine.
window.nuctiusLocalization = (() => {
    const attributes = ["placeholder", "title", "aria-label", "alt"];
    const excluded = "script,style,code,pre,textarea,[translate='no'],[data-no-translate],.nav-logo,.chat-user";
    const originals = new WeakMap();
    const cache = new Map();
    let reference, observer, language = { code: "en", culture: "en-US" }, catalog = {}, version = 0;
    let timer, busy = false, dirty = false, active = false, failed = false, status;

    function ensureStatus() {
        if (status) return;
        status = document.createElement("div");
        status.className = "localization-status";
        status.setAttribute("translate", "no");
        status.setAttribute("role", "status");
        const label = document.createElement("span");
        const retry = document.createElement("button");
        retry.type = "button";
        retry.onclick = () => { failed = false; schedule(); };
        status.append(label, retry);
        document.body.append(status);
    }
    function known(source) {
        const result = catalog[source.toLowerCase()];
        if (!result) return null;
        return source === source.toUpperCase() ? result.toLocaleUpperCase(language.culture) : result;
    }
    function showStatus(error, loading) {
        ensureStatus();
        status.hidden = !error && !loading;
        status.firstChild.textContent = known(error ? "Translation unavailable. Try again." : "Translating…") ||
            (error ? "Translation unavailable. Try again." : "Translating…");
        status.lastChild.textContent = known("Retry") || "Retry";
        status.lastChild.hidden = !error;
    }
    function schedule() {
        if (!active || failed) return;
        dirty = true;
        clearTimeout(timer);
        timer = setTimeout(flush, 120);
    }
    function collect() {
        const targets = [];
        function add(node, attribute) {
            const element = attribute ? node : node.parentElement;
            if (!element || element.closest(excluded)) return;
            const current = attribute ? node.getAttribute(attribute) : node.data;
            if (!current || !/\p{L}/u.test(current) || /^https?:\/\//i.test(current.trim())) return;
            const slot = attribute || "text";
            let slots = originals.get(node);
            if (!slots) { slots = new Map(); originals.set(node, slots); }
            let record = slots.get(slot);
            // A Blazor update creates a new source; our own mutations retain the old source.
            if (!record || (current !== record.output && current !== record.source)) {
                record = { source: current, output: current, language: null };
                slots.set(slot, record);
            }
            const content = !!element.closest("[data-news-text]");
            if (record.language === language.code && current === record.output) return;
            targets.push({ node, attribute, record, content, text: record.source.trim() });
        }
        const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
        while (walker.nextNode()) add(walker.currentNode, null);
        // The page title lives in <head>, not <body>.
        if (document.querySelector("title")?.firstChild) add(document.querySelector("title").firstChild, null);
        document.body.querySelectorAll("[placeholder],[title],[aria-label],[alt]").forEach(element => {
            attributes.forEach(attribute => { if (element.hasAttribute(attribute)) add(element, attribute); });
        });
        return targets;
    }
    function apply(target, value, revision) {
        if (!active || revision !== version || !target.node.isConnected) return;
        const current = target.attribute ? target.node.getAttribute(target.attribute) : target.node.data;
        if (current !== target.record.source && current !== target.record.output) return;
        const prefix = target.record.source.match(/^\s*/)[0];
        const suffix = target.record.source.match(/\s*$/)[0];
        target.record.output = prefix + value.trim() + suffix;
        target.record.language = language.code;
        if (target.attribute) target.node.setAttribute(target.attribute, target.record.output);
        else target.node.data = target.record.output;
    }
    async function flush() {
        if (busy || !active || failed) return;
        busy = true;
        dirty = false;
        const revision = version;
        const code = language.code;
        try {
            const pending = [];
            for (const target of collect()) {
                const key = `${code}:${target.content}:${target.text}`;
                const builtin = target.content ? null : (code === "en" ? target.text : known(target.text));
                if (builtin !== null) apply(target, builtin, revision);
                else if (cache.has(key)) apply(target, cache.get(key), revision);
                else pending.push({ ...target, key });
            }
            showStatus(false, pending.length > 0);
            while (pending.length && revision === version && active) {
                const batch = [], entries = new Map();
                let length = 0;
                while (pending.length && entries.size < 40 && (length < 22000 || !batch.length)) {
                    const target = pending.shift();
                    batch.push(target);
                    if (!entries.has(target.key)) {
                        entries.set(target.key, { text: target.text, content: target.content });
                        length += target.text.length;
                    }
                }
                const values = await reference.invokeMethodAsync("TranslateBatch", [...entries.values()], code);
                if (!Array.isArray(values) || values.length !== entries.size || values.some(x => typeof x !== "string" || !x.trim()))
                    throw new Error("Incomplete translation response");
                if (revision !== version || !active) break;
                [...entries.keys()].forEach((key, index) => cache.set(key, values[index]));
                // Bound the browser cache independently from the server's bounded cache.
                while (cache.size > 1000) cache.delete(cache.keys().next().value);
                batch.forEach(target => apply(target, cache.get(target.key), revision));
            }
            if (revision === version) showStatus(false, false);
        } catch {
            if (active && revision === version) { failed = true; showStatus(true, false); }
        } finally {
            busy = false;
            if (dirty && active && !failed) schedule();
        }
    }
    function setLanguage(next, translations) {
        language = next;
        catalog = Object.fromEntries(Object.entries(translations || {}).map(([key, value]) => [key.toLowerCase(), value]));
        version++;
        failed = false;
        document.documentElement.lang = language.code === "zh" ? "zh-CN" : language.code;
        document.documentElement.dir = language.rightToLeft ? "rtl" : "ltr";
        try { localStorage.setItem("nuctiusnow_language", language.code); } catch { }
        // Stop reading in the previous language immediately.
        window.nuctiusTts?.stop();
        schedule();
    }
    return {
        start(dotnet, next, translations) {
            reference = dotnet;
            active = true;
            observer?.disconnect();
            observer = new MutationObserver(records => {
                if (records.some(record => {
                    const element = record.target.nodeType === Node.TEXT_NODE ? record.target.parentElement : record.target;
                    return !element?.closest?.(excluded);
                })) schedule();
            });
            observer.observe(document.documentElement, { subtree: true, childList: true, characterData: true,
                attributes: true, attributeFilter: attributes });
            setLanguage(next, translations);
        },
        setLanguage,
        browserLanguage() { return navigator.language || "en"; },
        stop() { active = false; version++; clearTimeout(timer); observer?.disconnect(); status?.remove(); status = null; },
        async articleText() {
            // Wait for any pending translation before reading the displayed article.
            const start = Date.now();
            while ((busy || dirty) && !failed && Date.now() - start < 120000)
                await new Promise(resolve => setTimeout(resolve, 100));
            if (failed || busy || dirty) throw new Error("Article translation is not ready.");
            return [...document.querySelectorAll(".detail-title,.summary-section [data-news-text],.full-article-body")]
                .map(element => element.textContent.trim()).filter(Boolean).join("\n\n");
        }
    };
})();
