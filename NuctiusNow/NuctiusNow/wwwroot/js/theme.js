window.nuctiusTheme = {
    set: function (theme) {
        const root = document.documentElement;
        root.classList.remove('theme-dark', 'theme-light', 'theme-retro', 'theme-office');
        root.classList.add('theme-' + (theme || 'dark'));
    }
};
