// nav-boot.js — Vexis shared navigation. Collapsible sections and boot(). Loaded last: boot() runs on DOMContentLoaded and uses
// everything the other nav-*.js files define.
// Load order: nav.js → nav-theme.js → nav-panel.js → nav-styles.js → nav-boot.js

// ── Collapsible sections ──────────────────────────────────────────────────────
// Click any card title (class "card-title") to hide/show the rest of its card.
// Other headers opt in with data-collapse, plus data-collapse-target="#id" to
// hide a specific element instead of the rest of the card. State is remembered
// per page (and shared with popouts) and re-applied when pages rebuild cards.
const NAV_COLLAPSE_STORE = 'pcm-collapsed';

function navCollapsedAll() {
  try { return JSON.parse(localStorage.getItem(NAV_COLLAPSE_STORE) || '{}') || {}; } catch { return {}; }
}
function navCollapsedSet() { return new Set(navCollapsedAll()[NAV_CURRENT_PAGE] || []); }

window.navIsCollapsed = function(key) { return navCollapsedSet().has(key); };
window.navToggleCollapsed = function(key) {
  const all = navCollapsedAll();
  const set = new Set(all[NAV_CURRENT_PAGE] || []);
  if (set.has(key)) set.delete(key); else set.add(key);
  all[NAV_CURRENT_PAGE] = [...set];
  try { localStorage.setItem(NAV_COLLAPSE_STORE, JSON.stringify(all)); } catch {}
  return set.has(key);
};

function navCollapseKey(h) {
  return h.dataset.collapseKey || h.id || (h.textContent || '').trim().toUpperCase();
}

// Hide/show what a header controls
function navApplyCollapse(h, collapsed) {
  h.classList.toggle('pcm-is-collapsed', collapsed);
  const target = h.dataset.collapseTarget;
  if (target) {
    const t = document.querySelector(target);
    if (t) t.classList.toggle('pcm-hidden', collapsed);
    return;
  }
  const card = h.closest('[data-collapse-scope], .card');
  if (!card) return;
  // The card's direct child that holds the title stays visible
  let head = h;
  while (head.parentElement && head.parentElement !== card) head = head.parentElement;
  head.classList.add('pcm-collapse-head');
  card.classList.toggle('pcm-collapsed', collapsed);
}

function navWireCollapsibles() {
  const saved = navCollapsedSet();
  document.querySelectorAll('.card-title, [data-collapse]').forEach(h => {
    if (h.closest('#nav-panel')) return;
    if (!h.classList.contains('pcm-collapsible')) {
      h.classList.add('pcm-collapsible');
      h.title = h.title || 'Click to collapse / expand';
    }
    const want = saved.has(navCollapseKey(h));
    if (want !== h.classList.contains('pcm-is-collapsed') ||
        (want && !h.dataset.collapseTarget && !h.closest('.pcm-collapsed')))
      navApplyCollapse(h, want);
  });
}

// Toggle on press, not "click": pages re-render every poll, and a click is lost
// when the element under the mouse is replaced between press and release.
document.addEventListener('pointerdown', e => {
  if (e.button !== 0) return;
  const h = e.target.closest('.pcm-collapsible');
  if (!h || e.target.closest('button, input, select, a, label')) return;
  e.preventDefault(); // no text selection on rapid toggling
  navApplyCollapse(h, navToggleCollapsed(navCollapseKey(h)));
});

// Pages rebuild cards as data arrives — re-apply saved state after DOM changes
let _navCollapseQueued = false;
new MutationObserver(() => {
  if (_navCollapseQueued) return;
  _navCollapseQueued = true;
  requestAnimationFrame(() => { _navCollapseQueued = false; navWireCollapsibles(); });
}).observe(document.documentElement, { childList: true, subtree: true });

// ── Boot ──────────────────────────────────────────────────────────────────────
function boot() {
  injectSharedStyles();
  navInitZoom();
  // Every page defines receiveData(); tap it for nav-level info (alert status)
  const pageReceive = window.receiveData;
  if (typeof pageReceive === 'function')
    window.receiveData = function(d) {
      if (d && d.alert_status) navAlertStatus(d.alert_status);
      if (d && d.recording) navRecordState(d.recording);
      if (d) navAlertInbox(d.alerts || []);
      if (d) navCrashBanner(d.crash || null);
      return pageReceive.apply(this, arguments);
    };
  if (NAV_IS_POPOUT) initPopoutBadge();
  else               initNav();
  navApplyStoredFontScale();
  navWireCollapsibles();
  // Update state (the host caches it, so this only reaches GitHub once per launch)
  if (IS_WEBVIEW) setTimeout(() => sendToHost({ type: 'checkUpdate' }), 1200);
}

if (document.readyState === 'loading')
  document.addEventListener('DOMContentLoaded', boot);
else
  boot();
