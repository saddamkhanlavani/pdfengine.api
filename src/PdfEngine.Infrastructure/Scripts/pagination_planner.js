(function() {
    // PDFEngine Pre-Render Pagination Planner Layout Engine
    const targetPageHeight = 900; // standard printable height in pixels (A4/Letter minus margins)
    const blocks = Array.from(document.querySelectorAll('body > *:not(script):not(style)'));

    let currentPageTop = 0;
    
    blocks.forEach((block) => {
        const rect = block.getBoundingClientRect();
        const relativeTop = rect.top - currentPageTop;
        const relativeBottom = relativeTop + rect.height;

        // Check if this block overflows the current page boundary
        if (relativeBottom > targetPageHeight) {
            // Is this a heading that would be orphaned?
            const isHeading = ['H1', 'H2', 'H3', 'H4', 'H5', 'H6'].includes(block.tagName);
            
            // If it is a heading, or if it is a block we want to keep whole that doesn't fit,
            // we force a page break before it.
            if (isHeading || block.classList.contains('keep-together') || block.tagName === 'TABLE') {
                block.style.breakBefore = 'page';
                currentPageTop = rect.top; // reset page boundary to this element's top
            } else {
                // For other blocks (paragraphs, divs), we let them flow, but check if we should push them
                // to avoid leaving tiny fractions of blocks.
                if (relativeTop > targetPageHeight * 0.8) {
                    block.style.breakBefore = 'page';
                    currentPageTop = rect.top;
                }
            }
        }
    });
})();
