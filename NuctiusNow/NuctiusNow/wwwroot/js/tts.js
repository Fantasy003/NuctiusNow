window.nuctiusTts = {

    utterance: null,
    queue: [],

    speak: function (text, lang, rate) {

        if (!text || !window.speechSynthesis)
            return false;

        this.stop();

        this.queue = this.splitText(text);

        this.playNext(lang || "en-US", rate || 1.0);

        return true;
    },

    playNext: function (lang, rate) {

        if (this.queue.length === 0)
            return;

        const text = this.queue.shift();

        this.utterance = new SpeechSynthesisUtterance(text);

        this.utterance.lang = lang;
        this.utterance.rate = rate;
        this.utterance.pitch = 1.0;

        this.utterance.onend = () => {
            this.playNext(lang, rate);
        };

        window.speechSynthesis.speak(this.utterance);
    },

    pause: function () {
        if (window.speechSynthesis)
            window.speechSynthesis.pause();
    },

    resume: function () {
        if (window.speechSynthesis)
            window.speechSynthesis.resume();
    },

    stop: function () {

        this.queue = [];

        if (window.speechSynthesis)
            window.speechSynthesis.cancel();
    },

    splitText: function (text) {

        const sentences = text.match(/[^.!?]+[.!?]+/g);

        if (!sentences)
            return [text];

        const chunks = [];
        let current = "";

        for (const sentence of sentences) {

            if ((current + sentence).length > 300) {

                if (current.trim())
                    chunks.push(current.trim());

                current = sentence;
            }
            else {
                current += sentence;
            }
        }

        if (current.trim())
            chunks.push(current.trim());

        return chunks;
    }
};