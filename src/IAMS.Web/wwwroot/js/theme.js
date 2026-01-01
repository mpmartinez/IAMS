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

// Auth guard - checks if user is logged out and redirects
window.authGuard = {
    isAuthenticated: function() {
        return localStorage.getItem('authToken') !== null;
    },

    checkAuthOnVisibility: function() {
        document.addEventListener('visibilitychange', function() {
            if (document.visibilityState === 'visible') {
                if (!window.authGuard.isAuthenticated()) {
                    // User is not authenticated, redirect to login
                    if (!window.location.pathname.includes('/login') &&
                        !window.location.pathname.includes('/forgot-password') &&
                        !window.location.pathname.includes('/reset-password')) {
                        window.location.href = '/login';
                    }
                }
            }
        });
    },

    // Called on popstate (back/forward button)
    checkAuthOnNavigation: function() {
        window.addEventListener('popstate', function() {
            if (!window.authGuard.isAuthenticated()) {
                if (!window.location.pathname.includes('/login') &&
                    !window.location.pathname.includes('/forgot-password') &&
                    !window.location.pathname.includes('/reset-password')) {
                    window.location.href = '/login';
                }
            }
        });
    },

    init: function() {
        this.checkAuthOnVisibility();
        this.checkAuthOnNavigation();
    }
};

// Initialize auth guard
window.authGuard.init();
