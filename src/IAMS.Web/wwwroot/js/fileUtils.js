// File utilities for IAMS
// Trigger a file input element - used for camera capture button
window.triggerFileInput = function(inputId) {
    const input = document.getElementById(inputId);
    if (input) {
        input.click();
    }
};

// Download a data URL as a file
window.downloadFile = function(dataUrl, fileName) {
    const link = document.createElement('a');
    link.href = dataUrl;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};

// Print a specific element by ID
window.printElement = function(elementId) {
    const element = document.getElementById(elementId);
    if (!element) return;

    const printWindow = window.open('', '_blank');
    if (!printWindow) return;

    printWindow.document.write(`
        <!DOCTYPE html>
        <html>
        <head>
            <title>Print</title>
            <link href="css/app.css" rel="stylesheet" />
            <style>
                body {
                    margin: 0;
                    padding: 20px;
                    font-family: system-ui, -apple-system, sans-serif;
                }
                @media print {
                    body { padding: 0; }
                }
            </style>
        </head>
        <body>
            ${element.outerHTML}
        </body>
        </html>
    `);

    printWindow.document.close();
    printWindow.focus();

    // Wait for styles to load before printing
    setTimeout(() => {
        printWindow.print();
        printWindow.close();
    }, 500);
};
