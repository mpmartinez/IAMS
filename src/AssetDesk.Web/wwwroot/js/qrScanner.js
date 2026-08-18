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

    // Scan QR code from uploaded image file
    scanFile: async function (file) {
        if (!file) return null;

        try {
            // Create a temporary scanner instance for file scanning
            const html5QrCode = new Html5Qrcode("qr-file-scanner-temp");

            const result = await html5QrCode.scanFile(file, /* showImage */ false);

            // Extract asset tag from URL or use raw value
            let assetTag = result;
            const urlPattern = /\/assets\/scan\/([^\/\?]+)/;
            const match = result.match(urlPattern);
            if (match) {
                assetTag = decodeURIComponent(match[1]);
            }

            // Trigger haptic feedback
            this.triggerHaptic();

            return assetTag;
        } catch (error) {
            console.error('QR file scan error:', error);
            return null;
        }
    },

    // Set up file input listeners for QR upload
    setupFileInputs: function () {
        const self = this;
        const fileInputIds = ['qr-file-input', 'qr-file-input-error'];

        fileInputIds.forEach(id => {
            const input = document.getElementById(id);
            if (input && !input._qrListenerAttached) {
                input._qrListenerAttached = true;
                input.addEventListener('change', async (e) => {
                    const file = e.target.files[0];
                    if (!file) return;

                    // Reset the input so the same file can be selected again
                    e.target.value = '';

                    // Notify Blazor we're processing
                    if (self.dotNetRef) {
                        await self.dotNetRef.invokeMethodAsync('OnFileProcessingStart');
                    }

                    try {
                        const assetTag = await self.scanFileNative(file);
                        if (assetTag && self.dotNetRef) {
                            await self.dotNetRef.invokeMethodAsync('OnScanSuccess', assetTag);
                        } else if (self.dotNetRef) {
                            await self.dotNetRef.invokeMethodAsync('OnFileScanFailed', 'No QR code found in the image');
                        }
                    } catch (error) {
                        console.error('File scan error:', error);
                        if (self.dotNetRef) {
                            await self.dotNetRef.invokeMethodAsync('OnFileScanFailed', error.message || 'Failed to scan image');
                        }
                    }
                });
            }
        });
    },

    // Scan QR code from native File object
    scanFileNative: async function (file) {
        if (!file) return null;

        try {
            // Create temporary element for scanner
            let tempDiv = document.getElementById('qr-file-scanner-temp');
            if (!tempDiv) {
                tempDiv = document.createElement('div');
                tempDiv.id = 'qr-file-scanner-temp';
                tempDiv.style.display = 'none';
                document.body.appendChild(tempDiv);
            }

            // Create a temporary scanner instance for file scanning
            const html5QrCode = new Html5Qrcode("qr-file-scanner-temp");

            const result = await html5QrCode.scanFile(file, /* showImage */ false);

            // Clean up
            html5QrCode.clear();

            // Extract asset tag from URL or use raw value
            let assetTag = result;
            const urlPattern = /\/assets\/scan\/([^\/\?]+)/;
            const match = result.match(urlPattern);
            if (match) {
                assetTag = decodeURIComponent(match[1]);
            }

            // Trigger haptic feedback
            this.triggerHaptic();

            return assetTag;
        } catch (error) {
            console.error('QR file scan error:', error);
            throw error;
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
            // Try filename*= first (RFC 5987 encoded, e.g., filename*=UTF-8''Asset%20Inventory.csv)
            const utf8Match = contentDisposition.match(/filename\*=(?:UTF-8''|utf-8'')([^;\n]+)/i);
            if (utf8Match && utf8Match[1]) {
                filename = decodeURIComponent(utf8Match[1]);
            } else {
                // Fall back to regular filename= (may be quoted)
                const match = contentDisposition.match(/filename=(?:"([^"]+)"|([^;\n]+))/);
                if (match) {
                    filename = match[1] || match[2];
                }
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
