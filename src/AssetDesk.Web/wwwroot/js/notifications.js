// Server-Sent Events notification handler
window.notificationService = {
    eventSource: null,
    dotNetRef: null,

    // Start SSE connection
    start: function (url, dotNetReference) {
        this.stop(); // Close any existing connection

        this.dotNetRef = dotNetReference;

        try {
            this.eventSource = new EventSource(url, { withCredentials: true });

            this.eventSource.addEventListener('connected', (event) => {
                const data = JSON.parse(event.data);
                console.log('SSE Connected:', data);
                this.dotNetRef.invokeMethodAsync('OnConnected');
            });

            this.eventSource.addEventListener('notification', (event) => {
                const notification = JSON.parse(event.data);
                console.log('SSE Notification:', notification);
                this.dotNetRef.invokeMethodAsync('OnNotificationReceived', notification);
            });

            this.eventSource.addEventListener('heartbeat', (event) => {
                console.log('SSE Heartbeat:', event.data);
            });

            this.eventSource.onerror = (error) => {
                console.error('SSE Error:', error);
                if (this.eventSource.readyState === EventSource.CLOSED) {
                    this.dotNetRef.invokeMethodAsync('OnDisconnected');
                    // Attempt to reconnect after 5 seconds
                    setTimeout(() => {
                        if (this.dotNetRef) {
                            this.dotNetRef.invokeMethodAsync('OnReconnecting');
                        }
                    }, 5000);
                }
            };

            this.eventSource.onopen = () => {
                console.log('SSE Connection opened');
            };

            return true;
        } catch (error) {
            console.error('Failed to start SSE:', error);
            return false;
        }
    },

    // Stop SSE connection
    stop: function () {
        if (this.eventSource) {
            this.eventSource.close();
            this.eventSource = null;
        }
        this.dotNetRef = null;
    },

    // Check if connected
    isConnected: function () {
        return this.eventSource !== null && this.eventSource.readyState === EventSource.OPEN;
    }
};

// Web App Badging API — sets a numeric badge on the installed-PWA taskbar/dock icon.
// Supported in Chromium-based browsers (Chrome, Edge) on Windows/macOS/Android.
// No-ops silently on browsers without support (Firefox, Safari).
window.appBadge = {
    set: function (count) {
        try {
            if (!('setAppBadge' in navigator)) return false;
            const n = Math.max(0, Math.floor(Number(count) || 0));
            if (n === 0) {
                navigator.clearAppBadge();
            } else {
                navigator.setAppBadge(n);
            }
            return true;
        } catch (e) {
            console.warn('App badge update failed:', e);
            return false;
        }
    },
    clear: function () {
        try {
            if ('clearAppBadge' in navigator) navigator.clearAppBadge();
        } catch { /* ignore */ }
    }
};
