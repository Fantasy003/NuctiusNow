window.newsSearchScroll = {
    _handler: null,
    _dotNetRef: null,
    _notificationPending: false,

    register: function (dotNetRef) {
        this.dispose();
        this._dotNetRef = dotNetRef;

        this._handler = function () {
            const documentElement = document.documentElement;
            const body = document.body;
            const scrollHeight = Math.max(
                documentElement ? documentElement.scrollHeight : 0,
                body ? body.scrollHeight : 0);
            const atBottom = window.innerHeight + window.scrollY >= scrollHeight - 240;

            if (atBottom &&
                window.newsSearchScroll._dotNetRef &&
                !window.newsSearchScroll._notificationPending) {
                window.newsSearchScroll._notificationPending = true;

                window.newsSearchScroll._dotNetRef
                    .invokeMethodAsync('OnSearchReachedEnd')
                    .catch(() => {})
                    .finally(() => {
                        window.newsSearchScroll._notificationPending = false;
                    });
            }
        };

        window.addEventListener('scroll', this._handler, { passive: true });

        // Also check immediately after registration. This matters when the initial
        // result set is shorter than one viewport and the user is already at the end.
        setTimeout(this._handler, 250);
    },

    dispose: function () {
        if (this._handler) {
            window.removeEventListener('scroll', this._handler);
            this._handler = null;
        }

        // Do NOT call dotNetRef.dispose() here. The DotNetObjectReference is owned
        // and disposed by the Blazor component.
        this._dotNetRef = null;
        this._notificationPending = false;
    }
};
