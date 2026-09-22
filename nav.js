// nav.js v5 — Vexis shared navigation
// IS_WEBVIEW + sendToHost defined here — do NOT redeclare in pages

window.IS_WEBVIEW = !!window.chrome?.webview;

// F11 fullscreen — send to host to toggle window chrome
document.addEventListener('keydown', e => {
  if (e.key === 'F11') { e.preventDefault(); sendToHost({type:'toggleFullscreen'}); }
});
window.sendToHost = function(obj) {
  if (window.IS_WEBVIEW) window.chrome.webview.postMessage(JSON.stringify(obj));
};

// Update available badge
window.navSetUpdateAvailable = function(version) {
  let badge = document.getElementById('nav-update-badge');
  if (!badge) {
    badge = document.createElement('div');
    badge.id = 'nav-update-badge';
    badge.style.cssText = 'position:fixed;top:5px;right:30px;z-index:9001;background:#ffaa00;color:#000;font-size:8px;font-family:monospace;letter-spacing:.06em;padding:2px 6px;border-radius:3px;cursor:pointer;font-weight:700;';
    badge.onclick = () => sendToHost({type:'navigate', file:'info.html'});
    document.body.appendChild(badge);
  }
  badge.textContent = 'UPDATE ' + version;
  badge.style.display = 'block';
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
  home:'home.html', performance:'index.html', memory:'memory.html', temps:'temps.html',
  fans:'fans.html', rgb:'rgb.html', info:'info.html', security:'security.html'
};

// ── Pages list ─────────────────────────────────────────────────────────────────
const NAV_PAGES = [
  { id:'home',        icon:'⌂', label:'HOME'         },
  { id:'performance', icon:'▣', label:'PERFORMANCE'  },
  { id:'memory',      icon:'▦', label:'MEMORY'        },
  { id:'temps',       icon:'◈', label:'TEMPERATURES' },
  { id:'fans',        icon:'◎', label:'FAN CONTROL'  },
  { id:'rgb',         icon:'◐', label:'RGB CONTROL'  },
  { id:'info',        icon:'◌', label:'INFO'         },
  { id:'security',    icon:'🛡', label:'SECURITY'     },
];

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
function navLoadProfile(slot) {
  const colors = NAV_PROFILES[slot];
  if (!colors) { alert('No profile saved in slot ' + slot); return; }
  sendToHost({ type:'saveColors', colors });
  closeNav();
  showPageTransition(NAV_PAGE_FILES[NAV_CURRENT_PAGE] || 'home.html');
}
function navClearProfile(slot) {
  NAV_PROFILES[slot] = null;
  sendToHost({ type:'deleteProfile', slot });
  updateProfileUI();
}
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
  // Restore settings
  if (cfg.settings) {
    NAV_SETTINGS = cfg.settings;
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

function navColorReset(key) {
  const defaults = NAV_PRESETS[0].colors; // CYBERPUNK defaults
  const val = defaults[key];
  if (!val) return;
  const inp = document.getElementById(`nvc-${key}`);
  if (inp) inp.value = val;
  navColorChange(key, val);
}

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

// ── Page transition ────────────────────────────────────────────────────────────
window.navView   = function(file)   { closeNav(); showPageTransition(file); };
window.navPopOut = function(pageId) { closeNav(); sendToHost({ type:'popOut', page:pageId }); };

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
  // Force exact width after insertion (overrides any CSS inheritance)
  requestAnimationFrame(() => {
    if (panel) { panel.style.setProperty('width','300px','important'); panel.style.setProperty('min-width','300px','important'); panel.style.setProperty('max-width','300px','important'); }
  });

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

  // ⚙ gear — TOP RIGHT (opens full settings on perf page, colors panel elsewhere)
  const gear = document.createElement('button');
  gear.id = 'nav-gear-btn'; gear.innerHTML = '⚙'; gear.title = 'Colors';
  gear.onclick = () => {
    if (NAV_CURRENT_PAGE === 'performance' && typeof openSettings === 'function')
      openSettings();
    else { openNav(); showNavTab('colors'); }
  };
  document.body.appendChild(gear);

  updateProfileUI();
  applyStoredLightMode();

  // Hide nav gear on perf page (it has its own gear)
  if (NAV_CURRENT_PAGE === 'performance') {
    const g = document.getElementById('nav-gear-btn');
    if (g) g.style.display = 'none';
  }
}

function buildPanelHTML() {
  const pageItems = NAV_PAGES.map(p => {
    const active = p.id === NAV_CURRENT_PAGE;
    const file   = NAV_PAGE_FILES[p.id];
    return `<div class="nv-item${active ? ' nv-active' : ''}" onclick="navView('${file}')" title="Open ${p.label}">
      <span class="nv-icon">${p.icon}</span>
      <span class="nv-lbl">${p.label}</span>
      <button class="nv-pop" onclick="event.stopPropagation();navPopOut('${p.id}')" title="Pop out to window">
        <span class="nv-pop-art">⬡</span> POP
      </button>
    </div>`;
  }).join('');

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
        <div class="nv-ver">Hardware Monitoring · v2.0 · Open Source</div>
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
      <div class="nv-items">${pageItems}</div>
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
    <div class="nv-footer">MIT License · <span onclick="sendToHost({type:'openUrl',url:'https://github.com/Vlottiz/vexis'})" style="cursor:pointer;text-decoration:underline">github.com/Vlottiz/vexis</span></div>
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
        <div style="font-size:10px;color:var(--label);margin-bottom:6px">Security Settings</div>
        <div style="font-size:9px;color:var(--label);opacity:.7;line-height:1.4;margin-bottom:8px">
          Control which Windows security features Vexis adjusts on startup —
          with full explanations for each one.
        </div>
        <button onclick="sendToHost({type:'navigate',file:'security.html'});closeNav();"
          class="nvc-reset-all" style="width:100%;justify-content:center">
          🛡 Open Security Settings
        </button>
      </div>

      <div style="padding:8px 0;border-bottom:1px solid rgba(68,51,0,.2)">
        <div style="font-size:10px;color:var(--label);margin-bottom:4px">Version</div>
        <div style="font-size:11px;color:var(--mem-acc);font-family:monospace">v2.5</div>
      </div>

      <button onclick="sendToHost({type:'openUrl',url:'https://github.com/Vlottiz/vexis'})"
        class="nvc-reset-all" style="width:100%;margin-top:8px;justify-content:center">
        ⊞ GitHub
      </button>
    </div>
`;
}

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

function navToggleSecurityBypass(enabled) {
  NAV_SETTINGS['disableSecurityBypass'] = enabled ? 'false' : 'true'; // enabled checkbox = bypass ON
  sendToHost({ type:'saveSettings', settings: NAV_SETTINGS });
  const lbl = document.querySelector('#nv-sec-bypass + span');
  if (lbl) lbl.textContent = enabled ? 'ENABLED' : 'DISABLED';
}

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
  s.textContent = `#popout-badge{position:fixed;top:5px;left:8px;z-index:9000;font-size:9px;font-family:monospace;letter-spacing:.1em;color:var(--header-title,#ffcc00);opacity:.4;background:rgba(0,0,0,.4);padding:3px 8px;border:1px solid rgba(68,51,0,.3);border-radius:2px;pointer-events:none;}`;
  document.head.appendChild(s);
  applyStoredLightMode();
}

// ── Styles ────────────────────────────────────────────────────────────────────
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
    #nav-btn.nv-shifted { transform:translateX(300px) !important; }

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
      width:300px !important;min-width:300px !important;max-width:300px !important;z-index:8900;
      display:flex;flex-direction:column;
      background:color-mix(in srgb,var(--card-bg,#030108) 97%,transparent);
      backdrop-filter:blur(28px) saturate(1.6);
      border-right:1px solid color-mix(in srgb,var(--border,rgba(68,51,0,.6)) 60%,transparent);
      transform:translateX(-100%);
      transition:transform .22s cubic-bezier(.4,0,.2,1);
    }
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

    /* ── Colors tab ── */
    .nvc-toolbar {
      display:flex;align-items:center;justify-content:space-between;
      padding:8px 12px 6px;flex-shrink:0;border-bottom:1px solid color-mix(in srgb,var(--border,#443300) 25%,transparent);
    }
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

    /* ── Data text scaling ──────────────────────────────────────────────────── */
    /* Applied globally so all pages scale sensor readings without per-page edits */
    :root { --data-scale:1; --nv-scale:1; }
    .fan-rpm-big      { font-size:calc(30px * var(--data-scale,1)) !important; }
    .stat-val,
    .temp-val, .val-big, .value-big,
    .core-clock-val, .pkg-val,
    [class$="-val"]:not([class*="label"]):not([class*="lbl"]) {
      font-size:calc(1em * var(--data-scale,1));
    }
  `;
  document.head.appendChild(s);
}

// ── Boot ──────────────────────────────────────────────────────────────────────
function boot() {
  if (NAV_IS_POPOUT) initPopoutBadge();
  else               initNav();
  navApplyStoredFontScale();
}

if (document.readyState === 'loading')
  document.addEventListener('DOMContentLoaded', boot);
else
  boot();
