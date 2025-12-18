// Print utility functions for IAMS

window.printElement = function(elementId) {
    const element = document.getElementById(elementId);
    if (!element) {
        console.error('Element not found:', elementId);
        return;
    }

    // Create a new window for printing
    const printWindow = window.open('', '_blank', 'width=800,height=600');

    // Build print document with inline styles for reliability
    printWindow.document.write(`
        <!DOCTYPE html>
        <html>
        <head>
            <title>Print</title>
            <style>
                * {
                    margin: 0;
                    padding: 0;
                    box-sizing: border-box;
                }
                body {
                    font-family: system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
                    background: white;
                    color: #1e293b;
                    -webkit-print-color-adjust: exact !important;
                    print-color-adjust: exact !important;
                }
                .print-container {
                    padding: 40px;
                    display: flex;
                    flex-direction: column;
                    align-items: center;
                    justify-content: center;
                    min-height: 100vh;
                }
                /* Label styles */
                .label-content {
                    display: flex;
                    flex-direction: column;
                    align-items: center;
                    text-align: center;
                    gap: 16px;
                }
                .qr-container {
                    border: 2px solid #e2e8f0;
                    border-radius: 8px;
                    padding: 16px;
                    display: flex;
                    align-items: center;
                    justify-content: center;
                }
                .qr-container img {
                    width: 160px;
                    height: 160px;
                    display: block;
                }
                .asset-tag {
                    font-size: 24px;
                    font-family: ui-monospace, monospace;
                    font-weight: bold;
                    color: #0f172a;
                }
                .asset-name {
                    font-size: 14px;
                    color: #475569;
                    margin-top: 4px;
                }
                .serial-number {
                    font-size: 12px;
                    font-family: ui-monospace, monospace;
                    color: #64748b;
                    margin-top: 4px;
                }
                /* Report styles */
                .report-content {
                    max-width: 800px;
                    margin: 0 auto;
                }
                .report-header {
                    display: flex;
                    justify-content: space-between;
                    align-items: flex-start;
                    border-bottom: 1px solid #e2e8f0;
                    padding-bottom: 16px;
                    margin-bottom: 24px;
                }
                .report-title {
                    font-size: 20px;
                    font-weight: bold;
                }
                .report-date {
                    font-size: 14px;
                    color: #64748b;
                    margin-top: 4px;
                }
                .report-section {
                    margin-bottom: 24px;
                }
                .report-section-title {
                    font-size: 12px;
                    font-weight: 600;
                    text-transform: uppercase;
                    letter-spacing: 0.05em;
                    margin-bottom: 12px;
                    color: #0f172a;
                }
                .report-grid {
                    display: grid;
                    grid-template-columns: repeat(2, 1fr);
                    gap: 16px;
                    font-size: 14px;
                }
                .report-label {
                    color: #64748b;
                }
                .report-value {
                    color: #0f172a;
                    margin-left: 8px;
                }
                @media print {
                    body {
                        -webkit-print-color-adjust: exact !important;
                        print-color-adjust: exact !important;
                    }
                    .print-container {
                        padding: 20px;
                        min-height: auto;
                    }
                }
            </style>
        </head>
        <body>
            <div class="print-container">
                ${elementId === 'asset-label' ? formatLabelContent(element) : formatReportContent(element)}
            </div>
        </body>
        </html>
    `);

    printWindow.document.close();

    // Wait for content and images to load then print
    printWindow.onload = function() {
        // Wait for images to load
        const images = printWindow.document.images;
        let loadedImages = 0;
        const totalImages = images.length;

        if (totalImages === 0) {
            setTimeout(() => {
                printWindow.focus();
                printWindow.print();
                printWindow.close();
            }, 100);
            return;
        }

        for (let img of images) {
            if (img.complete) {
                loadedImages++;
                if (loadedImages === totalImages) {
                    setTimeout(() => {
                        printWindow.focus();
                        printWindow.print();
                        printWindow.close();
                    }, 100);
                }
            } else {
                img.onload = img.onerror = function() {
                    loadedImages++;
                    if (loadedImages === totalImages) {
                        setTimeout(() => {
                            printWindow.focus();
                            printWindow.print();
                            printWindow.close();
                        }, 100);
                    }
                };
            }
        }
    };
};

function formatLabelContent(element) {
    const img = element.querySelector('img');
    const imgSrc = img ? img.src : '';

    const texts = element.querySelectorAll('p');
    const assetTag = texts[0] ? texts[0].textContent : '';
    const assetName = texts[1] ? texts[1].textContent : '';
    const serialNumber = texts[2] ? texts[2].textContent : '';

    return `
        <div class="label-content">
            <div class="qr-container">
                <img src="${imgSrc}" alt="QR Code" />
            </div>
            <div>
                <p class="asset-tag">${assetTag}</p>
                <p class="asset-name">${assetName}</p>
                ${serialNumber ? `<p class="serial-number">${serialNumber}</p>` : ''}
            </div>
        </div>
    `;
}

function formatReportContent(element) {
    // For reports, use the original HTML with some cleanup
    return `<div class="report-content">${element.innerHTML}</div>`;
}
