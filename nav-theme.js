// nav-theme.js — Vexis shared navigation. Colours: theme presets, light mode, the three saved colour profiles, the colour picker,
// and navOnConfig (settings from the app). Loaded after nav.js.
// Load order: nav.js → nav-theme.js → nav-panel.js → nav-styles.js → nav-boot.js

// ── Color state ───────────────────────────────────────────────────────────────
let NAV_CURRENT_COLORS = {};
let NAV_PROFILES       = {};

// ── Color groups for picker UI ────────────────────────────────────────────────
const NAV_COLOR_GROUPS = [
  { label: 'BACKGROUNDS', keys: ['bg','cardBg','surface','surface2','barBg'] },
  { label: 'BORDERS',     keys: ['border','borderBright','dividerLine'] },
  { label: 'LABELS & TEXT', keys: ['label','text','textDim','dividerLbl','cardTitle','coreLbl','ccdLbl','sumLbl'] },
  { label: 'HEADER',      keys: ['headerTitle','titleGlow','glowCol','headerSub','headerAcc','headerTime'] },
  { label: 'ACCENT LINES', keys: ['lineCol1','lineCol2'] },
  { label: 'CORE BARS',   keys: ['barStart','barLow','barMid','barHigh','barBoost'] },
  { label: 'TEMPERATURES', keys: ['tempCold','tempMid','tempHot','tempLow','tempWarn','tempVal'] },
  { label: 'DATA COLORS', keys: ['peakCol','avgCol','pwrCol','gpuAcc','memAcc'] },
];

const NAV_COLOR_LABELS = {
  bg:'Background', cardBg:'Card BG', surface:'Card Float BG', surface2:'Card Float Alt', barBg:'Bar Background',
  border:'Border', borderBright:'Border Highlight', dividerLine:'Divider Line',
  label:'Label', text:'Info Values (DDR5/MHz/etc)', textDim:'Dim Text', dividerLbl:'Divider Label', cardTitle:'Card Title',
  coreLbl:'Core Label', ccdLbl:'CCD Label', sumLbl:'Summary Label',
  headerTitle:'Header Title', titleGlow:'Title Glow', glowCol:'Value Glow',
  headerSub:'Header Subtitle', headerAcc:'Header Accent', headerTime:'Header Time',
  lineCol1:'Accent Line 1', lineCol2:'Accent Line 2',
  barStart:'Bar Start', barLow:'Bar Low', barMid:'Bar Mid',
  barHigh:'Bar High', barBoost:'Bar Boost',
  tempCold:'Temp Cold', tempMid:'Temp Mid', tempHot:'Temp Hot',
  tempLow:'Low Indicator', tempWarn:'Warn Indicator', tempVal:'Temp Value',
  peakCol:'Peak Clock', avgCol:'Avg Clock', pwrCol:'Power',
  gpuAcc:'GPU Accent', memAcc:'Memory Accent',
};

// ── Presets ───────────────────────────────────────────────────────────────────
const NAV_PRESETS = [
  { name:'CYBERPUNK', colors:{bg:'#0a0808',cardBg:'#120e08',surface:'#140f08',surface2:'#1e1608',borderBright:'#886600',text:'#e8d080',textDim:'#886600',border:'#443300',label:'#aa7700',dividerLine:'#443300',dividerLbl:'#ffcc00',headerTitle:'#ffcc00',headerSub:'#aa8800',headerAcc:'#00ffcc',headerTime:'#776600',titleGlow:'#ffcc00',glowCol:'#ffcc00',lineCol1:'#ffcc00',lineCol2:'#00ffcc',ccdLbl:'#ffcc00',coreLbl:'#aa8800',barBg:'#120a00',barStart:'#221100',barLow:'#443300',barMid:'#886600',barHigh:'#ffaa00',barBoost:'#ffcc00',tempCold:'#007799',tempMid:'#00ccaa',tempHot:'#00ffcc',tempVal:'#00ddaa',tempLow:'#00aacc',tempWarn:'#ffaa00',sumLbl:'#aa7700',peakCol:'#ffcc00',avgCol:'#00ffcc',pwrCol:'#ff6600',gpuAcc:'#00ffcc',memAcc:'#ffaa00',cardTitle:'#aa7700'} },
  { name:'PURPLE',    colors:{bg:'#000000',cardBg:'#0a0612',border:'#5c2278',label:'#cc88aa',dividerLine:'#5c2278',dividerLbl:'#dd88cc',headerTitle:'#ff2266',headerSub:'#cc99bb',headerAcc:'#aa44ee',headerTime:'#8855aa',titleGlow:'#ff1e64',glowCol:'#ff50c8',lineCol1:'#9900ff',lineCol2:'#ff0044',ccdLbl:'#ff2266',coreLbl:'#cc77aa',barBg:'#0e0820',barStart:'#2a1050',barLow:'#5511aa',barMid:'#8833dd',barHigh:'#cc2266',barBoost:'#ff1a44',tempCold:'#6600cc',tempMid:'#cc1188',tempHot:'#ff1133',tempVal:'#dd77ee',tempLow:'#aa33ff',tempWarn:'#ee2277',sumLbl:'#dd88cc',peakCol:'#ff2255',avgCol:'#bb44ff',pwrCol:'#dd33cc',gpuAcc:'#ff4488',memAcc:'#aa55ff',cardTitle:'#dd88cc',surface:'#0d0818',surface2:'#180d28',text:'#ddaacc',textDim:'#9966aa'} },
  { name:'MATRIX',    colors:{bg:'#000000',cardBg:'#010d01',border:'#003300',label:'#005500',dividerLine:'#003300',dividerLbl:'#00aa00',headerTitle:'#00ff44',headerSub:'#007722',headerAcc:'#00dd22',headerTime:'#005511',titleGlow:'#00ff44',glowCol:'#00ff44',lineCol1:'#00ff44',lineCol2:'#006622',ccdLbl:'#00ff44',coreLbl:'#00aa22',barBg:'#010d01',barStart:'#001100',barLow:'#003311',barMid:'#006622',barHigh:'#00aa33',barBoost:'#00ff44',tempCold:'#003322',tempMid:'#00aa44',tempHot:'#00ff22',tempVal:'#00cc33',tempLow:'#00cc33',tempWarn:'#00ff22',sumLbl:'#00aa22',peakCol:'#00ff44',avgCol:'#00cc33',pwrCol:'#00aa22',gpuAcc:'#00ee33',memAcc:'#00cc44',cardTitle:'#007722',surface:'#010d01',surface2:'#021402',text:'#00cc44',textDim:'#006622'} },
  { name:'OCEAN',     colors:{bg:'#000810',cardBg:'#000d1a',border:'#003366',label:'#336699',dividerLine:'#003366',dividerLbl:'#4499cc',headerTitle:'#00aaff',headerSub:'#336688',headerAcc:'#00ccff',headerTime:'#224466',titleGlow:'#00aaff',glowCol:'#00ccff',lineCol1:'#00aaff',lineCol2:'#0044cc',ccdLbl:'#00aaff',coreLbl:'#336699',barBg:'#000d1a',barStart:'#001122',barLow:'#003366',barMid:'#0066aa',barHigh:'#0099dd',barBoost:'#00ccff',tempCold:'#003399',tempMid:'#0066cc',tempHot:'#00aaff',tempVal:'#4499bb',tempLow:'#0099dd',tempWarn:'#00aaff',sumLbl:'#4499cc',peakCol:'#00ccff',avgCol:'#0099ff',pwrCol:'#0066cc',gpuAcc:'#00ddff',memAcc:'#0099cc',cardTitle:'#336699',surface:'#000d1a',surface2:'#001122',text:'#66aacc',textDim:'#336688'} },
  { name:'EMBER',     colors:{bg:'#080200',cardBg:'#100400',border:'#441100',label:'#883300',dividerLine:'#441100',dividerLbl:'#cc5500',headerTitle:'#ff4400',headerSub:'#882200',headerAcc:'#ff8800',headerTime:'#662200',titleGlow:'#ff4400',glowCol:'#ff6600',lineCol1:'#ff4400',lineCol2:'#ff8800',ccdLbl:'#ff4400',coreLbl:'#cc4400',barBg:'#100400',barStart:'#220800',barLow:'#441100',barMid:'#882200',barHigh:'#cc4400',barBoost:'#ff6600',tempCold:'#441100',tempMid:'#cc4400',tempHot:'#ff8800',tempVal:'#cc6600',tempLow:'#cc4400',tempWarn:'#ff6600',sumLbl:'#cc5500',peakCol:'#ff4400',avgCol:'#ff8800',pwrCol:'#cc3300',gpuAcc:'#ff6600',memAcc:'#ff4400',cardTitle:'#883300',surface:'#100400',surface2:'#180800',text:'#cc8844',textDim:'#884422'} },
  { name:'SYNTHWAVE', colors:{bg:'#0d0015',cardBg:'#150020',border:'#6600aa',label:'#9933cc',dividerLine:'#6600aa',dividerLbl:'#cc44ff',headerTitle:'#ff44cc',headerSub:'#9933aa',headerAcc:'#cc44ff',headerTime:'#662288',titleGlow:'#ff44cc',glowCol:'#cc44ff',lineCol1:'#cc44ff',lineCol2:'#ff44cc',ccdLbl:'#ff44cc',coreLbl:'#cc44aa',barBg:'#150020',barStart:'#220033',barLow:'#440088',barMid:'#8800cc',barHigh:'#cc00ff',barBoost:'#ff44cc',tempCold:'#4400aa',tempMid:'#cc00aa',tempHot:'#ff00cc',tempVal:'#cc44ff',tempLow:'#cc44ff',tempWarn:'#ff00cc',sumLbl:'#cc44ff',peakCol:'#ff44cc',avgCol:'#cc44ff',pwrCol:'#aa00ff',gpuAcc:'#ff00cc',memAcc:'#cc44ff',cardTitle:'#9933aa',surface:'#150020',surface2:'#200030',text:'#dd88ff',textDim:'#8833aa'} },
  { name:'TACTICAL',  colors:{bg:'#040600',cardBg:'#070900',border:'#1a2200',label:'#4a5a22',dividerLine:'#1a2200',dividerLbl:'#6a7a33',headerTitle:'#aacc00',headerSub:'#556611',headerAcc:'#ccaa00',headerTime:'#3a4a11',titleGlow:'#aacc00',glowCol:'#aacc00',lineCol1:'#aacc00',lineCol2:'#ccaa00',ccdLbl:'#aacc00',coreLbl:'#778833',barBg:'#060800',barStart:'#0a0e00',barLow:'#1a2a00',barMid:'#3a5500',barHigh:'#6a9900',barBoost:'#aacc00',tempCold:'#224400',tempMid:'#778800',tempHot:'#ccaa00',tempVal:'#88aa00',tempLow:'#88aa00',tempWarn:'#ccaa00',sumLbl:'#6a7a33',peakCol:'#aacc00',avgCol:'#88aa00',pwrCol:'#ccaa00',gpuAcc:'#ccaa00',memAcc:'#aacc00',cardTitle:'#556622',surface:'#070900',surface2:'#0a0d00',text:'#aacc66',textDim:'#6a7a33'} },
  { name:'LIGHT',     colors:{bg:'#f4f4f4',cardBg:'#e8e8e8',surface:'#e0e0e0',surface2:'#d8d8d8',border:'#bbbbbb',borderBright:'#888888',text:'#222222',textDim:'#555555',label:'#2a2a2a',dividerLine:'#bbbbbb',dividerLbl:'#1a1a1a',cardTitle:'#333333',coreLbl:'#222222',ccdLbl:'#1a1a1a',barBg:'#d0d0d0',headerTitle:'#111111',headerSub:'#333333',headerAcc:'#004499',headerTime:'#444444',titleGlow:'rgba(0,0,0,0)',glowCol:'rgba(0,0,0,0)',lineCol1:'#004499',lineCol2:'#006677',barStart:'#d0d0d0',barLow:'#aaaaaa',barMid:'#666666',barHigh:'#333333',barBoost:'#111111',tempCold:'#005599',tempMid:'#994400',tempHot:'#990000',tempVal:'#003377',tempLow:'#005599',tempWarn:'#884400',sumLbl:'#444444',peakCol:'#111111',avgCol:'#004499',pwrCol:'#880000',gpuAcc:'#005577',memAcc:'#004466',cardTitle:'#333333'} },
  { name:'GHOST',     colors:{bg:'#080808',cardBg:'#0f0f0f',border:'#333333',label:'#666666',dividerLine:'#333333',dividerLbl:'#888888',headerTitle:'#ffffff',headerSub:'#888888',headerAcc:'#cccccc',headerTime:'#555555',titleGlow:'#ffffff',glowCol:'#aaaaaa',lineCol1:'#cccccc',lineCol2:'#888888',ccdLbl:'#ffffff',coreLbl:'#888888',barBg:'#111111',barStart:'#1a1a1a',barLow:'#333333',barMid:'#666666',barHigh:'#999999',barBoost:'#ffffff',tempCold:'#333333',tempMid:'#888888',tempHot:'#ffffff',tempVal:'#aaaaaa',tempLow:'#aaaaaa',tempWarn:'#dddddd',sumLbl:'#888888',peakCol:'#ffffff',avgCol:'#cccccc',pwrCol:'#aaaaaa',gpuAcc:'#dddddd',memAcc:'#bbbbbb',cardTitle:'#777777',surface:'#0f0f0f',surface2:'#181818',text:'#aaaaaa',textDim:'#666666'} },
];

const NAV_VAR_MAP = {
  bg:'--bg',cardBg:'--card-bg',surface:'--surface',surface2:'--surface2',border:'--border',borderBright:'--border-bright',label:'--label',text:'--text',textDim:'--text-dim',
  dividerLine:'--divider-line',dividerLbl:'--divider-lbl',
  headerTitle:'--header-title',headerSub:'--header-sub',headerAcc:'--header-acc',
  headerTime:'--header-time',titleGlow:'--title-glow',glowCol:'--glow-col',
  lineCol1:'--line-col1',lineCol2:'--line-col2',
  ccdLbl:'--ccd-lbl',coreLbl:'--core-lbl',barBg:'--bar-bg',
  barStart:'--bar-start',barLow:'--bar-low',barMid:'--bar-mid',
  barHigh:'--bar-high',barBoost:'--bar-boost',
  tempCold:'--temp-cold',tempMid:'--temp-mid',tempHot:'--temp-hot',
  tempVal:'--temp-val',tempLow:'--temp-low',tempWarn:'--temp-warn',
  sumLbl:'--sum-lbl',peakCol:'--peak-col',avgCol:'--avg-col',pwrCol:'--pwr-col',
  gpuAcc:'--gpu-acc',memAcc:'--mem-acc',cardTitle:'--card-title',
};

// Apply colors to CSS vars on this page
window.navApplyColors = function(colors) {
  if (!colors || !Object.keys(colors).length) return;
  NAV_CURRENT_COLORS = {...colors};
  const r = document.documentElement.style;
  // Clear ALL mapped vars first so stale light/dark values don't bleed through
  for (const cssVar of Object.values(NAV_VAR_MAP))
    r.removeProperty(cssVar);
  // Apply new theme values
  for (const [k, cssVar] of Object.entries(NAV_VAR_MAP))
    if (colors[k]) r.setProperty(cssVar, colors[k]);
  if (colors.bg) document.body.style.background = colors.bg;
  if (typeof window.__setColors === 'function') window.__setColors(colors);
  // Detect light mode — remove pcm-light class always (pure CSS var approach now)
  document.body.classList.remove('pcm-light');
  syncColorPickers();
};

// Apply preset + save + reload current page
window.navApplyPreset = function(idx) {
  const preset = NAV_PRESETS[idx];
  if (!preset) return;
  localStorage.setItem('pcm-last-preset-idx', String(idx));
  sessionStorage.setItem('pcm-last-preset-idx', String(idx)); // backup
  NAV_CURRENT_COLORS = {...preset.colors};
  sendToHost({ type:'saveColors', colors: preset.colors, presetIdx: idx });
  closeNav();
  showPageTransition(NAV_PAGE_FILES[NAV_CURRENT_PAGE] || 'home.html');
};

// ── Light mode ─────────────────────────────────────────────────────────────────
function toggleLightMode() {
  const on = document.body.classList.toggle('nav-light-mode');
  localStorage.setItem('pcm-light-mode', on ? '1' : '0');
  const btn = document.getElementById('nv-light-toggle');
  if (btn) btn.classList.toggle('nv-light-on', on);
}
// Light mode is a per-PC choice kept in this browser's storage, not in config.json
function applyStoredLightMode() {
  if (localStorage.getItem('pcm-light-mode') === '1') {
    document.body.classList.add('nav-light-mode');
    const btn = document.getElementById('nv-light-toggle');
    if (btn) btn.classList.add('nv-light-on');
  }
}

// ── Profile management ─────────────────────────────────────────────────────────
function navSaveProfile(slot) {
  if (!Object.keys(NAV_CURRENT_COLORS).length) {
    alert('Apply a theme first before saving a profile.'); return;
  }
  NAV_PROFILES[slot] = {...NAV_CURRENT_COLORS};
  sendToHost({ type:'saveProfile', slot, colors: NAV_CURRENT_COLORS });
  updateProfileUI();
}
// Colour profiles: three saved slots (1-3). Loading one saves it as the current colours,
// then reloads the page so every element picks the new values up
function navLoadProfile(slot) {
  const colors = NAV_PROFILES[slot];
  if (!colors) { alert('No profile saved in slot ' + slot); return; }
  sendToHost({ type:'saveColors', colors });
  closeNav();
  showPageTransition(NAV_PAGE_FILES[NAV_CURRENT_PAGE] || 'home.html');
}
// Empties a slot here and in config.json
function navClearProfile(slot) {
  NAV_PROFILES[slot] = null;
  sendToHost({ type:'deleteProfile', slot });
  updateProfileUI();
}
// Redraws the three slot buttons: coloured dot + LOAD when a slot has a profile, grey when empty
function updateProfileUI() {
  for (let s = 1; s <= 3; s++) {
    const saved = NAV_PROFILES[s];
    const dot   = document.getElementById(`nv-pdot-${s}`);
    const lbl   = document.getElementById(`nv-plbl-${s}`);
    const load  = document.getElementById(`nv-pload-${s}`);
    const clr   = document.getElementById(`nv-pclr-${s}`);
    if (dot)  dot.style.background = saved ? (saved.headerTitle || '#888') : '#333';
    if (lbl)  { lbl.textContent = saved ? 'SAVED' : 'EMPTY'; lbl.style.color = saved ? '#88aa44' : '#443300'; }
    if (load) load.disabled     = !saved;
    if (clr)  clr.style.display = saved ? '' : 'none';
  }
}

// Called from any page's onConfigReceived
let NAV_SETTINGS = {};

window.navOnConfig = function(cfg) {
  if (typeof cfg.startWithWindows === 'boolean') navSetStartupState(cfg.startWithWindows);
  if (cfg.zoom) navZoomState(cfg.zoom);
  if (cfg.appVersion) {
    window.NAV_APP_VERSION = cfg.appVersion;
    const v = document.getElementById('nv-version');
    if (v) v.textContent = 'v' + cfg.appVersion;
    document.querySelectorAll('.nv-ver-num').forEach(el => el.textContent = 'v' + cfg.appVersion);
  }
  // Restore settings
  if (cfg.settings) {
    const orderChanged = (cfg.settings.navOrder || '') !== ((NAV_SETTINGS && NAV_SETTINGS.navOrder) || '');
    NAV_SETTINGS = cfg.settings;
    if (orderChanged) { try { localStorage.setItem('pcm-nav-order', cfg.settings.navOrder || ''); } catch {} navRenderPageList(); }
    navSyncPerfSettings();
    // Old security-bypass switch (see navToggleSecurityBypass): its checkbox no longer exists, so this finds nothing
    const bypassDisabled = cfg.settings['disableSecurityBypass'] === 'true';
    const cb = document.getElementById('nv-sec-bypass');
    if (cb) {
      cb.checked = !bypassDisabled; // checked = bypass enabled
      const lbl = cb.nextElementSibling;
      if (lbl) lbl.textContent = bypassDisabled ? 'DISABLED' : 'ENABLED';
    }
  }
  // Restore last preset index from config (persists across installs)
  if (cfg.lastPresetIdx != null) {
    localStorage.setItem('pcm-last-preset-idx', String(cfg.lastPresetIdx));
    sessionStorage.setItem('pcm-last-preset-idx', String(cfg.lastPresetIdx));
  }
  if (cfg.colors && Object.keys(cfg.colors).length)
    navApplyColors(cfg.colors);
  if (cfg.colorProfiles) {
    NAV_PROFILES = {...cfg.colorProfiles};
    updateProfileUI();
  }
};

// ── Color picker logic ─────────────────────────────────────────────────────────
function buildColorPickerHTML() {
  // Get defaults from CYBERPUNK preset (index 0) for reset targets
  const defaults = NAV_PRESETS[0].colors;

  return NAV_COLOR_GROUPS.map(group => {
    const rows = group.keys.map(key => {
      const lbl = NAV_COLOR_LABELS[key] || key;
      const def = defaults[key] || '#000000';
      return `
        <div class="nvc-row" id="nvcrow-${key}">
          <div class="nvc-swatch" id="nvc-swatch-${key}" style="background:${def}"></div>
          <label class="nvc-lbl">${lbl}</label>
          <button class="nvc-reset" onclick="navColorReset('${key}')" title="Reset to default">↺</button>
          <input type="color" class="nvc-input" id="nvc-${key}" value="${def}"
            oninput="navColorChange('${key}',this.value)"
            onchange="navColorChange('${key}',this.value)">
        </div>`;
    }).join('');
    return `
      <div class="nvc-group-hdr">${group.label}</div>
      ${rows}`;
  }).join('');
}

// After a preset or profile changes the colours, move every picker and swatch to match
function syncColorPickers() {
  // Update all picker input values and swatches to match current colors
  for (const key of Object.keys(NAV_COLOR_LABELS)) {
    const val = NAV_CURRENT_COLORS[key];
    if (!val) continue;
    const inp = document.getElementById(`nvc-${key}`);
    const sw  = document.getElementById(`nvc-swatch-${key}`);
    if (inp) inp.value = val;
    if (sw)  sw.style.background = val;
  }
}

// Live preview while dragging a colour picker: sets the CSS variable on this page and
// saves the colour so other pages and pop-outs get it too
function navColorChange(key, val) {
  const cssVar = NAV_VAR_MAP[key];
  if (cssVar) document.documentElement.style.setProperty(cssVar, val);
  if (key === 'bg') document.body.style.background = val;

  // Update swatch
  const sw = document.getElementById(`nvc-swatch-${key}`);
  if (sw) sw.style.background = val;

  // Update internal state
  NAV_CURRENT_COLORS[key] = val;

  // Notify performance page color engine if present
  if (typeof window.__setColors === 'function')
    window.__setColors(NAV_CURRENT_COLORS);

  // Debounced save to C# (avoid hammering on slider drag)
  clearTimeout(window._navColorSaveTimer);
  window._navColorSaveTimer = setTimeout(() => {
    sendToHost({ type:'saveColors', colors: NAV_CURRENT_COLORS });
  }, 400);
}

// ↺ next to one colour: back to the default (first) preset's value
function navColorReset(key) {
  const defaults = NAV_PRESETS[0].colors; // CYBERPUNK defaults
  const val = defaults[key];
  if (!val) return;
  const inp = document.getElementById(`nvc-${key}`);
  if (inp) inp.value = val;
  navColorChange(key, val);
}

// RESET ALL: back to whichever preset was picked last (or the default preset)
function navColorResetAll() {
  const savedIdx = parseInt(
    sessionStorage.getItem('pcm-last-preset-idx') ||
    localStorage.getItem('pcm-last-preset-idx') || '0');
  const preset = NAV_PRESETS[savedIdx] || NAV_PRESETS[0];
  if (!confirm('Reset colors to ' + preset.name + ' defaults?')) return;
  NAV_CURRENT_COLORS = {...preset.colors};
  navApplyColors(NAV_CURRENT_COLORS);
  sendToHost({ type:'saveColors', colors: NAV_CURRENT_COLORS });
  syncColorPickers();
}
