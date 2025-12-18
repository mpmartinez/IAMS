// QR Scanner JavaScript Interop for Blazor WASM
window.QrScanner = {
    scanner: null,
    dotNetRef: null,
    isScanning: false,

    // Initialize the scanner
    init: async function (dotNetReference, videoElementId) {
        this.dotNetRef = dotNetReference;

        try {
            // Check if camera is available
            const devices = await navigator.mediaDevices.enumerateDevices();
            const cameras = devices.filter(d => d.kind === 'videoinput');

            if (cameras.length === 0) {
                await this.dotNetRef.invokeMethodAsync('OnScanError', 'No camera found on this device');
                return false;
            }

            // Create scanner instance
            this.scanner = new Html5Qrcode(videoElementId);
            return true;
        } catch (error) {
            console.error('QR Scanner init error:', error);
            await this.dotNetRef.invokeMethodAsync('OnScanError', 'Failed to initialize camera: ' + error.message);
            return false;
        }
    },

    // Start scanning
    start: async function () {
        if (!this.scanner || this.isScanning) return false;

        try {
            const config = {
                fps: 10,
                qrbox: { width: 250, height: 250 },
                aspectRatio: 1.0,
                disableFlip: false,
                experimentalFeatures: {
                    useBarCodeDetectorIfSupported: true
                }
            };

            await this.scanner.start(
                { facingMode: "environment" }, // Prefer back camera
                config,
                async (decodedText, decodedResult) => {
                    // Success callback
                    await this.onScanSuccess(decodedText);
                },
                (errorMessage) => {
                    // Ignore continuous scan errors (no QR found in frame)
                }
            );

            this.isScanning = true;
            return true;
        } catch (error) {
            console.error('QR Scanner start error:', error);

            // Try front camera if back camera fails
            try {
                await this.scanner.start(
                    { facingMode: "user" },
                    {
                        fps: 10,
                        qrbox: { width: 250, height: 250 },
                        aspectRatio: 1.0
                    },
                    async (decodedText) => {
                        await this.onScanSuccess(decodedText);
                    },
                    () => {}
                );
                this.isScanning = true;
                return true;
            } catch (fallbackError) {
                await this.dotNetRef.invokeMethodAsync('OnScanError', 'Camera access denied or unavailable');
                return false;
            }
        }
    },

    // Handle successful scan
    onScanSuccess: async function (decodedText) {
        if (!this.dotNetRef) return;

        // Pause scanning to prevent multiple scans
        await this.pause();

        // Trigger haptic feedback
        this.triggerHaptic();

        // Extract asset tag from URL or use raw value
        let assetTag = decodedText;

        // Check if it's a URL and extract asset tag
        const urlPattern = /\/assets\/scan\/([^\/\?]+)/;
        const match = decodedText.match(urlPattern);
        if (match) {
            assetTag = decodeURIComponent(match[1]);
        }

        // Notify Blazor
        await this.dotNetRef.invokeMethodAsync('OnScanSuccess', assetTag);
    },

    // Pause scanning (keeps camera running)
    pause: async function () {
        if (this.scanner && this.isScanning) {
            try {
                await this.scanner.pause(true);
            } catch (e) {
                console.log('Pause error (may already be paused):', e);
            }
        }
    },

    // Resume scanning
    resume: async function () {
        if (this.scanner && this.isScanning) {
            try {
                await this.scanner.resume();
            } catch (e) {
                console.log('Resume error:', e);
            }
        }
    },

    // Stop and cleanup
    stop: async function () {
        if (this.scanner) {
            try {
                if (this.isScanning) {
                    await this.scanner.stop();
                }
                this.scanner.clear();
            } catch (error) {
                console.log('Stop error:', error);
            }
            this.isScanning = false;
            this.scanner = null;
        }
        this.dotNetRef = null;
    },

    // Trigger haptic feedback
    triggerHaptic: function () {
        if ('vibrate' in navigator) {
            // Short vibration pattern for success
            navigator.vibrate([50, 30, 50]);
        }
    },

    // Trigger error haptic
    triggerErrorHaptic: function () {
        if ('vibrate' in navigator) {
            // Longer vibration for error
            navigator.vibrate(200);
        }
    },

    // Check if device has camera
    hasCamera: async function () {
        try {
            const devices = await navigator.mediaDevices.enumerateDevices();
            return devices.some(d => d.kind === 'videoinput');
        } catch {
            return false;
        }
    },

    // Toggle flashlight (torch)
    toggleFlash: async function () {
        if (!this.scanner || !this.isScanning) return false;

        try {
            const track = this.scanner.getRunningTrackSettings();
            if (track && 'torch' in track) {
                const capabilities = await this.scanner.getRunningTrackCapabilities();
                if (capabilities && capabilities.torch) {
                    const currentState = track.torch || false;
                    await this.scanner.applyVideoConstraints({
                        advanced: [{ torch: !currentState }]
                    });
                    return !currentState;
                }
            }
        } catch (e) {
            console.log('Flash toggle not supported:', e);
        }
        return false;
    }
};

// Download file with authentication header
window.downloadWithAuth = async function (url, token) {
    try {
        const response = await fetch(url, {
            method: 'GET',
            headers: {
                'Authorization': `Bearer ${token}`
            }
        });

        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }

        // Get filename from Content-Disposition header or URL
        const contentDisposition = response.headers.get('Content-Disposition');
        let filename = 'report.csv';
        if (contentDisposition) {
            const match = contentDisposition.match(/filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/);
            if (match && match[1]) {
                filename = match[1].replace(/['"]/g, '');
            }
        }

        // Create blob and download
        const blob = await response.blob();
        const downloadUrl = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = downloadUrl;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        window.URL.revokeObjectURL(downloadUrl);
        document.body.removeChild(a);
    } catch (error) {
        console.error('Download failed:', error);
        alert('Failed to download report. Please try again.');
    }
};
