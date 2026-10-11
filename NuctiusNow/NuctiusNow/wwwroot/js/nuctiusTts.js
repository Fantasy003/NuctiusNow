window.nuctiusTts = {
    _dotNetRef: null,
    _utterance: null,

    register: function (dotNetRef) {
        this._dotNetRef = dotNetRef;
        return true;
    },

    speak: function (text, rate, language) {
        if (!text || !window.speechSynthesis) {
            return false;
        }

        this.stop();

        const utterance = new SpeechSynthesisUtterance(text);

        utterance.lang = language || document.documentElement.lang || "en-US";
        utterance.rate = Number(rate) || 1.0;
        utterance.pitch = 1.0;
        utterance.volume = 1.0;

        utterance.onstart = () => {
            this._notify("OnTtsStarted");
        };

        utterance.onpause = () => {
            this._notify("OnTtsPaused");
        };

        utterance.onresume = () => {
            this._notify("OnTtsResumed");
        };

        utterance.onend = () => {
            this._notify("OnTtsEnded");
        };

        utterance.onerror = () => {
            this._notify("OnTtsEnded");
        };

        this._utterance = utterance;

        window.speechSynthesis.speak(utterance);

        return true;
    },

    pause: function () {
        if (!window.speechSynthesis) {
            return;
        }

        if (window.speechSynthesis.speaking) {
            window.speechSynthesis.pause();
        }
    },

    resume: function () {
        if (!window.speechSynthesis) {
            return;
        }

        if (window.speechSynthesis.paused) {
            window.speechSynthesis.resume();
        }
    },

    stop: function () {
        if (!window.speechSynthesis) {
            return;
        }

        window.speechSynthesis.cancel();
        this._utterance = null;
    },

    isSpeaking: function () {
        return !!window.speechSynthesis &&
            window.speechSynthesis.speaking;
    },

    isPaused: function () {
        return !!window.speechSynthesis &&
            window.speechSynthesis.paused;
    },

    _notify: function (methodName) {
        if (!this._dotNetRef) {
            return;
        }

        try {
            this._dotNetRef.invokeMethodAsync(methodName);
        }
        catch {
            // Blazor circuit may already be disconnected.
        }
    },

    dispose: function () {
        this.stop();
        this._dotNetRef = null;
    }
};