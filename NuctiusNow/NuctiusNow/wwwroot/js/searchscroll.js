window.newsSearchScroll = {
    _handler: null,
    _dotNetRef: null,
    _notificationPending: false,

    register: function (dotNetRef) {
        this.dispose();
        this._dotNetRef = dotNetRef;

        this._handler = function () {
            const doc = document.documentElement;
            const atBottom = window.innerHeight + window.scrollY >= doc.scrollHeight - 220;
            if (atBottom && window.newsSearchScroll._dotNetRef && !window.newsSearchScroll._notificationPending) {
                window.newsSearchScroll._notificationPending = true;
                window.newsSearchScroll._dotNetRef.invokeMethodAsync('OnSearchReachedEnd')
                    .catch(() => {})
                    .finally(() => { window.newsSearchScroll._notificationPending = false; });
            }
        };

        window.addEventListener('scroll', this._handler, { passive: true });
    },

    dispose: function () {
        if (this._handler) {
            window.removeEventListener('scroll', this._handler);
            this._handler = null;
        }
        if (this._dotNetRef) {
            this._dotNetRef.dispose();
            this._dotNetRef = null;
        }
    }
};
