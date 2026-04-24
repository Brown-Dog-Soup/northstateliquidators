const { chromium } = require('playwright');
const path = require('path');

(async () => {
  const inputHtml = process.argv[2] || 'NSL_Services_Proposal.html';
  const outputPdf = process.argv[3] || inputHtml.replace(/\.html$/, '.pdf');
  const inputAbs = path.resolve(__dirname, inputHtml);
  const pdfPath = path.resolve(__dirname, outputPdf);

  const browser = await chromium.launch();
  const page = await browser.newPage();
  await page.goto('file://' + inputAbs.replace(/\\/g, '/'), { waitUntil: 'networkidle' });

  const footerTemplate = `
    <div style="width: 100%; padding: 0 0.55in; font-size: 7.5pt; color: #6b7280;
                font-family: Helvetica, Arial, sans-serif; display: flex;
                justify-content: space-between; border-top: 1px solid #e5e7eb; padding-top: 4px;">
      <span>TenantIQ Pro LLC | tenantiqpro.com | jeffrey.blanchard@tenantiqpro.com</span>
      <span>NSL Services Proposal | NSL-202604-0001 | Page <span class="pageNumber"></span> of <span class="totalPages"></span></span>
    </div>`;

  await page.pdf({
    path: pdfPath,
    format: 'Letter',
    printBackground: true,
    displayHeaderFooter: true,
    headerTemplate: '<div></div>',
    footerTemplate,
    margin: { top: '0.5in', bottom: '0.55in', left: '0.55in', right: '0.55in' }
  });
  await browser.close();
  console.log('PDF written: ' + pdfPath);
})();
