// Theme management for dark mode support
window.themeManager = {
    setTheme: function(isDark) {
        if (isDark) {
            document.documentElement.classList.add('dark');
        } else {
            document.documentElement.classList.remove('dark');
        }
        localStorage.setItem('theme', isDark ? 'dark' : 'light');
    },

    getTheme: function() {
        const stored = localStorage.getItem('theme');
        if (stored) return stored;
        // Fallback to system preference
        return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    },

    initTheme: function() {
        const theme = this.getTheme();
        if (theme === 'dark') {
            document.documentElement.classList.add('dark');
        }
        return theme;
    }
};

// Initialize theme on page load (before Blazor renders to prevent flash)
window.themeManager.initTheme();
