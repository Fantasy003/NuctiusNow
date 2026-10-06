window.newsDetailScroll = {
    registerScrollEnd: function (dotNetRef) {
        if (window.__newsDetailScrollHandler) {
            window.removeEventListener('scroll', window.__newsDetailScrollHandler);
        }

        window.__newsDetailScrollHandler = function () {
            const doc = document.documentElement;
            const atBottom = window.innerHeight + window.scrollY >= doc.scrollHeight - 80;
            if (atBottom) {
                dotNetRef.invokeMethodAsync('OnScrollReachedEnd');
                window.removeEventListener('scroll', window.__newsDetailScrollHandler);
                window.__newsDetailScrollHandler = null;
            }
        };

        window.addEventListener('scroll', window.__newsDetailScrollHandler, { passive: true });
    },
    dispose: function () {
        if (window.__newsDetailScrollHandler) {
            window.removeEventListener('scroll', window.__newsDetailScrollHandler);
            window.__newsDetailScrollHandler = null;
        }
    }
};
window.nuctiusTts = {
    utterance: null,
    dotNetRef: null,

    register: function (dotNetRef) {
        this.dotNetRef = dotNetRef;
    },

    speak: function (text, rate, language) {
        if (!text || !("speechSynthesis" in window)) {
            return false;
        }

        window.speechSynthesis.cancel();

        const utterance = new SpeechSynthesisUtterance(text);

        utterance.rate = rate || 1.0;
        utterance.pitch = 1.0;
        utterance.volume = 1.0;

        utterance.lang = language || "en-US";

        utterance.onend = () => {
            if (this.dotNetRef) {
                this.dotNetRef.invokeMethodAsync("OnTtsEnded")
                    .catch(() => { });
            }
        };

        utterance.onerror = () => {
            if (this.dotNetRef) {
                this.dotNetRef.invokeMethodAsync("OnTtsEnded")
                    .catch(() => { });
            }
        };

        this.utterance = utterance;

        window.speechSynthesis.speak(utterance);

        return true;
    },

    pause: function () {
        if ("speechSynthesis" in window) {
            window.speechSynthesis.pause();
        }
    },

    resume: function () {
        if ("speechSynthesis" in window) {
            window.speechSynthesis.resume();
        }
    },

    stop: function () {
        if ("speechSynthesis" in window) {
            window.speechSynthesis.cancel();
        }

        this.utterance = null;
    }
};