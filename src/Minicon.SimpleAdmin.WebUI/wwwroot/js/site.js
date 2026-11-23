// SimpleAdmin WebUI — page-independent helpers: theme switch, relative times, auto-refresh,
// release-notes hints ("Neu" next to the footer version, Neuigkeiten page).

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

    // ---------- Release notes: "Neu" since the last visit of the Neuigkeiten page ----------

    const seenVersionKey = 'sa-seen-version';

    function compareVersions(a, b) {
        const pa = String(a).split(/[.-]/).map(n => parseInt(n, 10) || 0);
        const pb = String(b).split(/[.-]/).map(n => parseInt(n, 10) || 0);
        for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
            const diff = (pa[i] || 0) - (pb[i] || 0);
            if (diff !== 0) return diff;
        }
        return 0;
    }

    function initReleaseNotes() {
        let seen = null;
        try { seen = localStorage.getItem(seenVersionKey); } catch { /* private mode */ }

        const page = document.querySelector('[data-changelog]');
        if (page) {
            const installed = page.dataset.installedVersion;
            // Never visited before: nothing is marked, otherwise every release would be "new".
            if (seen) {
                page.querySelectorAll('[data-release-version]').forEach(release => {
                    if (compareVersions(release.dataset.releaseVersion, seen) > 0) {
                        release.querySelector('[data-release-new]')?.classList.remove('d-none');
                        release.open = true;
                    }
                });
            }
            try { localStorage.setItem(seenVersionKey, installed); } catch { /* private mode */ }
            seen = installed;
        }

        document.querySelectorAll('[data-new-version-badge]').forEach(badge =>
            badge.classList.toggle('d-none', seen !== null && compareVersions(badge.dataset.newVersionBadge, seen) <= 0));
    }

    // ---------- Modals: aus Glas-Karten in den <body> heben ----------
    //
    // Das Glossy-Theme setzt auf .card ein backdrop-filter (theme.css). Ein Element mit
    // backdrop-filter/filter/transform ist ein Containing-Block für position:fixed UND
    // erzeugt einen eigenen Stacking-Context. Liegt ein Bootstrap-Modal in einer solchen
    // Karte, wird es
    //   1. nicht mehr viewport-zentriert (position:fixed bezieht sich auf die Karte), und
    //   2. vom .modal-backdrop (z-index 1050, im <body>) überdeckt – die Seite wird grau,
    //      der Dialog ist nicht bedienbar.
    //
    // Abhilfe: das Modal vor dem Öffnen an <body> hängen. Dann ist der Containing-Block
    // wieder das Dokument und das Modal teilt sich den Stacking-Context mit dem Backdrop
    // (Modal 1055 > Backdrop 1050). Der Zustand (Position im DOM) bleibt für die
    // Seiten-Navigation nicht relevant, da die Seite ohnehin neu geladen wird.
    //
    // show.bs.modal feuert VOR dem Einblenden -> hier ist noch Zeit zum Verschieben.
    // 'hidden.bs.modal' setzt das Modal zurück an seinen ursprünglichen Platz, damit die
    // DOM-Reihenfolge (und damit die Tab-Reihenfolge im Formular) erhalten bleibt.

    // Ein Browser, der eine CSS-Eigenschaft nicht kennt, liefert undefined bzw. ''.
    // Ohne diese Normalisierung wäre '!== none' IMMER erfüllt und jedes Modal würde
    // (unnötig) gehoben.
    // 'empty' ist der Ausgangswert der jeweiligen Eigenschaft, wenn sie unbekannt ist:
    // bei will-change ist das 'auto' (nicht 'none').
    function styleValue(cs, name, empty) {
        const v = cs[name];
        if (v === undefined || v === null || v === '') return empty;
        return v;
    }

    function createsContainingBlock(el) {
        const cs = getComputedStyle(el);
        // backdrop-filter: der unpräfixierte Name ist Standard; -webkit- als Rückfall.
        const backdrop = cs.backdropFilter || cs.webkitBackdropFilter || 'none';
        return backdrop !== 'none' ||
               styleValue(cs, 'filter', 'none') !== 'none' ||
               styleValue(cs, 'transform', 'none') !== 'none' ||
               styleValue(cs, 'perspective', 'none') !== 'none' ||
               styleValue(cs, 'contain', 'none') !== 'none' ||
               styleValue(cs, 'willChange', 'auto') !== 'auto';
    }

    function modalNeedsLift(modal) {
        // Ein position:fixed-Element wird vom nächstgelegenen Vorfahren mit
        // backdrop-filter/filter/transform/… als Containing-Block eingefangen –
        // das kann auch der Großelternteil sein (z. B. .card > .card-body > .modal).
        for (let el = modal.parentElement; el && el !== document.body; el = el.parentElement) {
            if (createsContainingBlock(el)) return true;
        }
        return false;
    }

    function liftModalsToBody(root) {
        const scope = root || document;
        const modals = scope === document
            ? scope.querySelectorAll('.modal')
            : (scope.matches && scope.matches('.modal') ? [scope] : scope.querySelectorAll('.modal'));
        modals.forEach(modal => {
            if (!modalNeedsLift(modal)) return;
            if (!modal.dataset.saModalHome) {
                // Ursprünglichen Platz merken (Elternelement + Nachbar + Index) für das
                // Zurückhängen. Der Index wird zusätzlich gespeichert, damit die Position
                // auch dann exakt wiederhergestellt wird, wenn der Nachbarknoten inzwischen
                // entfernt wurde (sonst wanderte das Modal ans Ende der Kinder).
                modal.dataset.saModalHome = '1';
                modal._saHomeParent = modal.parentElement;
                modal._saHomeNext = modal.nextElementSibling;
                modal._saHomeIndex = modal.parentElement
                    ? Array.prototype.indexOf.call(modal.parentElement.children, modal)
                    : -1;
            }
            document.body.appendChild(modal);
        });
    }

    // Setzt das Modal an seinen gemerkten Originalplatz zurück und räumt die
    // Buchführung (dataset-Flag + gemerkte Positionen) IMMER auf.
    function clearModalHome(modal) {
        delete modal.dataset.saModalHome;
        modal._saHomeParent = null;
        modal._saHomeNext = null;
        modal._saHomeIndex = null;
    }

    function restoreModal(modal) {
        if (!modal || !modal.dataset.saModalHome) return;
        const home = modal._saHomeParent;
        if (!home || !home.isConnected) {
            // Der ursprüngliche Elternteil wurde zwischenzeitlich ersetzt (z. B. durch ein
            // Razor-Partial-Reload) oder entfernt. Das Modal darf dann nicht dauerhaft im
            // <body> hängen bleiben: Buchführung aufräumen und den aktuellen Parent als
            // neues Home adoptieren, damit das Modal bei einem späteren Schließen wieder
            // sauber einsortiert wird (und nicht als "noch zu restaurieren" gilt).
            clearModalHome(modal);
            if (modal.parentElement && modal.parentElement !== document.body) {
                modal._saHomeParent = modal.parentElement;
                modal._saHomeNext = modal.nextElementSibling;
                modal._saHomeIndex = Array.prototype.indexOf.call(
                    modal.parentElement.children, modal);
                modal.dataset.saModalHome = '1';
            }
            return;
        }
        if (modal._saHomeNext && modal._saHomeNext.parentElement === home) {
            // Normalfall: der Nachbarknoten existiert noch -> Position exakt wiederherstellen.
            home.insertBefore(modal, modal._saHomeNext);
        } else if (typeof modal._saHomeIndex === 'number' && modal._saHomeIndex >= 0) {
            // Nachbarknoten entfernt: Position über den gemerkten Index wiederherstellen,
            // damit die DOM- (und damit die Tab-) Reihenfolge erhalten bleibt.
            const reference = home.children[modal._saHomeIndex] || null;
            home.insertBefore(modal, reference);
        } else {
            home.appendChild(modal);
        }
        clearModalHome(modal);
    }

    function initModalLift() {
        // Beim Start alle Modals prüfen (auch die, die z. B. per Partial in einer Karte liegen).
        liftModalsToBody(document);

        document.addEventListener('show.bs.modal', function (e) {
            const modal = e.target;
            if (!modal || !modal.classList.contains('modal')) return;
            // Kurz vor dem Öffnen erneut prüfen: die Karte könnte zwischenzeitlich
            // umgerendert worden sein. Nur das zu öffnende Modal anheben – ein bereits
            // geöffnetes Modal darf nicht unberührt umgehängt werden.
            liftModalsToBody(modal);
        }, true);

        document.addEventListener('hidden.bs.modal', function (e) {
            // Nach dem Schließen zurück an den ursprünglichen Platz (DOM-Reihenfolge bleibt).
            // Nur das gerade geschlossene Modal – ein anderes, noch offenes Modal darf
            // nicht aus dem <body> zurückwandern.
            restoreModal(e.target);
        }, true);
    }

    document.addEventListener('DOMContentLoaded', () => {
        applyTheme(currentTheme());
        document.querySelectorAll('[data-theme-toggle]').forEach(button =>
            button.addEventListener('click', () => applyTheme(currentTheme() === 'dark' ? 'light' : 'dark')));

        renderRelativeTimes();
        setInterval(renderRelativeTimes, 30000);

        document.querySelectorAll('[data-auto-refresh]').forEach(initAutoRefresh);
        loadOpenProblemCount();
        initReleaseNotes();
        initModalLift();
    });

    window.simpleAdmin = { renderRelativeTimes, applyTheme };
})();
