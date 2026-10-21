// nav.js — Vexis shared navigation, part 1 of 5: the basics every page uses
// (IS_WEBVIEW, sendToHost, escHtml, the page list). Do NOT redeclare these in pages.
// Load order (classic scripts sharing one global scope): nav.js → nav-theme.js → nav-panel.js → nav-styles.js → nav-boot.js
// Pages include all five, in this order; this tag carries data-page.

window.IS_WEBVIEW = !!window.chrome?.webview;

// F11 fullscreen — send to host to toggle window chrome
document.addEventListener('keydown', e => {
  if (e.key === 'F11') { e.preventDefault(); sendToHost({type:'toggleFullscreen'}); }
});
// Escape text before putting it into HTML (element text or a quoted attribute).
// Names, paths and log lines come from hardware, files and other programs, so they
// must never be read as markup. Never build inline onclick="f('…')" code from data:
// the browser decodes &#39; back to ' before running it. Use data-* attributes instead.
window.escHtml = function(t) {
  return String(t ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[c]));
};

// One set of temperature colour bands for CPU and GPU readings on every page (pages used to
// have their own: 60/78, 60/80 and 50/75 °C, so one reading showed different colours).
// Drives keep their own per-model limit (storage.html).
window.NAV_TEMP_WARN = 60;
window.NAV_TEMP_HOT  = 78;
window.navTempColor = function(t) {
  if (t == null) return 'var(--label)';
  return t < NAV_TEMP_WARN ? 'var(--temp-low)' : t < NAV_TEMP_HOT ? 'var(--temp-warn)' : 'var(--temp-hot)';
};

window.sendToHost = function(obj) {
  if (window.IS_WEBVIEW) window.chrome.webview.postMessage(JSON.stringify(obj));
};

// Update available badge
window.navSetUpdateAvailable = function(version) {
  let badge = document.getElementById('nav-update-badge');
  if (!badge) {
    badge = document.createElement('div');
    badge.id = 'nav-update-badge';
    badge.style.cssText = 'position:fixed;top:34px;right:8px;z-index:9001;background:#ffaa00;color:#000;font-size:8px;font-family:monospace;letter-spacing:.06em;padding:2px 6px;border-radius:3px;cursor:pointer;font-weight:700;';
    badge.onclick = () => sendToHost({type:'navigate', file:'info.html'});
    document.body.appendChild(badge);
  }
  badge.textContent = 'UPDATE ' + version;
  badge.style.display = 'block';
};

// Update check result from the host: {current, latest, available, canInstall, notes, url} or {offline:true}
window.NAV_UPDATE = null;
window.navUpdateInfo = function(info) {
  window.NAV_UPDATE = info;
  if (info && info.available) navSetUpdateAvailable(info.latest + (info.canInstall ? ' \u00b7 INSTALL' : ''));
  if (typeof window.onUpdateInfo === 'function') window.onUpdateInfo(info);
};
// Download/install progress: pct 0-100, or -1 on failure
window.navUpdateProgress = function(pct, text) {
  const badge = document.getElementById('nav-update-badge');
  if (badge) badge.textContent = pct < 0 ? 'UPDATE FAILED' : pct >= 100 ? 'INSTALLING\u2026' : 'UPDATING ' + pct + '%';
  if (typeof window.onUpdateProgress === 'function') window.onUpdateProgress(pct, text);
};

// Show/hide the driver warning caution icon in the nav bar
window.navSetDriverWarning = function(msg) {
  let icon = document.getElementById('nav-caution');
  if (!icon) {
    icon = document.createElement('div');
    icon.id = 'nav-caution';
    icon.title = msg || 'CPU sensor driver blocked';
    icon.innerHTML = '⚠';
    icon.style.cssText = 'position:fixed;top:7px;left:36px;z-index:9001;font-size:11px;color:#ffaa00;cursor:pointer;opacity:.8;transition:opacity .3s;';
    icon.onclick = () => {
    const dw = document.getElementById('driver-warn');
    if (dw) {
      const showing = dw.style.display !== 'none';
      if (showing) {
        sessionStorage.setItem('pcm-warn-dismissed','1');
        dw.style.transition = 'opacity .3s'; dw.style.opacity = '0';
        setTimeout(() => { dw.style.display = 'none'; }, 300);
      } else {
        sessionStorage.removeItem('pcm-warn-dismissed');
        window._warnTimerStarted = false; // allow timer to restart
        dw.style.display = 'block'; dw.style.opacity = '1'; dw.style.transition = '';
      }
    }
  };
    document.body.appendChild(icon);
  } else {
    icon.style.display = msg ? 'block' : 'none';
  }
};

// ── Current page ──────────────────────────────────────────────────────────────
const NAV_CURRENT_PAGE = document.currentScript?.getAttribute('data-page') || 'home';
const NAV_IS_POPOUT    = new URLSearchParams(window.location.search).get('popout') === '1';
const NAV_PAGE_FILES   = {
  home:'home.html', performance:'index.html', threads:'threads.html', processes:'processes.html', gpu:'gpu.html', memory:'memory.html', storage:'storage.html', temps:'temps.html',
  fans:'fans.html', rgb:'rgb.html', info:'info.html', record:'record.html', security:'security.html'
};

// ── Pages list ─────────────────────────────────────────────────────────────────
const NAV_PAGES = [
  { id:'home',        icon:'⌂', label:'HOME'         },
  { id:'performance', icon:'▣', label:'PERFORMANCE'  },
  { id:'threads',     icon:'▤', label:'THREAD VIEWER' },
  { id:'processes',   icon:'☰', label:'PROCESSES'    },
  { id:'gpu',         icon:'▩', label:'GPU'          },
  { id:'memory',      icon:'▦', label:'MEMORY'        },
  { id:'storage',     icon:'▥', label:'STORAGE'       },
  { id:'temps',       icon:'◈', label:'TEMPERATURES' },
  { id:'fans',        icon:'◎', label:'FAN CONTROL'  },
  { id:'rgb',         icon:'◐', label:'RGB CONTROL'  },
  { id:'info',        icon:'◌', label:'INFO'         },
  { id:'record',      icon:'⏺', label:'CSV RECORDING' },
  { id:'security',    icon:'🛡', label:'SECURITY'     },
];

// ── Tab order (drag ⋮⋮ in the Pages list; saved in settings "navOrder") ─────────
function navSavedOrder() {
  let o = NAV_SETTINGS && NAV_SETTINGS.navOrder;
  if (!o) { try { o = localStorage.getItem('pcm-nav-order'); } catch {} }
  return o ? String(o).split(',').filter(Boolean) : [];
}
// Saved order first; pages it doesn't mention (new ones) keep their default spot
function navOrderedPages() {
  const saved = navSavedOrder().filter(id => NAV_PAGES.some(p => p.id === id));
  if (!saved.length) return NAV_PAGES.slice();
  const out = saved.map(id => NAV_PAGES.find(p => p.id === id));
  NAV_PAGES.forEach((p, i) => {
    if (saved.includes(p.id)) return;
    const prev = NAV_PAGES.slice(0, i).reverse().find(q => out.includes(q));   // after the page it followed by default
    out.splice(prev ? out.indexOf(prev) + 1 : 0, 0, p);
  });
  return out;
}
function navPageItemsHtml() {
  return navOrderedPages().map(p => {
    const active = p.id === NAV_CURRENT_PAGE;
    const file   = NAV_PAGE_FILES[p.id];
    return `<div class="nv-item${active ? ' nv-active' : ''}" data-id="${p.id}" onclick="navView('${file}')" title="Open ${p.label}">
      <span class="nv-grip" title="Drag to reorder" onclick="event.stopPropagation()" onpointerdown="navDragStart(event,'${p.id}')">⋮⋮</span>
      <span class="nv-icon">${p.icon}</span>
      <span class="nv-lbl">${p.label}</span>
      <button class="nv-pop" onclick="event.stopPropagation();navPopOut('${p.id}')" title="Pop out to window">
        <span class="nv-pop-art">⬡</span> POP
      </button>
    </div>`;
  }).join('');
}
function navRenderPageList() {
  const list = document.getElementById('nv-page-list');
  if (list) list.innerHTML = navPageItemsHtml();
}
function navSaveOrder(ids) {
  const v = ids.join(',');
  try { localStorage.setItem('pcm-nav-order', v); } catch {}
  if (typeof navSaveSetting === 'function') navSaveSetting('navOrder', v);
}
window.navResetOrder = function() {
  try { localStorage.removeItem('pcm-nav-order'); } catch {}
  if (NAV_SETTINGS) NAV_SETTINGS.navOrder = '';
  if (typeof navSaveSetting === 'function') navSaveSetting('navOrder', '');
  navRenderPageList();
};
// Pointer-based drag: the row follows the pointer, a gap shows where it will land
let _navDrag = null;
window.navDragStart = function(e, id) {
  e.preventDefault(); e.stopPropagation();
  const list = document.getElementById('nv-page-list');
  const item = list && list.querySelector(`.nv-item[data-id="${id}"]`);
  if (!item) return;
  const r = item.getBoundingClientRect();
  const ph = document.createElement('div');
  ph.className = 'nv-drop'; ph.style.height = r.height + 'px';
  item.after(ph);
  item.classList.add('nv-dragging');
  Object.assign(item.style, { position:'fixed', left:r.left + 'px', top:r.top + 'px', width:r.width + 'px', zIndex:10050, pointerEvents:'none' });
  _navDrag = { item, ph, list, dy: e.clientY - r.top };
  document.addEventListener('pointermove', navDragMove);
  document.addEventListener('pointerup', navDragEnd, { once:true });
};
function navDragMove(e) {
  const d = _navDrag; if (!d) return;
  d.item.style.top = (e.clientY - d.dy) + 'px';
  const others = [...d.list.querySelectorAll('.nv-item:not(.nv-dragging)')];
  const before = others.find(o => { const r = o.getBoundingClientRect(); return e.clientY < r.top + r.height / 2; });
  if (before) { if (d.ph.nextElementSibling !== before) before.before(d.ph); }
  else if (d.list.lastElementChild !== d.ph) d.list.appendChild(d.ph);
  // Scroll the list when dragging near its edges
  const lr = d.list.getBoundingClientRect();
  if (e.clientY < lr.top + 24) d.list.scrollTop -= 8; else if (e.clientY > lr.bottom - 24) d.list.scrollTop += 8;
}
function navDragEnd() {
  const d = _navDrag; _navDrag = null;
  document.removeEventListener('pointermove', navDragMove);
  if (!d) return;
  d.item.classList.remove('nv-dragging');
  d.item.removeAttribute('style');
  d.ph.replaceWith(d.item);
  navSaveOrder([...d.list.querySelectorAll('.nv-item')].map(x => x.dataset.id));
}
