window.terminalInterop = {
    // Manual fit: calculate cols/rows from container size and resize the terminal
    fitTerminal: function (terminalElementId) {
        const container = document.getElementById(terminalElementId);
        if (!container) return { cols: 80, rows: 24 };

        const xtermEl = container.querySelector('.xterm-screen');
        if (!xtermEl) return { cols: 80, rows: 24 };

        // Measure a single character by looking at the xterm core dimensions
        const core = container.querySelector('.xterm-rows');
        if (!core || !core.firstChild) return { cols: 80, rows: 24 };

        // Create a temp span to measure character size
        const span = document.createElement('span');
        span.style.visibility = 'hidden';
        span.style.position = 'absolute';
        span.textContent = 'W';
        core.appendChild(span);
        const charWidth = span.getBoundingClientRect().width;
        const charHeight = span.getBoundingClientRect().height;
        core.removeChild(span);

        if (charWidth === 0 || charHeight === 0) return { cols: 80, rows: 24 };

        const containerWidth = container.clientWidth;
        const containerHeight = container.clientHeight;

        const cols = Math.max(2, Math.floor(containerWidth / charWidth) - 1);
        const rows = Math.max(1, Math.floor(containerHeight / charHeight));

        return { cols: cols, rows: rows };
    },

    setupResizeObserver: function (terminalElementId, dotnetRef) {
        const el = document.getElementById(terminalElementId);
        if (!el) return;

        let resizeTimeout = null;
        const debounceResize = () => {
            if (resizeTimeout) clearTimeout(resizeTimeout);
            resizeTimeout = setTimeout(() => {
                dotnetRef.invokeMethodAsync('OnContainerResized');
            }, 100);
        };

        const resizeObserver = new ResizeObserver(debounceResize);
        resizeObserver.observe(el);

        window.addEventListener('resize', debounceResize);

        // Use visualViewport API to handle mobile keyboard show/hide
        if (window.visualViewport) {
            const page = document.querySelector('.terminal-page');
            const applyViewport = () => {
                const vv = window.visualViewport;
                if (page) {
                    page.style.height = vv.height + 'px';
                    page.style.maxHeight = vv.height + 'px';
                }
                debounceResize();
            };
            window.visualViewport.addEventListener('resize', applyViewport);
            window.visualViewport.addEventListener('scroll', applyViewport);
            // Apply once on setup
            applyViewport();
        }
    },

    focusTerminal: function (terminalElementId) {
        const el = document.getElementById(terminalElementId);
        if (el) {
            const textarea = el.querySelector('.xterm-helper-textarea');
            if (textarea) textarea.focus();
        }
    },

    preventDefaultTouchHandlers: function (terminalElementId) {
        const el = document.getElementById(terminalElementId);
        if (!el) return;

        el.addEventListener('touchmove', function (e) {
            if (e.touches.length > 1) {
                e.preventDefault();
            }
        }, { passive: false });
    }
};
