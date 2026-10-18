// nav-panel.js — Vexis shared navigation. The side panel: page changes, its tabs (Pages / Colors / Themes / System), text size,
// settings switches, pop-out badge, zoom buttons, crash banner and alert inbox.
// Load order: nav.js → nav-theme.js → nav-panel.js → nav-styles.js → nav-boot.js

// ── Page transition ────────────────────────────────────────────────────────────
window.navView   = function(file)   { closeNav(); showPageTransition(file); };
window.navPopOut = function(pageId) { closeNav(); sendToHost({ type:'popOut', page:pageId }); };

// The "LOADING" ring shown while changing page, so a slow page load doesn't look frozen
function showPageTransition(file) {
  // Create or reuse overlay
  let ov = document.getElementById('nav-transition');
  if (!ov) {
    ov = document.createElement('div');
    ov.id = 'nav-transition';
    ov.innerHTML = '<div class="nv-tr-ring"></div><div class="nv-tr-label">LOADING</div>';
    document.body.appendChild(ov);
    const s = document.createElement('style');
    s.textContent = `
      #nav-transition {
        position:fixed !important;inset:0 !important;z-index:999999 !important;
        display:flex;flex-direction:column;align-items:center;justify-content:center;gap:16px;
        background:rgba(4,2,0,0.72) !important;
        backdrop-filter:blur(28px) saturate(1.8) brightness(0.35) !important;
        opacity:0;pointer-events:none;
        transition:opacity .18s ease;
      }
      #nav-transition.nv-tr-show { opacity:1 !important;pointer-events:all !important; }
      .nv-tr-ring {
        width:44px;height:44px;border-radius:50%;
        border:2px solid rgba(68,51,0,.3);border-top-color:var(--header-title,#ffcc00);
        box-shadow:0 0 24px var(--title-glow,#ffcc00),0 0 48px rgba(255,204,0,.2);
        animation:nv-spin .7s linear infinite;
      }
      .nv-tr-label {
        font-size:10px;letter-spacing:.3em;text-transform:uppercase;
        color:var(--header-title,#ffcc00);font-family:monospace;
        text-shadow:0 0 10px var(--title-glow,#ffcc00);
      }
      @keyframes nv-spin { to { transform:rotate(360deg); } }`;
    document.head.appendChild(s);
  }
  // Force layout, then show
  ov.style.display = 'flex';
  requestAnimationFrame(() => {
    requestAnimationFrame(() => {
      ov.classList.add('nv-tr-show');
      setTimeout(() => sendToHost({ type:'navigate', file }), 280);
    });
  });
}

// ── Nav init ──────────────────────────────────────────────────────────────────
function initNav() {
  injectNavStyles();

  const overlay = document.createElement('div');
  overlay.id = 'nav-overlay';
  overlay.onclick = closeNav;
  document.body.appendChild(overlay);

  const panel = document.createElement('div');
  panel.id = 'nav-panel';
  panel.innerHTML = buildPanelHTML();
  document.body.appendChild(panel);
  navInitPanelResize(panel);

  // ☰ nav button — TOP LEFT
  const btn = document.createElement('button');
  btn.id = 'nav-btn'; btn.innerHTML = '☰'; btn.title = 'Navigation';
  btn.onclick = toggleNav;
  document.body.appendChild(btn);

  // ⛶ fullscreen toggle — TOP RIGHT, next to gear
  const fsBtn = document.createElement('button');
  fsBtn.id = 'nav-fs-btn';
  fsBtn.innerHTML = '⛶';
  fsBtn.title = 'Toggle Fullscreen (F11)';
  fsBtn.style.cssText = 'position:fixed !important;top:6px !important;right:36px !important;left:auto !important;z-index:9999 !important;background:rgba(0,0,0,.35);border:none;cursor:pointer;font-size:15px;color:var(--header-title,#ffcc00);padding:3px 7px;line-height:1;border-radius:3px;opacity:.7;';
  fsBtn.onclick = () => sendToHost({type:'toggleFullscreen'});
  document.body.appendChild(fsBtn);

  // ⚙ gear — TOP RIGHT (opens the Colors tab, same on every page)
  const gear = document.createElement('button');
  gear.id = 'nav-gear-btn'; gear.innerHTML = '⚙'; gear.title = 'Colors';
  gear.onclick = () => { openNav(); showNavTab('colors'); };
  document.body.appendChild(gear);

  updateProfileUI();
  applyStoredLightMode();

}

// Builds the whole side panel (Pages / Colors / Themes / System tabs) as one HTML string.
// Only Vexis's own values go into it (page names, preset names, colour keys)
function buildPanelHTML() {
  const pageItems = navPageItemsHtml();

  const presetBtns = NAV_PRESETS.map((p, i) => {
    const c = p.colors;
    return `<button class="nv-preset" onclick="navApplyPreset(${i})" title="${p.name}">
      <div class="nv-pdots">
        <span style="background:${c.headerTitle}"></span>
        <span style="background:${c.lineCol1}"></span>
        <span style="background:${c.lineCol2}"></span>
        <span style="background:${c.gpuAcc||c.memAcc}"></span>
      </div>
      <div class="nv-pname">${p.name}</div>
    </button>`;
  }).join('');

  const profileSlots = [1,2,3].map(s => `
    <div class="nv-pslot">
      <div class="nv-pslot-dot" id="nv-pdot-${s}"></div>
      <div class="nv-pslot-info">
        <div class="nv-pslot-name">PROFILE ${s}</div>
        <div class="nv-pslot-status" id="nv-plbl-${s}">EMPTY</div>
      </div>
      <div class="nv-pslot-btns">
        <button class="nv-ps-btn" onclick="navSaveProfile(${s})">SAVE</button>
        <button class="nv-ps-btn nv-ps-load" id="nv-pload-${s}" onclick="navLoadProfile(${s})" disabled>LOAD</button>
        <button class="nv-ps-btn nv-ps-clr" id="nv-pclr-${s}" onclick="navClearProfile(${s})" style="display:none">✕</button>
      </div>
    </div>`).join('');

  return `
    <div class="nv-header">
      <div>
        <div class="nv-title">VEXIS</div>
        <div class="nv-ver">Hardware Monitoring · <span class="nv-ver-num">v${window.NAV_APP_VERSION||'?'}</span> · Open Source</div>
      </div>
      <button class="nv-x" onclick="closeNav()">✕</button>
    </div>

    <div class="nv-tabs">
      <button class="nv-tab nv-tab-active" id="ntab-pages"  onclick="showNavTab('pages')">PAGES</button>
      <button class="nv-tab"               id="ntab-colors" onclick="showNavTab('colors')">COLORS</button>
      <button class="nv-tab"               id="ntab-themes" onclick="showNavTab('themes')">THEMES</button>
      <button class="nv-tab"               id="ntab-system" onclick="showNavTab('system')">SYSTEM</button>
    </div>

    <!-- PAGES TAB -->
    <div id="nvs-pages" class="nv-section">
      <div class="nv-items" id="nv-page-list">${pageItems}</div>
      <div class="nv-order-bar"><span>Drag <b>⋮⋮</b> to reorder</span><button onclick="navResetOrder()">↺ Reset order</button></div>
    </div>

    <!-- COLORS TAB -->
    <div id="nvs-colors" class="nv-section" style="display:none">
      <div class="nvc-toolbar">
        <span style="font-size:9px;color:#665500;letter-spacing:.1em">INDIVIDUAL COLORS</span>
        <button class="nvc-reset-all" onclick="navColorResetAll()">↺ RESET ALL</button>
      </div>
      <div class="nvc-scroll">
        ${buildColorPickerHTML()}
      </div>
    </div>

    <!-- THEMES TAB -->
    <div id="nvs-themes" class="nv-section" style="display:none">
      <div class="nv-section-hdr">QUICK PRESETS</div>
      <div class="nv-grid">${presetBtns}</div>
      <div class="nv-section-hdr" style="margin-top:6px">COLOR PROFILES</div>
      <div class="nv-profiles">${profileSlots}</div>

    </div>

    <div class="nv-footer-actions">
      <button class="nv-exit-btn" onclick="if(confirm('Exit Vexis?'))sendToHost({type:'exit'})">⏻ EXIT</button>
    </div>
    <div class="nv-footer">MIT License · <span onclick="sendToHost({type:'openUrl',url:'https://github.com/Vlottiz/Vexis-PC-monitor-and-controll'})" style="cursor:pointer;text-decoration:underline">GitHub</span></div>
    <!-- SYSTEM TAB -->
    <div id="nvs-system" class="nv-section" style="display:none;padding:12px 10px">
      <div style="font-size:9px;color:#665500;letter-spacing:.1em;margin-bottom:10px">SYSTEM SETTINGS</div>

      <!-- Page UI Font Size -->
      <div style="padding:8px 0;border-bottom:1px solid rgba(68,51,0,.2)">
        <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:6px">
          <span style="font-size:10px;color:var(--label)">Page UI Font</span>
          <span id="nv-font-val" style="font-size:10px;color:var(--mem-acc);font-family:monospace;font-weight:700">100%</span>
        </div>
        <input type="range" id="nv-font-slider" min="70" max="160" value="100" step="5"
          style="width:100%;accent-color:var(--mem-acc)"
          oninput="navSetFontScale(this.value)">
        <div style="display:flex;justify-content:space-between;font-size:8px;color:var(--label-dim);margin-top:2px">
          <span>Smaller</span><span>Default</span><span>Larger</span>
        </div>
      </div>

      <!-- Data Values Font Size -->
      <div style="padding:8px 0;border-bottom:1px solid rgba(68,51,0,.2)">
        <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:6px">
          <span style="font-size:10px;color:var(--label)">Data Text</span>
          <span id="nv-data-val" style="font-size:10px;color:var(--mem-acc);font-family:monospace;font-weight:700">100%</span>
        </div>
        <input type="range" id="nv-data-slider" min="60" max="160" value="100" step="5"
          style="width:100%;accent-color:var(--mem-acc)"
          oninput="navSetDataScale(this.value)">
        <div style="display:flex;justify-content:space-between;font-size:8px;color:var(--label-dim);margin-top:2px">
          <span>Smaller</span><span>Default</span><span>Larger</span>
        </div>
      </div>

      <!-- Nav Panel Font Size -->
      <div style="padding:8px 0;border-bottom:1px solid rgba(68,51,0,.2)">
        <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:6px">
          <span style="font-size:10px;color:var(--label)">Nav Panel Text</span>
          <span id="nv-nav-val" style="font-size:10px;color:var(--mem-acc);font-family:monospace;font-weight:700">100%</span>
        </div>
        <input type="range" id="nv-nav-slider" min="70" max="130" value="100" step="5"
          style="width:100%;accent-color:var(--mem-acc)"
          oninput="navSetNavScale(this.value)">
        <div style="display:flex;justify-content:space-between;font-size:8px;color:var(--label-dim);margin-top:2px">
          <span>Compact</span><span>Default</span><span>Larger</span>
        </div>
      </div>

      <div style="padding:8px 0;border-bottom:1px solid rgba(68,51,0,.2)">
        <div style="font-size:10px;color:var(--label);margin-bottom:6px">Windows</div>
        <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:6px">
          <span style="font-size:9px;color:var(--label);opacity:.8">Start with Windows (minimized to tray)</span>
          <label class="nv-switch">
            <input type="checkbox" id="nv-startup" onchange="navSetStartup(this.checked)">
            <span class="nv-switch-track"><span class="nv-switch-knob"></span></span>
          </label>
        </div>
        <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:6px">
          <span style="font-size:9px;color:var(--label);opacity:.8" title="When a new release is out, Vexis downloads and installs it at launch and restarts itself. Otherwise you get an UPDATE badge and choose when.">Install updates automatically</span>
          <label class="nv-switch">
            <input type="checkbox" id="nv-autoUpdate" onchange="navSaveSetting('autoUpdate', this.checked ? 'true' : 'false')">
            <span class="nv-switch-track"><span class="nv-switch-knob"></span></span>
          </label>
        </div>
      </div>

      <div style="padding:8px 0;border-bottom:1px solid rgba(68,51,0,.2)">
        <div style="font-size:10px;color:var(--label);margin-bottom:6px">Temperature Alerts</div>
        <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:6px">
          <span style="font-size:9px;color:var(--label);opacity:.8">Tray notification when too hot</span>
          <label class="nv-switch">
            <input type="checkbox" id="nv-alertsEnabled" onchange="navSaveSetting('alertsEnabled', this.checked ? 'true' : 'false')">
            <span class="nv-switch-track"><span class="nv-switch-knob"></span></span>
          </label>
        </div>
        <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:6px">
          <span style="font-size:9px;color:var(--label);opacity:.8">CPU limit</span>
          <span><input type="number" id="nv-alertCpu" class="nv-select nv-num" min="40" max="115" step="1" value="90"
            oninput="navSetAlertLimit('alertCpu', this.value)"> <span style="font-size:9px;color:var(--label);opacity:.8">°C</span></span>
        </div>
        <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:6px">
          <span style="font-size:9px;color:var(--label);opacity:.8">GPU limit</span>
          <span><input type="number" id="nv-alertGpu" class="nv-select nv-num" min="40" max="115" step="1" value="85"
            oninput="navSetAlertLimit('alertGpu', this.value)"> <span style="font-size:9px;color:var(--label);opacity:.8">°C</span></span>
        </div>
        <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:6px">
          <span style="font-size:9px;color:var(--label);opacity:.8">GPU hot spot limit</span>
          <span><input type="number" id="nv-alertGpuHotspot" class="nv-select nv-num" min="40" max="125" step="1" value="100"
            oninput="navSetAlertLimit('alertGpuHotspot', this.value)"> <span style="font-size:9px;color:var(--label);opacity:.8">°C</span></span>
        </div>
        <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:6px">
          <span style="font-size:9px;color:var(--label);opacity:.8">GPU memory (VRAM) limit</span>
          <span><input type="number" id="nv-alertGpuMem" class="nv-select nv-num" min="40" max="125" step="1" value="100"
            oninput="navSetAlertLimit('alertGpuMem', this.value)"> <span style="font-size:9px;color:var(--label);opacity:.8">°C</span></span>
        </div>
        <div id="nv-alert-status" style="font-size:9px;font-family:monospace;color:var(--label);opacity:.75;margin:2px 0 6px;min-height:12px"></div>
        <button onclick="sendToHost({type:'testAlert'})" class="nvc-reset-all" style="width:100%;justify-content:center;margin-top:2px">
          🔔 Send test notification (in 5 s)
        </button>
      </div>

      <div style="padding:8px 0;border-bottom:1px solid rgba(68,51,0,.2)">
        <div style="font-size:10px;color:var(--label);margin-bottom:4px">Recording (CSV)</div>
        <div style="font-size:9px;color:var(--label);opacity:.75;line-height:1.5;margin-bottom:6px">
          Start and stop recordings, pick what to record and view saved ones on the
          <b>CSV Recording</b> page. Files go to Documents\\Vexis Logs.
        </div>
        <button onclick="sendToHost({type:'navigate', file:'record.html'})" class="nvc-reset-all" style="width:100%;justify-content:center">
          ⏺ Open CSV Recording
        </button>
      </div>

      <div style="padding:8px 0;border-bottom:1px solid rgba(68,51,0,.2)">
        <div style="font-size:10px;color:var(--label);margin-bottom:6px">Performance Page</div>
        <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:6px">
          <span style="font-size:9px;color:var(--label);opacity:.8">Sparklines &amp; side panels</span>
          <label class="nv-switch">
            <input type="checkbox" id="nv-sparklines" ${navStoredSparklines() ? 'checked' : ''} onchange="navSetSparklines(this.checked)">
            <span class="nv-switch-track"><span class="nv-switch-knob"></span></span>
          </label>
        </div>
        <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:6px">
          <span style="font-size:9px;color:var(--label);opacity:.8">Update speed</span>
          <select id="nv-updateInterval" class="nv-select" onchange="navSetPerfSetting('updateInterval',this.value)">
            <option value="250">Max (4/sec)</option><option value="500">Fast (2/sec)</option>
            <option value="1000">Normal (1/sec)</option><option value="2000">Slow (every 2s)</option><option value="5000">Very slow (every 5s)</option>
          </select>
        </div>
        <div style="display:flex;align-items:center;justify-content:space-between;gap:8px">
          <span style="font-size:9px;color:var(--label);opacity:.8">AVG MHz window</span>
          <select id="nv-avgWindow" class="nv-select" onchange="navSetPerfSetting('avgWindow',this.value)">
            <option value="sync">Sync with update</option><option value="1000">1 second</option>
            <option value="5000">5 seconds</option><option value="15000">15 seconds</option>
            <option value="30000">30 seconds</option><option value="60000">1 minute</option>
          </select>
        </div>
      </div>

      <div style="padding:8px 0;border-bottom:1px solid rgba(68,51,0,.2)">
        <div style="font-size:10px;color:var(--label);margin-bottom:6px">Security Settings</div>
        <div style="font-size:9px;color:var(--label);opacity:.7;line-height:1.4;margin-bottom:8px">
          Sensor driver (PawnIO) status and Windows protection status.
        </div>
        <button onclick="sendToHost({type:'navigate',file:'security.html'});closeNav();"
          class="nvc-reset-all" style="width:100%;justify-content:center">
          🛡 Open Security Settings
        </button>
      </div>

      <div style="padding:8px 0;border-bottom:1px solid rgba(68,51,0,.2)">
        <div style="font-size:10px;color:var(--label);margin-bottom:4px">Version</div>
        <div id="nv-version" style="font-size:11px;color:var(--mem-acc);font-family:monospace">v${window.NAV_APP_VERSION||'?'}</div>
      </div>

      <div style="padding:8px 0;border-bottom:1px solid rgba(68,51,0,.2)">
        <div style="font-size:10px;color:var(--label);margin-bottom:4px">Reset</div>
        <div style="font-size:9px;color:var(--label);opacity:.75;line-height:1.5;margin-bottom:6px">
          Puts every setting back the way it was when Vexis was installed, then restarts.
        </div>
        <button onclick="navFactoryReset()" class="nvc-reset-all nv-danger-btn" style="width:100%;justify-content:center">
          ⟲ Reset to factory settings
        </button>
      </div>

      <button onclick="sendToHost({type:'openUrl',url:'https://github.com/Vlottiz/Vexis-PC-monitor-and-controll'})"
        class="nvc-reset-all" style="width:100%;margin-top:8px;justify-content:center">
        ⊞ GitHub
      </button>
    </div>
`;
}

// Text size slider: every page sizes its text as calc(Npx * var(--fs-base)), so one
// CSS variable scales the whole UI. Saved per PC in localStorage
function navSetFontScale(pct) {
  const scale = parseFloat(pct) / 100;
  document.documentElement.style.setProperty('--fs-base', scale);
  localStorage.setItem('pcm-font-scale', pct);
  const val = document.getElementById('nv-font-val');
  if (val) val.textContent = pct + '%';
}

// Data values scale — applies to sensor readings (temps, RPM, clocks, %)
// Uses --data-scale CSS variable. Pages can reference it in their data display CSS.
// A global rule is also injected in nav styles to catch common patterns.
window.navSetDataScale = function(pct) {
  const scale = parseFloat(pct) / 100;
  document.documentElement.style.setProperty('--data-scale', scale);
  localStorage.setItem('pcm-data-scale', pct);
  const val = document.getElementById('nv-data-val');
  if (val) val.textContent = pct + '%';
};

// Nav panel text scale — scales nav item labels, tabs, buttons
window.navSetNavScale = function(pct) {
  const scale = parseFloat(pct) / 100;
  document.documentElement.style.setProperty('--nv-scale', scale);
  localStorage.setItem('pcm-nav-scale', pct);
  const val = document.getElementById('nv-nav-val');
  if (val) val.textContent = pct + '%';
};

// On page load: put both text sliders (UI text and data numbers) back where they were
function navApplyStoredFontScale() {
  // Page UI font
  const stored = localStorage.getItem('pcm-font-scale') || '100';
  const slider = document.getElementById('nv-font-slider');
  if (slider) slider.value = stored;
  navSetFontScale(stored);
  // Data text
  const dataStored = localStorage.getItem('pcm-data-scale') || '100';
  const dataSlider = document.getElementById('nv-data-slider');
  if (dataSlider) dataSlider.value = dataStored;
  navSetDataScale(dataStored);
  // Nav panel text
  const navStored = localStorage.getItem('pcm-nav-scale') || '100';
  const navSlider = document.getElementById('nv-nav-slider');
  if (navSlider) navSlider.value = navStored;
  navSetNavScale(navStored);
}

// ── Reset to factory settings (System tab and Info page) ───────────────────
// Asks first, then the host turns fans back to automatic, removes the startup
// entry, deletes the saved settings and restarts Vexis. Recordings are kept.
window.navFactoryReset = function() {
  if (document.getElementById('nv-reset-dlg')) return;
  const d = document.createElement('div');
  d.id = 'nv-reset-dlg';
  d.innerHTML = `<div class="nv-rd-box" role="dialog" aria-modal="true">
    <div class="nv-rd-title">RESET VEXIS TO FACTORY SETTINGS?</div>
    <div class="nv-rd-text">This puts everything back the way it was when Vexis was installed:</div>
    <ul class="nv-rd-list">
      <li>Temperature alerts: switched off, limits back to default</li>
      <li>Fans: back to automatic, saved curves and fan profile removed</li>
      <li>Colours, themes, text size, zoom, side menu order and window position</li>
      <li>Saved RGB looks, recording choices and every other setting</li>
      <li>Start with Windows and automatic updates: switched off</li>
    </ul>
    <div class="nv-rd-text">Your CSV recordings are kept. Vexis restarts when it's done.</div>
    <div class="nv-rd-btns">
      <button class="nvc-reset-all" onclick="document.getElementById('nv-reset-dlg').remove()">Cancel</button>
      <button class="nvc-reset-all nv-danger-btn" id="nv-rd-go">⟲ Reset everything</button>
    </div></div>`;
  d.addEventListener('click', e => { if (e.target === d) d.remove(); });
  document.body.appendChild(d);
  document.getElementById('nv-rd-go').onclick = () => {
    try { localStorage.clear(); } catch {}
    d.querySelector('.nv-rd-box').innerHTML = '<div class="nv-rd-title">RESETTING…</div><div class="nv-rd-text">Vexis will restart in a moment.</div>';
    sendToHost({ type:'factoryReset' });
  };
};

// Generic setting saved to the shared config (config.json via the host)
window.navSaveSetting = function(key, val) {
  NAV_SETTINGS[key] = String(val);
  sendToHost({ type:'saveSettings', settings: { [key]: String(val) } });
};

// Alert limits save as you type (debounced) and switch alerts on
let _navAlertTimer = {};   // per limit, so editing two quickly keeps both
window.navSetAlertLimit = function(key, val) {
  if (!(parseFloat(val) > 0)) return;
  clearTimeout(_navAlertTimer[key]);
  _navAlertTimer[key] = setTimeout(() => {
    const sw = document.getElementById('nv-alertsEnabled');
    if (sw && !sw.checked) { sw.checked = true; navSaveSetting('alertsEnabled', 'true'); }
    navSaveSetting(key, val);
  }, 400);
};
// Live alert state from the host, e.g. "CPU 55°/40° sent 14:02 · GPU 47°/85° ok"
window.navAlertStatus = function(text) {
  const el = document.getElementById('nv-alert-status');
  if (el && text != null && el.textContent !== text) el.textContent = text;
};

// Start with Windows — the host owns the real state (a scheduled task)
window.navSetStartup = function(on) { sendToHost({ type:'setStartup', enabled: !!on }); };
window.navSetStartupState = function(on) {
  const el = document.getElementById('nv-startup');
  if (el) el.checked = !!on;
};

// Sparklines (Performance page) — per-viewer display preference
function navStoredSparklines() {
  try { return localStorage.getItem('pcm-sparklines') !== '0'; } catch { return true; }
}
window.navSetSparklines = function(on) {
  try { localStorage.setItem('pcm-sparklines', on ? '1' : '0'); } catch {}
  if (typeof window.setSparklines === 'function') window.setSparklines(on);
};

// Performance page settings live in the shared config so any page can change them
window.navSetPerfSetting = function(key, val) {
  NAV_SETTINGS[key] = String(val);
  sendToHost({ type:'saveSettings', settings: { [key]: String(val) } });
  if (typeof window.onPerfSettingChanged === 'function') window.onPerfSettingChanged(key, val);
};
function navSyncPerfSettings() {
  for (const key of ['updateInterval', 'avgWindow', 'alertCpu', 'alertGpu', 'alertGpuHotspot', 'alertGpuMem']) {
    const el = document.getElementById('nv-' + key);
    if (el && NAV_SETTINGS[key]) el.value = NAV_SETTINGS[key];
  }
  const alerts = document.getElementById('nv-alertsEnabled');
  if (alerts) alerts.checked = NAV_SETTINGS.alertsEnabled === 'true';
  const au = document.getElementById('nv-autoUpdate');
  if (au) au.checked = NAV_SETTINGS.autoUpdate === 'true';
}

// Leftover from early builds that could switch Windows protections off. Nothing calls it
// any more, and the app ignores this setting
function navToggleSecurityBypass(enabled) {
  NAV_SETTINGS['disableSecurityBypass'] = enabled ? 'false' : 'true'; // enabled checkbox = bypass ON
  sendToHost({ type:'saveSettings', settings: NAV_SETTINGS });
  const lbl = document.querySelector('#nv-sec-bypass + span');
  if (lbl) lbl.textContent = enabled ? 'ENABLED' : 'DISABLED';
}

// Switches the side panel between its four tabs
function showNavTab(tab) {
  // Make panel more transparent in colors mode so live changes are visible
  const panel = document.getElementById('nav-panel');
  if (panel) panel.classList.toggle('nv-colors-open', tab === 'colors');
  ['pages','colors','themes','system'].forEach(t => {
    const sec = document.getElementById(`nvs-${t}`);
    const btn = document.getElementById(`ntab-${t}`);
    if (sec) sec.style.display = t === tab ? 'flex' : 'none';
    if (btn) btn.className = 'nv-tab' + (t === tab ? ' nv-tab-active' : '');
  });
  // Sync picker values when opening the colors tab
  if (tab === 'colors') syncColorPickers();
  // Restore font slider when opening system tab
  if (tab === 'system') navApplyStoredFontScale();
}

window.openNav  = () => {
  document.getElementById('nav-panel').classList.add('nv-open');
  document.getElementById('nav-overlay').classList.add('nv-open');
  document.getElementById('nav-btn')?.classList.add('nv-shifted');
};
window.closeNav = () => {
  document.getElementById('nav-panel')?.classList.remove('nv-open');
  document.getElementById('nav-overlay')?.classList.remove('nv-open');
  document.getElementById('nav-btn')?.classList.remove('nv-shifted');
};
// ☰ button: slide the side panel in or out (the dark overlay behind it closes it too)
function toggleNav() {
  const o = document.getElementById('nav-panel').classList.toggle('nv-open');
  document.getElementById('nav-overlay').classList.toggle('nv-open', o);
  document.getElementById('nav-btn')?.classList.toggle('nv-shifted', o);
}

// ── Popout badge ──────────────────────────────────────────────────────────────
function initPopoutBadge() {
  const b = document.createElement('div');
  b.id = 'popout-badge'; b.innerHTML = '⊞ INSTANCE';
  document.body.appendChild(b);
  const s = document.createElement('style');
  s.textContent = `#popout-badge{position:fixed;bottom:5px;left:8px;z-index:9000;font-size:9px;font-family:monospace;letter-spacing:.1em;color:var(--header-title,#ffcc00);opacity:.4;background:rgba(0,0,0,.4);padding:3px 8px;border:1px solid rgba(68,51,0,.3);border-radius:2px;pointer-events:none;}`;
  document.head.appendChild(s);
  applyStoredLightMode();
}

// ── Zoom buttons (for people without Ctrl+scroll) ─────────────────────────────
// The host changes WebView2's zoom (same as Ctrl+scroll) and reports the level back.
const NAV_ZOOM_SVG = sign => `<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true">
  <circle cx="6.5" cy="6.5" r="4.5" fill="none" stroke="currentColor" stroke-width="1.6"/>
  <line x1="10" y1="10" x2="14.5" y2="14.5" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/>
  <line x1="4.3" y1="6.5" x2="8.7" y2="6.5" stroke="currentColor" stroke-width="1.4"/>
  ${sign === '+' ? '<line x1="6.5" y1="4.3" x2="6.5" y2="8.7" stroke="currentColor" stroke-width="1.4"/>' : ''}
</svg>`;

function navInitZoom() {
  if (document.getElementById('nav-zoom')) return;
  const z = document.createElement('div');
  z.id = 'nav-zoom';
  if (NAV_IS_POPOUT) z.style.right = '8px'; // popouts have no ⛶ / ⚙ buttons
  z.innerHTML =
    `<button type="button" id="nav-rec" title="Recording — open the CSV Recording page" data-rec="1"><span class="rec-dot"></span><span id="nav-rec-txt">REC</span></button>` +
    `<span class="nav-zoom-sep" id="nav-rec-sep"></span>` +
    `<button type="button" title="Zoom out (Ctrl −)" aria-label="Zoom out" data-d="-1">${NAV_ZOOM_SVG('-')}</button>` +
    `<button type="button" id="nav-zoom-pct" title="Reset zoom (Ctrl 0)" aria-label="Reset zoom" data-d="0">100%</button>` +
    `<button type="button" title="Zoom in (Ctrl +)" aria-label="Zoom in" data-d="1">${NAV_ZOOM_SVG('+')}</button>`;
  z.addEventListener('click', e => {
    const b = e.target.closest('button');
    if (!b) return;
    if (b.dataset.rec) sendToHost({ type: 'navigate', file: 'record.html' });
    else sendToHost({ type: 'zoom', delta: parseInt(b.dataset.d) });
  });
  document.body.appendChild(z);
}
// ── Crash helper: last session didn't close properly ──────────────────────────
let _navCrashShown = false;
window.navCrashBanner = function(c) {
  let box = document.getElementById('nav-crash');
  if (!c) { if (box) box.remove(); _navCrashShown = false; return; }
  if (_navCrashShown && box) return;
  _navCrashShown = true;
  const esc = escHtml;
  if (!box) { box = document.createElement('div'); box.id = 'nav-crash'; document.body.appendChild(box); }
  box.innerHTML =
    `<div class="ncr-title">&#x26A0; ${c.hasError ? 'Vexis ran into an error last time' : 'Vexis didn\'t close properly last time'}${c.when ? ' <span>(' + esc(c.when) + ')</span>' : ''}</div>` +
    `<div class="ncr-text">${c.hasError ? 'The error and the log from that session were saved.' : esc(c.summary)} ` +
    `Reporting it helps get it fixed — the report opens on GitHub with your version and system details filled in.</div>` +
    (c.hasError ? `<details class="ncr-details"><summary>Error details</summary><pre>${esc(c.summary)}</pre></details>` : '') +
    `<div class="ncr-btns"><button onclick="sendToHost({type:'crashOpenLog'})">OPEN LOG</button>` +
    `<button class="pri" onclick="sendToHost({type:'crashReport'})">REPORT ON GITHUB</button>` +
    `<button onclick="sendToHost({type:'crashDismiss'});navCrashBanner(null)">DISMISS</button></div>`;
};

// ── Alert inbox: notifications sent while you were away, until cleared ──────────
let _navInboxSig = '', _navInboxOpen = true;
window.navAlertInbox = function(list) {
  const sig = list.map(a => a.id).join(',');
  if (sig === _navInboxSig) return;
  const grew = list.length > (_navInboxSig ? _navInboxSig.split(',').length : 0);
  _navInboxSig = sig;
  let box = document.getElementById('nav-inbox');
  if (!list.length) { if (box) box.remove(); return; }
  if (!box) {
    box = document.createElement('div'); box.id = 'nav-inbox';
    document.body.appendChild(box);
  }
  if (grew) _navInboxOpen = true;           // a new alert always opens the list
  const esc = escHtml;
  box.className = _navInboxOpen ? 'open' : '';
  box.innerHTML =
    `<div class="nib-head" onclick="navInboxToggle()"><span class="nib-bell">&#x26A0;</span>` +
    `<span class="nib-title">${list.length} ALERT${list.length > 1 ? 'S' : ''}</span>` +
    `<span class="nib-chev">${_navInboxOpen ? '&#x25B4;' : '&#x25BE;'}</span></div>` +
    `<div class="nib-list">${list.slice().reverse().map(a =>
      `<div class="nib-item"><span class="nib-time">${esc(a.time)}</span><b>${esc(a.title)}</b><div>${esc(a.text)}</div></div>`).join('')}</div>` +
    `<button class="nib-clear" onclick="sendToHost({type:'clearAlerts'});navAlertInbox([])">CLEAR ALERTS</button>`;
};
window.navInboxToggle = function() {
  _navInboxOpen = !_navInboxOpen;
  const box = document.getElementById('nav-inbox');
  if (box) { box.classList.toggle('open', _navInboxOpen); box.querySelector('.nib-chev').innerHTML = _navInboxOpen ? '&#x25B4;' : '&#x25BE;'; }
};

// CSV recorder state from the host (data.recording)
window.navRecordState = function(r) {
  const b = document.getElementById('nav-rec'), t = document.getElementById('nav-rec-txt');
  if (!b || !r) return;
  b.classList.toggle('on', !!r.active);
  // Only shown while recording — started and stopped on the CSV Recording page
  if (r.active) {
    const m = Math.floor(r.seconds / 60), sec = String(r.seconds % 60).padStart(2, '0');
    t.textContent = m + ':' + sec;
    b.title = 'Recording to ' + r.file + ' (' + r.rows + ' rows) — open the CSV Recording page';
  }
};
window.navZoomState = function(pct) {
  const el = document.getElementById('nav-zoom-pct');
  if (el && pct) el.textContent = Math.round(pct) + '%';
};
