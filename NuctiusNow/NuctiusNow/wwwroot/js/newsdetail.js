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
