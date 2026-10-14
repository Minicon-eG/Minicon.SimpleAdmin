// SimpleAdmin WebUI — page-independent helpers: theme switch, relative times, auto-refresh.

(function () {
    'use strict';

    // ---------- Theme (light / dark) ----------
    // The initial theme is set inline in <head> (no flash); this only wires the toggle.

    function currentTheme() {
        return document.documentElement.getAttribute('data-bs-theme') === 'dark' ? 'dark' : 'light';
    }

    function applyTheme(theme) {
        document.documentElement.setAttribute('data-bs-theme', theme);
        try { localStorage.setItem('sa-theme', theme); } catch { /* private mode */ }
        document.querySelectorAll('[data-theme-toggle] i').forEach(icon => {
            icon.className = theme === 'dark' ? 'bi bi-sun' : 'bi bi-moon-stars';
        });
        document.querySelectorAll('[data-theme-toggle]').forEach(button => {
            button.title = theme === 'dark' ? 'Helles Design' : 'Dunkles Design';
        });
    }

    // ---------- Relative times: <time class="relative-time" data-utc="ISO"> ----------

    const rtf = new Intl.RelativeTimeFormat('de', { numeric: 'auto' });
    const units = [['day', 86400], ['hour', 3600], ['minute', 60]];

    function renderRelativeTimes(root) {
        (root || document).querySelectorAll('.relative-time[data-utc]').forEach(el => {
            const seconds = (new Date(el.dataset.utc) - Date.now()) / 1000;
            if (isNaN(seconds)) return;
            if (Math.abs(seconds) < 60) { el.textContent = 'gerade eben'; return; }
            const [unit, size] = units.find(([, s]) => Math.abs(seconds) >= s) || ['minute', 60];
            el.textContent = rtf.format(Math.round(seconds / size), unit);
        });
    }

    // ---------- Auto-refresh: <div data-auto-refresh> (see _AutoRefresh.cshtml) ----------
    // Reloads the page every N seconds while enabled (stored per browser). Pauses while a dialog
    // is open, a form field has focus or the tab is hidden, so nobody loses input.

    const refreshKey = 'sa-autorefresh';

    function refreshBlocked() {
        if (document.hidden) return true;
        if (document.querySelector('.modal.show')) return true;
        const active = document.activeElement;
        return !!active && ['INPUT', 'TEXTAREA', 'SELECT'].includes(active.tagName) && active.type !== 'checkbox';
    }

    function initAutoRefresh(host) {
        const interval = parseInt(host.dataset.autoRefresh, 10) || 60;
        const toggle = host.querySelector('[data-auto-refresh-toggle]');
        const label = host.querySelector('[data-auto-refresh-label]');
        let enabled = false;
        try { enabled = localStorage.getItem(refreshKey) === 'on'; } catch { /* private mode */ }
        let remaining = interval;

        function render() {
            toggle.classList.toggle('active', enabled);
            toggle.setAttribute('aria-pressed', enabled ? 'true' : 'false');
            label.textContent = enabled ? remaining + ' s' : 'Aus';
        }

        toggle.addEventListener('click', () => {
            enabled = !enabled;
            remaining = interval;
            try { localStorage.setItem(refreshKey, enabled ? 'on' : 'off'); } catch { /* private mode */ }
            render();
        });

        setInterval(() => {
            if (!enabled) return;
            if (refreshBlocked()) { label.textContent = 'pausiert'; return; }
            remaining--;
            if (remaining <= 0) { location.reload(); return; }
            render();
        }, 1000);

        render();
    }

    // ---------- Open problems badge in the navigation ----------

    async function loadOpenProblemCount() {
        const badge = document.querySelector('[data-open-problems]');
        if (!badge) return;
        try {
            const response = await fetch(badge.dataset.openProblems, { headers: { 'Accept': 'application/json' } });
            if (!response.ok) return;
            const items = await response.json();
            const open = items.filter(i => i.problem && !i.problem.acknowledged);
            if (open.length === 0) return;
            // Severity may arrive as enum name or number (Info=0, Warning=1, Critical=2)
            const critical = open.some(i => i.problem.severity === 'Critical' || i.problem.severity === 2);
            badge.textContent = open.length;
            badge.classList.toggle('bg-danger', critical);
            badge.classList.toggle('bg-warning', !critical);
            badge.classList.remove('d-none');
        } catch { /* badge is optional */ }
    }

    document.addEventListener('DOMContentLoaded', () => {
        applyTheme(currentTheme());
        document.querySelectorAll('[data-theme-toggle]').forEach(button =>
            button.addEventListener('click', () => applyTheme(currentTheme() === 'dark' ? 'light' : 'dark')));

        renderRelativeTimes();
        setInterval(renderRelativeTimes, 30000);

        document.querySelectorAll('[data-auto-refresh]').forEach(initAutoRefresh);
        loadOpenProblemCount();
    });

    window.simpleAdmin = { renderRelativeTimes, applyTheme };
})();
