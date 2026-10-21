// nav-styles.js — Vexis shared navigation. The CSS nav.js injects into every page (shared styles + the menu/panel), and the resizable panel.
// Load order: nav.js → nav-theme.js → nav-panel.js → nav-styles.js → nav-boot.js

// ── Resizable nav panel ───────────────────────────────────────────────────────
const NAV_WIDTH_DEFAULT = 300, NAV_WIDTH_MIN = 240, NAV_WIDTH_MAX = 640;

function navSetPanelWidth(px, save) {
  const max = Math.min(NAV_WIDTH_MAX, window.innerWidth - 40);
  const w = Math.round(Math.max(NAV_WIDTH_MIN, Math.min(max, px)));
  document.documentElement.style.setProperty('--nv-width', w + 'px');
  // Contents scale with the panel: 450 px wide = 1.5× the 300 px layout
  document.documentElement.style.setProperty('--nv-zoom', (w / NAV_WIDTH_DEFAULT).toFixed(3));
  if (save) { try { localStorage.setItem('pcm-nav-width', String(w)); } catch {} }
}

function navInitPanelResize(panel) {
  let stored = NAV_WIDTH_DEFAULT;
  try { stored = parseInt(localStorage.getItem('pcm-nav-width')) || NAV_WIDTH_DEFAULT; } catch {}
  navSetPanelWidth(stored, false);

  const grip = document.createElement('div');
  grip.id = 'nav-resize';
  grip.title = 'Drag to resize · double-click to reset';
  panel.appendChild(grip);

  let lastDown = 0;
  grip.addEventListener('pointerdown', e => {
    if (e.button !== 0) return;
    e.preventDefault();
    // Double-click (two presses within 350 ms) resets to the default width
    const now = Date.now();
    if (now - lastDown < 350) { lastDown = 0; navSetPanelWidth(NAV_WIDTH_DEFAULT, true); return; }
    lastDown = now;

    const startX = e.clientX;
    let moved = false;
    grip.setPointerCapture(e.pointerId);
    document.body.classList.add('nv-resizing');
    const move = ev => {
      if (!moved && Math.abs(ev.clientX - startX) < 3) return; // ignore plain clicks
      moved = true;
      navSetPanelWidth(ev.clientX, false);
    };
    const up = ev => {
      grip.releasePointerCapture(e.pointerId);
      grip.removeEventListener('pointermove', move);
      grip.removeEventListener('pointerup', up);
      document.body.classList.remove('nv-resizing');
      if (moved) navSetPanelWidth(ev.clientX, true);
    };
    grip.addEventListener('pointermove', move);
    grip.addEventListener('pointerup', up);
  });
}

// ── Styles ────────────────────────────────────────────────────────────────────
// Page-level styles every page needs, including popout windows:
// collapsible sections, switches, selects and the text-scale variables.
function injectSharedStyles() {
  const s = document.createElement('style');
  s.textContent = `
    /* ── Collapsible sections ─────────────────────────────────────────────── */
    .pcm-collapsible {
      cursor:pointer; user-select:none;
      color:var(--header-title) !important; font-weight:700 !important; letter-spacing:.2em;
      text-shadow:0 0 10px color-mix(in srgb, var(--title-glow, transparent) 45%, transparent);
      transition:filter .15s;
    }
    .pcm-collapsible:hover { filter:brightness(1.25); }
    .pcm-collapsible::after {
      content:'▾'; display:inline-block; margin-left:6px; font-size:.9em;
      opacity:.6; transition:transform .15s, opacity .15s;
    }
    .pcm-collapsible:hover::after { opacity:1; }
    .pcm-collapsible.pcm-is-collapsed::after { transform:rotate(-90deg); }
    .pcm-collapsed > :not(.pcm-collapse-head) { display:none !important; }
    .pcm-collapsed .pcm-collapse-head, .pcm-collapsed .pcm-collapse-head .card-title { margin-bottom:0 !important; }
    .pcm-hidden { display:none !important; }

    /* ── Text scaling ──────────────────────────────────────────────────────── */
    /* Pages size text as calc(Npx * var(--fs-base)) — "Page UI Font" slider — and
       live readings additionally * var(--data-scale) — "Data Text" slider. */
    :root { --fs-base:1; --data-scale:1; --nv-scale:1; }
    /* On/off switch — shared by all pages */
    .nv-switch { display:inline-flex; align-items:center; gap:6px; cursor:pointer; user-select:none; }
    .nv-switch input { position:absolute; opacity:0; width:0; height:0; }
    .nv-switch-track {
      position:relative; width:28px; height:14px; border-radius:7px; flex-shrink:0;
      background:var(--bar-bg,#120a00); border:1px solid var(--border,#443300); transition:background .15s, border-color .15s;
    }
    .nv-switch-knob {
      position:absolute; top:1px; left:1px; width:10px; height:10px; border-radius:50%;
      background:var(--label,#aa7700); transition:transform .15s, background .15s;
    }
    .nv-switch input:checked + .nv-switch-track {
      background:color-mix(in srgb, var(--header-title,#ffcc00) 22%, transparent); border-color:var(--header-title,#ffcc00);
    }
    .nv-switch input:checked + .nv-switch-track .nv-switch-knob {
      transform:translateX(14px); background:var(--header-title,#ffcc00);
      box-shadow:0 0 6px var(--title-glow,transparent);
    }
    .nv-switch-text {
      font-size:calc(9px * var(--fs-base,1)); font-family:monospace; letter-spacing:.14em;
      color:var(--label,#aa7700); transition:color .15s;
    }
    .nv-switch input:checked ~ .nv-switch-text { color:var(--header-title,#ffcc00); }

    .nv-num { width:52px; text-align:right; }

    /* Zoom − 100% + (top-right, left of ⛶ ⚙ — pages keep that corner clear) */
    #nav-zoom {
      position:fixed; right:68px; top:6px; z-index:9998; display:flex; align-items:center; gap:2px;
      padding:2px; border-radius:14px; background:rgba(0,0,0,.45);
      border:1px solid color-mix(in srgb, var(--border,#443300) 70%, transparent);
      opacity:.55; transition:opacity .15s;
    }
    #nav-zoom:hover, #nav-zoom:focus-within { opacity:1; }
    #nav-zoom button {
      display:flex; align-items:center; justify-content:center; height:20px; min-width:22px; padding:0 4px;
      border:none; border-radius:12px; background:transparent; cursor:pointer;
      color:var(--header-title,#ffcc00); font-family:monospace; font-size:10px;
    }
    #nav-zoom button:hover { background:color-mix(in srgb, var(--header-title,#ffcc00) 18%, transparent); }
    #nav-crash {
      position:fixed; top:62px; left:50%; transform:translateX(-50%); z-index:9550; width:min(520px, calc(100vw - 24px));
      background:var(--surface,#140f08); border:1px solid #d9901a; border-radius:6px; padding:12px 14px;
      box-shadow:0 8px 28px rgba(0,0,0,.65); font-family:monospace;
    }
    #nav-crash .ncr-title { color:#ffb347; font-size:11px; letter-spacing:.08em; font-weight:700; margin-bottom:6px; }
    #nav-crash .ncr-title span { color:var(--label-dim,#665500); font-weight:400; }
    #nav-crash .ncr-text { color:var(--label,#aa7700); font-size:10px; line-height:1.55; }
    #nav-crash .ncr-details { margin-top:6px; color:var(--label-dim,#665500); font-size:9px; }
    #nav-crash .ncr-details pre { max-height:120px; overflow:auto; white-space:pre-wrap; background:var(--bar-bg,#0a0600); padding:6px; border-radius:3px; }
    #nav-crash .ncr-btns { display:flex; gap:6px; justify-content:flex-end; margin-top:10px; flex-wrap:wrap; }
    #nav-crash button { font-family:monospace; font-size:9px; letter-spacing:.12em; padding:5px 10px; cursor:pointer; border-radius:3px;
      background:transparent; color:var(--label,#aa7700); border:1px solid var(--border,#443300); }
    #nav-crash button.pri { color:#ffb347; border-color:#d9901a; }
    #nav-crash button:hover { background:rgba(255,170,0,.1); }
    #nav-inbox {
      position:fixed; top:62px; right:12px; z-index:9500; width:min(340px, calc(100vw - 24px));
      background:var(--surface,#140f08); border:1px solid #cc3333; border-radius:6px;
      box-shadow:0 6px 24px rgba(0,0,0,.6), 0 0 12px rgba(255,60,60,.25); font-family:monospace; overflow:hidden;
    }
    #nav-inbox .nib-head { display:flex; align-items:center; gap:8px; padding:7px 10px; cursor:pointer; background:rgba(200,40,40,.15); }
    #nav-inbox .nib-bell { color:#ff5555; animation:navRecBlink 1s steps(2) infinite; }
    #nav-inbox .nib-title { flex:1; color:#ff7777; font-size:10px; letter-spacing:.14em; font-weight:700; }
    #nav-inbox .nib-chev { color:#ff7777; font-size:10px; }
    #nav-inbox .nib-list, #nav-inbox .nib-clear { display:none; }
    #nav-inbox.open .nib-list { display:block; max-height:40vh; overflow:auto; padding:4px 10px; }
    #nav-inbox.open .nib-clear { display:block; }
    #nav-inbox .nib-item { padding:6px 0; border-bottom:1px solid rgba(68,51,0,.3); font-size:10px; color:var(--text,#e8d080); line-height:1.45; }
    #nav-inbox .nib-item b { color:var(--header-title,#ffcc00); letter-spacing:.04em; text-transform:uppercase; font-size:9px; }
    #nav-inbox .nib-time { float:right; color:var(--label-dim,#665500); font-size:9px; }
    #nav-inbox .nib-clear {
      width:calc(100% - 20px); margin:6px 10px 10px; padding:6px; cursor:pointer; font-family:monospace; font-size:9px;
      letter-spacing:.14em; color:#ff7777; background:transparent; border:1px solid rgba(204,51,51,.6); border-radius:3px;
    }
    #nav-inbox .nib-clear:hover { background:rgba(200,40,40,.15); }
    #nav-rec, #nav-rec-sep { display:none !important; }
    #nav-rec.on { display:flex !important; }
    #nav-rec.on + #nav-rec-sep { display:block !important; }
    #nav-rec { gap:4px; color:var(--label,#aa7700) !important; letter-spacing:.06em; min-width:44px !important; }
    #nav-rec .rec-dot { width:7px; height:7px; border-radius:50%; background:#aa3333; display:inline-block; }
    #nav-rec.on { color:#ff5555 !important; }
    #nav-rec.on .rec-dot { background:#ff3333; box-shadow:0 0 6px #ff3333; animation:navRecBlink 1s steps(2) infinite; }
    @keyframes navRecBlink { 50% { opacity:.25; } }
    .nav-zoom-sep { width:1px; height:14px; background:color-mix(in srgb, var(--border,#443300) 90%, transparent); margin:0 2px; }
    #nav-zoom-pct { min-width:36px !important; color:var(--label,#aa7700) !important; }
    .nv-select {
      background:var(--surface2,#1e1608); border:1px solid var(--border,#443300); color:var(--text,#e8d080);
      font-size:calc(9px * var(--nv-scale,1)); padding:3px 6px; border-radius:3px; font-family:monospace; cursor:pointer;
    }
  `;
  document.head.appendChild(s);
}

// nav.js brings its own CSS, so every page gets the same menu, gear and panel without
// each page's stylesheet having to include it. !important wins over page styles
function injectNavStyles() {
  const s = document.createElement('style');
  s.textContent = `
    #nav-btn {
      position:fixed !important;top:6px !important;left:8px !important;right:auto !important;
      z-index:9999 !important;background:rgba(0,0,0,.35);border:none;cursor:pointer;
      font-size:clamp(17px,1.8vw,22px);color:var(--header-title,#ffcc00);
      padding:3px 7px;line-height:1;border-radius:3px;opacity:.85;
      /* Slide right when nav panel opens */
      transition:transform .22s cubic-bezier(.4,0,.2,1),opacity .2s !important;
    }
    #nav-btn:hover { opacity:1 !important; }
    #nav-btn.nv-shifted { transform:translateX(var(--nv-width,300px)) !important; }

    #nav-gear-btn {
      position:fixed !important;top:6px !important;right:8px !important;left:auto !important;
      z-index:9999 !important;background:rgba(0,0,0,.35);border:none;cursor:pointer;
      font-size:clamp(14px,1.5vw,18px);color:var(--label,#aa7700);
      padding:4px;line-height:1;opacity:.5;transition:opacity .2s;
    }
    #nav-gear-btn:hover { opacity:1 !important; }

    #nav-overlay {
      display:none;position:fixed;inset:0;z-index:8800;
      background:rgba(0,0,0,.55);backdrop-filter:blur(4px);
    }
    #nav-overlay.nv-open { display:block; }

    #nav-panel {
      position:fixed !important;top:0;left:0;bottom:0;
      width:var(--nv-width,300px) !important;min-width:var(--nv-width,300px) !important;max-width:var(--nv-width,300px) !important;z-index:8900;
      display:flex;flex-direction:column;
      background:color-mix(in srgb,var(--card-bg,#030108) 97%,transparent);
      backdrop-filter:blur(28px) saturate(1.6);
      border-right:1px solid color-mix(in srgb,var(--border,rgba(68,51,0,.6)) 60%,transparent);
      transform:translateX(-100%);
      transition:transform .22s cubic-bezier(.4,0,.2,1);
    }
    /* Drag the right edge to resize the panel (double-click resets) */
    #nav-panel > :not(#nav-resize) { zoom:var(--nv-zoom,1); }
    #nav-resize {
      position:absolute;top:0;right:0;bottom:0;width:8px;cursor:ew-resize;z-index:2;
    }
    #nav-resize::after {
      content:'';position:absolute;top:50%;left:3px;width:2px;height:48px;margin-top:-24px;border-radius:1px;
      background:var(--border-bright,#886600);opacity:0;transition:opacity .15s;
    }
    #nav-resize:hover::after, body.nv-resizing #nav-resize::after { opacity:.9; }
    body.nv-resizing, body.nv-resizing * { cursor:ew-resize !important; user-select:none !important; }
    body.nv-resizing #nav-panel, body.nv-resizing #nav-btn { transition:none !important; }
    #nav-panel.nv-open {
      transform:translateX(0);
      box-shadow:6px 0 32px rgba(0,0,0,.7);
    }
    #nav-panel.nv-colors-open {
      background:rgba(3,1,8,.82) !important;
      backdrop-filter:blur(12px) saturate(1.2) !important;
    }

    .nv-header {
      display:flex;align-items:center;justify-content:space-between;
      padding:14px 15px 10px;flex-shrink:0;
      border-bottom:1px solid rgba(68,51,0,.4);
    }
    .nv-title { font-size:clamp(13px,1.4vw,17px);font-family:monospace;color:var(--header-title,#ffcc00);letter-spacing:.2em;text-shadow:0 0 10px var(--title-glow,#ffcc00); }
    .nv-ver   { font-size:clamp(8px,.8vw,10px);color:var(--label,#665500);opacity:.7;letter-spacing:.08em;margin-top:3px; }
    .nv-x     { background:none;border:none;color:var(--label,#886600);cursor:pointer;font-size:14px;padding:2px 5px;transition:color .15s; }
    .nv-x:hover { color:var(--header-title,#aa7700); }

    .nv-tabs { display:flex;border-bottom:1px solid color-mix(in srgb,var(--border,#443300) 30%,transparent);flex-shrink:0; }
    .nv-tab {
      flex:1;padding:9px 4px;font-size:calc(clamp(9px,1vw,12px)*var(--nv-scale,1));letter-spacing:.1em;
      color:var(--label,#886600);background:none;border:none;border-bottom:2px solid transparent;
      cursor:pointer;font-family:monospace;transition:color .2s,border-color .2s;
    }
    .nv-tab:hover { color:var(--header-title,#ccaa00); }
    .nv-tab-active { color:var(--label,#aa7700) !important;border-bottom-color:var(--line-col1,#ffcc00) !important; }

    /* Sections share the same flex column layout */
    .nv-section { flex:1;display:flex;flex-direction:column;overflow:hidden;min-height:0; }

    .nv-items { flex:1;overflow-y:auto;padding:5px 0; }
    .nv-items::-webkit-scrollbar { width:3px; }
    .nv-items::-webkit-scrollbar-thumb { background:#2a1800;border-radius:2px; }

    .nv-item {
      display:flex;align-items:center;gap:10px;padding:13px 15px;
      transition:background .15s;border-left:2px solid transparent;
    }
    .nv-item:hover { background:color-mix(in srgb,var(--border,#443300) 20%,transparent); }
    .nv-active { background:color-mix(in srgb,var(--border,#443300) 30%,transparent) !important;border-left-color:var(--header-title,#ffcc00) !important; }
    .nv-icon   { font-size:calc(clamp(14px,1.5vw,18px)*var(--nv-scale,1));width:22px;text-align:center;color:var(--label,#886600);flex-shrink:0; }
    .nv-active .nv-icon { color:var(--header-title,#ffcc00); }
    .nv-lbl    { font-size:calc(clamp(11px,1.1vw,14px)*var(--nv-scale,1));flex:1;font-family:monospace;color:var(--label,#ccaa00);letter-spacing:.06em; }
    .nv-active .nv-lbl  { color:var(--header-title,#ffcc00); }
    .nv-pop {
      display:flex;align-items:center;gap:4px;flex-shrink:0;
      background:color-mix(in srgb,var(--border,#443300) 15%,transparent);
      border:1px solid color-mix(in srgb,var(--border,#443300) 35%,transparent);
      color:var(--label,#886600);
      font-size:calc(clamp(8px,.9vw,10px)*var(--nv-scale,1));
      padding:4px 10px;border-radius:3px;
      cursor:pointer;font-family:monospace;letter-spacing:.1em;transition:all .18s;
    }
    .nv-pop:hover {
      background:color-mix(in srgb,var(--header-title,#ffcc00) 18%,transparent);
      color:var(--header-title,#ffcc00);
      border-color:color-mix(in srgb,var(--header-title,#ffcc00) 50%,transparent);
      box-shadow:0 0 8px color-mix(in srgb,var(--header-title,#ffcc00) 12%,transparent);
    }
    .nv-pop-art { font-size:10px;opacity:.55; }
    .nv-item { cursor:pointer; }
    .nv-grip { cursor:grab;color:var(--label-dim,#665500);font-size:calc(11px*var(--nv-scale,1));letter-spacing:-2px;padding:4px 2px;margin:-4px -4px -4px -6px;
               opacity:.45;user-select:none;touch-action:none;flex-shrink:0; }
    .nv-item:hover .nv-grip { opacity:1; }
    .nv-grip:active { cursor:grabbing; }
    .nv-dragging { background:var(--surface2,#1e1608) !important;box-shadow:0 8px 24px rgba(0,0,0,.6);border-left-color:var(--header-title,#ffcc00) !important;opacity:.95; }
    .nv-drop { border:1px dashed color-mix(in srgb,var(--header-title,#ffcc00) 55%,transparent);border-radius:4px;margin:2px 8px;
               background:color-mix(in srgb,var(--header-title,#ffcc00) 6%,transparent); }
    .nv-order-bar { display:flex;justify-content:space-between;align-items:center;padding:6px 14px 10px;font-size:calc(9px*var(--nv-scale,1));
                    color:var(--label-dim,#665500);font-family:monospace;letter-spacing:.06em; }
    .nv-order-bar b { color:var(--label,#aa7700); }
    .nv-order-bar button { background:none;border:none;color:var(--label,#aa7700);cursor:pointer;font-family:monospace;font-size:inherit;letter-spacing:.06em; }
    .nv-order-bar button:hover { color:var(--header-title,#ffcc00); }

    /* ── Colors tab ── */
    .nvc-toolbar {
      display:flex;align-items:center;justify-content:space-between;
      padding:8px 12px 6px;flex-shrink:0;border-bottom:1px solid color-mix(in srgb,var(--border,#443300) 25%,transparent);
    }
    .nv-danger-btn { color:#ff6655 !important; border-color:rgba(255,80,60,.45) !important; }
    .nv-danger-btn:hover { background:rgba(255,60,40,.12) !important; }
    #nv-reset-dlg { position:fixed; inset:0; z-index:100000; background:rgba(0,0,0,.72); display:flex; align-items:center; justify-content:center; padding:16px; }
    #nv-reset-dlg .nv-rd-box { background:var(--surface, #111); border:1px solid rgba(255,80,60,.5); border-radius:8px; padding:18px 20px; max-width:440px; width:100%; font-family:inherit; }
    #nv-reset-dlg .nv-rd-title { font-size:12px; font-weight:700; letter-spacing:.12em; color:#ff6655; margin-bottom:10px; }
    #nv-reset-dlg .nv-rd-text { font-size:11px; color:var(--label); line-height:1.5; margin:6px 0; }
    #nv-reset-dlg .nv-rd-list { font-size:11px; color:var(--label); opacity:.85; line-height:1.6; margin:4px 0 8px 18px; padding:0; }
    #nv-reset-dlg .nv-rd-btns { display:flex; gap:8px; justify-content:flex-end; margin-top:14px; }
    #nv-reset-dlg .nv-rd-btns button { padding:6px 14px; }
    .nvc-reset-all {
      background:color-mix(in srgb,var(--border,#443300) 20%,transparent);
      border:1px solid color-mix(in srgb,var(--border,#443300) 40%,transparent);
      color:var(--label,#886600);font-size:8px;padding:3px 8px;border-radius:2px;
      cursor:pointer;font-family:monospace;letter-spacing:.06em;transition:all .15s;
    }
    .nvc-reset-all:hover { background:color-mix(in srgb,var(--header-title,#ffcc00) 10%,transparent);color:var(--header-title,#ccaa00); }

    .nvc-scroll { flex:1;overflow-y:auto;padding:4px 0 8px; }
    .nvc-scroll::-webkit-scrollbar { width:3px; }
    .nvc-scroll::-webkit-scrollbar-thumb { background:#2a1800;border-radius:2px; }

    .nvc-group-hdr {
      padding:8px 12px 4px;font-size:10px;letter-spacing:.12em;
      color:var(--label,#887744);font-family:monospace;font-weight:600;opacity:.7;
    }

    .nvc-row {
      display:flex;align-items:center;gap:7px;
      padding:5px 12px;transition:background .12s;
    }
    .nvc-row:hover { background:color-mix(in srgb,var(--border,#443300) 25%,transparent); }


    .nvc-swatch {
      width:16px;height:16px;border-radius:2px;flex-shrink:0;
      border:1px solid rgba(68,51,0,.5);
    }
    .nvc-lbl {
      flex:1;font-size:clamp(9px,1vw,11px);color:var(--label,#886600);
      font-family:monospace;overflow:hidden;white-space:nowrap;text-overflow:ellipsis;
    }
    .nvc-reset {
      background:none;border:none;color:color-mix(in srgb,var(--border,#443300) 100%,transparent);cursor:pointer;
      font-size:12px;padding:0 2px;line-height:1;transition:color .15s;flex-shrink:0;
    }
    .nvc-reset:hover { color:var(--header-title,#ccaa00); }
    .nvc-input {
      width:24px;height:20px;border:1px solid rgba(68,51,0,.5);border-radius:2px;
      cursor:pointer;background:none;padding:1px;flex-shrink:0;
    }
    .nvc-input::-webkit-color-swatch-wrapper { padding:0; }
    .nvc-input::-webkit-color-swatch { border:none;border-radius:1px; }

    /* ── Themes tab ── */
    .nv-section-hdr { padding:8px 14px 4px;font-size:8px;letter-spacing:.16em;color:var(--label,#554400);opacity:.7;font-family:monospace;flex-shrink:0; }
    .nv-grid { display:grid;grid-template-columns:1fr 1fr;gap:6px;padding:4px 12px 8px;flex-shrink:0; }
    .nv-preset {
      background:color-mix(in srgb,var(--card-bg,#0a0803) 80%,transparent);
      border:1px solid color-mix(in srgb,var(--border,#443300) 30%,transparent);
      border-radius:3px;padding:8px 6px;cursor:pointer;text-align:center;
      transition:border-color .2s,background .2s;
    }
    .nv-preset:hover { border-color:color-mix(in srgb,var(--header-title,#ffcc00) 50%,transparent);background:color-mix(in srgb,var(--card-bg,#1e1400) 90%,transparent); }
    .nv-pdots { display:flex;gap:3px;justify-content:center;margin-bottom:5px; }
    .nv-pdots span { width:9px;height:9px;border-radius:50%;display:inline-block; }
    .nv-pname { font-size:clamp(8px,.9vw,10px);color:var(--label,#886600);font-family:monospace;letter-spacing:.06em; }

    .nv-profiles { padding:4px 12px;flex-shrink:0; }
    .nv-pslot { display:flex;align-items:center;gap:8px;padding:8px 0;border-bottom:1px solid rgba(68,51,0,.2); }
    .nv-pslot:last-child { border-bottom:none; }
    .nv-pslot-dot { width:10px;height:10px;border-radius:50%;background:#333;flex-shrink:0;transition:background .3s; }
    .nv-pslot-info { flex:1;min-width:0; }
    .nv-pslot-name { font-size:9px;font-family:monospace;letter-spacing:.1em;color:var(--label,#886600); }
    .nv-pslot-status { font-size:8px;color:#443300;margin-top:1px;letter-spacing:.08em; }
    .nv-pslot-btns { display:flex;gap:3px;flex-shrink:0; }
    .nv-ps-btn {
      padding:3px 7px;font-size:8px;letter-spacing:.06em;font-family:monospace;
      border:1px solid color-mix(in srgb,var(--border,#443300) 40%,transparent);border-radius:2px;cursor:pointer;
      background:color-mix(in srgb,var(--border,#443300) 15%,transparent);color:var(--label,#886600);transition:all .15s;
    }
    .nv-ps-btn:hover { background:color-mix(in srgb,var(--header-title,#ffcc00) 10%,transparent);color:var(--header-title,#ccaa00);border-color:color-mix(in srgb,var(--header-title,#ffcc00) 30%,transparent); }
    .nv-ps-btn:disabled { opacity:.3;cursor:default; }
    .nv-ps-load { color:#5a8a22;border-color:rgba(80,130,30,.3); }
    .nv-ps-load:hover { background:rgba(100,180,40,.1);color:#88cc44; }
    .nv-ps-clr { color:#884444;border-color:rgba(140,40,40,.3);padding:3px 5px; }
    .nv-ps-clr:hover { background:rgba(180,40,40,.15);color:#cc6666; }

    .nv-light-btn {
      width:100%;padding:7px;background:rgba(255,255,255,.05);
      border:1px solid color-mix(in srgb,var(--border,#443300) 30%,transparent);
      border-radius:2px;color:var(--label,#886600);font-size:clamp(10px,1vw,13px);font-family:monospace;
      letter-spacing:.1em;cursor:pointer;transition:all .15s;
    }
    .nv-light-btn:hover { background:rgba(255,255,200,.08);color:var(--label,#aa7700); }
    .nv-light-btn.nv-light-on { background:rgba(255,255,200,.15);border-color:color-mix(in srgb,var(--header-title,#ffcc00) 50%,transparent);color:var(--header-title,#ccaa00); }

    .nv-footer-actions { padding:6px 12px 2px;flex-shrink:0; }
    .nv-exit-btn {
      width:100%;padding:8px;background:rgba(180,20,20,.1);border:1px solid rgba(180,20,20,.3);
      border-radius:3px;color:#cc4444;font-size:clamp(10px,1vw,13px);font-family:monospace;
      letter-spacing:.1em;cursor:pointer;transition:all .15s;
    }
    .nv-exit-btn:hover { background:rgba(180,20,20,.25);border-color:rgba(220,50,50,.5);color:#ff6666; }

    .nv-footer {
      padding:6px 14px 8px;border-top:1px solid color-mix(in srgb,var(--border,#443300) 25%,transparent);
      font-size:clamp(8px,.8vw,10px);font-family:monospace;color:var(--label,#665500);opacity:.6;letter-spacing:.05em;flex-shrink:0;
    }

    body.nav-light-mode { filter:invert(1) hue-rotate(180deg); }

  `;
  document.head.appendChild(s);
}
