// D3 UI Designer page. The server (DesignerServer.cs) owns the files and lays the layout out with the game's rules
// (UiLayout.cs); this page draws the result (design surface), shows the outline and a property grid, and sends edit operations.
'use strict';
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const dpr = () => window.devicePixelRatio || 1;
const hex = n => '0x' + n.toString(16).toUpperCase().padStart(2, '0');
const esc = s => String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;');

let PROPS = {}, TYPES = {};
let model = null;                 // {doc, controls, layout:{name, nodes, strings, marginX, marginY}}
let sel = [];                     // selected controls (full paths), last = primary
let mode = 'design';
let nodes = [], nodeByPath = new Map();
let screenRect = [0, 0, 1600, 1200];
let view = { scale: 1, ox: 0, oy: 0 }, fitted = false;
const collapsed = new Set();
const viewHidden = new Set();      // controls hidden in the designer only (the eye in the outline; the file is not changed)

// ------------------------------------------------------------------------------------------------- property grid model
// Categories of the grid (ids from the game's value table; see the ModKit docs "UI").
const CATS = [
  ['Layout', [0x07, 0x08, 0x09, 0x0A, 0x03, 0x04, 0x05, 0x06, 0x8C, 0x8D, 0xD1, 0x0B, 0x0C, 0x0D, 0x0E, 0x97]],
  ['Appearance', [0x10, 0x11, 0x12, 0x13, 0x20, 0x14, 0x1F, 0x0F]],
  ['Text', [0x41, 0x3A, 0x3B, 0x33, 0x34, 0x3D, 0x3E]],
  ['Behavior', [0x29, 0x63, 0x65, 0x69, 0x60, 0x61, 0x2C, 0x6E]],
  ['Template & style', [0x9D, 0x9E, 0xBB, 0x01]],
  ['Stack panel', [0x99, 0x9A, 0x9B, 0xA4, 0xA6]],
];
const ENUMS = {
  0x8C: [['', '(inherit)'], ['LEFT', 'Left'], ['RIGHT', 'Right'], ['STRETCH', 'Stretch'], ['CENTER', 'Center']],
  0x8D: [['', '(inherit)'], ['TOP', 'Top'], ['BOTTOM', 'Bottom'], ['CENTER', 'Center']],
  0xD1: [[0, 'none'], [1, 'center horizontally'], [2, 'center vertically'], [3, 'center'], [4, 'right']],
  0x3D: [[0, 'default'], [1, 'left'], [2, 'center'], [3, 'right']],
};
const TEXTURE_IDS = new Set([0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1F, 0x20]);
const CONTAINER_TYPES = new Set([2, 13, 14, 38]);
// Toolbox: what can be added (type, label, icon text, default size, defaults)
const TOOLS = [
  { type: 2, label: 'Panel / Window', ic: '▭', w: 300, h: 200 },
  { type: 2, label: 'Image', ic: '🖼', w: 128, h: 128, texture: 'UI:Stash_Slot_Empty', name: 'Image' },
  { type: 3, label: 'Button', ic: 'btn', w: 200, h: 48, texture: 'UI:BattleNetButton_RedUp_398x82' },
  { type: 4, label: 'Text', ic: 'Aa', w: 240, h: 32, text: 'Bnet_Campaign:MenuPlayGame' },
  { type: 18, label: 'Check box', ic: '☑', w: 32, h: 32 },
  { type: 22, label: 'Radio button', ic: '◉', w: 32, h: 32 },
  { type: 20, label: 'Dropdown', ic: '▾', w: 220, h: 32 },
  { type: 13, label: 'List box', ic: '☰', w: 300, h: 240 },
  { type: 38, label: 'Stack panel', ic: '⇵', w: 200, h: 200 },
];

function toast(msg, err) {
  const t = $('#toast'); t.textContent = msg; t.className = 'show' + (err ? ' err' : '');
  clearTimeout(toast.t); toast.t = setTimeout(() => t.className = '', err ? 4500 : 1800);
}
async function api(path, body) {
  const r = await fetch(path, body === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  const text = await r.text();
  if (!r.ok) throw new Error(text || r.statusText);
  return text ? JSON.parse(text) : null;
}

// ------------------------------------------------------------------------------------------------- left: tabs
$$('.tabs button').forEach(b => b.addEventListener('click', () => {
  $$('.tabs button').forEach(x => x.classList.toggle('on', x === b));
  $$('#left .pane').forEach(p => p.hidden = p.dataset.pane !== b.dataset.tab);
  if (b.dataset.tab === 'art') showTextures();
}));

// toolbox
$('#toolbox').innerHTML = TOOLS.map((t, k) => `<div class="tool" draggable="true" data-tool="${k}"><span class="ic">${t.ic}</span>${t.label}</div>`).join('');
$('#toolbox').addEventListener('dragstart', e => { const t = e.target.closest('.tool'); if (t) e.dataTransfer.setData('text/d3tool', t.dataset.tool); });
$('#toolbox').addEventListener('click', e => {
  const t = e.target.closest('.tool'); if (!t || !model) return;
  // into the selected container (or its parent), centred in it
  const parent = containerFor(primaryNode()) ?? defaultContainer();
  if (!parent) return toast('open a layout first', true);
  const r = displayRect(parent);
  addControl(TOOLS[+t.dataset.tool], parent, (r.left + r.right) / 2, (r.top + r.bottom) / 2);
});

// layouts
let catalog = { stock: [], custom: [] }, source = 'stock';
async function loadCatalog() {
  catalog = await api('/api/catalog');
  $('#projectDir').textContent = 'Project: ' + catalog.project;
  showCatalog();
}
function showCatalog() {
  const q = $('#filter').value.toLowerCase();
  const names = (source === 'stock' ? catalog.stock : catalog.custom).filter(n => n.toLowerCase().includes(q));
  $('#catalog').innerHTML = names.slice(0, 3000).map(n => `<div class="item" data-name="${encodeURIComponent(n)}" title="${esc(n)}">${esc(n)}</div>`).join('')
    || `<div class="item muted">${source === 'stock' ? 'No game data (check --install)' : 'No .ui files in the project yet'}</div>`;
}
$$('#layoutSource button').forEach(b => b.addEventListener('click', () => {
  source = b.dataset.src; $$('#layoutSource button').forEach(x => x.classList.toggle('on', x === b)); showCatalog();
}));
$('#filter').addEventListener('input', showCatalog);
$('#catalog').addEventListener('click', async e => {
  const it = e.target.closest('.item[data-name]'); if (!it) return;
  try { openModel(await api('/api/open', { source, name: decodeURIComponent(it.dataset.name) })); } catch (err) { toast(err.message, true); }
});

// art
let textures = null, artSno = 0, artPick = null;
async function showTextures() {
  if (!textures) { $('#textures').innerHTML = '<div class="item muted">Loading…</div>'; textures = await api('/api/textures'); }
  const q = $('#artFilter').value.toLowerCase();
  $('#textures').innerHTML = textures.filter(t => t.name.toLowerCase().includes(q)).map(t =>
    `<div class="item" data-sno="${t.sno}" title="${t.ns} namespace, SNO ${t.sno}${t.project ? ', in your project' : ''}">${esc(t.name)}<span class="t">${t.project ? 'project' : t.ns}</span></div>`).join('')
    || '<div class="item muted">No textures</div>';
}
$('#artFilter').addEventListener('input', showTextures);
$('#textures').addEventListener('click', e => { const it = e.target.closest('.item[data-sno]'); if (it) openArt(+it.dataset.sno).catch(err => toast(err.message, true)); });
async function openArt(sno) {
  const info = await api(`/api/texture/${sno}/frames`);
  const t = textures?.find(x => x.sno === sno);
  artSno = sno;
  $('#art').hidden = false;
  $('#art').classList.toggle('picking', !!artPick);
  $('#artTitle').textContent = (artPick ? 'Pick a frame for ' + artPick.label + ' — ' : '') + (t ? t.name : `SNO ${sno}`);
  $('#artInfo').textContent = `${info.width}×${info.height} ${info.format}, ${info.frames.length} frames`;
  $('#artImg').src = info.decodable ? `/api/texture/${sno}.png` : '';
  $('#artFrames').innerHTML = info.frames.filter(f => f.name).map(f =>
    `<div style="left:${f.x}px;top:${f.y}px;width:${f.w}px;height:${f.h}px" title="${t ? t.ns : 'UI'}:${esc(f.name)} (${f.w}×${f.h})" data-ref="${t ? t.ns : 'UI'}:${esc(f.name)}"></div>`).join('');
}
$('#artFrames').addEventListener('click', e => {
  const d = e.target.closest('div[data-ref]'); if (!d) return;
  $$('#artFrames .sel').forEach(x => x.classList.remove('sel')); d.classList.add('sel');
  if (artPick) { const p = artPick; artPick = null; $('#art').hidden = true; setValue(p.id, d.dataset.ref); return; }
  navigator.clipboard?.writeText(d.dataset.ref).catch(() => {});
  toast(`${d.dataset.ref} copied`);
});
$('#artClose').addEventListener('click', () => { artPick = null; $('#art').hidden = true; });
$('#artExtract').addEventListener('click', async () => {
  try { const r = await api('/api/extract-art', { sno: artSno }); toast(`${r.frames} frames + sheet → ${r.folder}`); } catch (err) { toast(err.message, true); }
});
// import: the page decodes the picture (any format the browser reads) and sends RGBA pixels; the server writes the texture
let importPixels = null;
$('#importArt').addEventListener('click', () => $('#importFile').click());
$('#importFile').addEventListener('change', async e => {
  const file = e.target.files[0]; e.target.value = '';
  if (!file) return;
  try {
    const bmp = await createImageBitmap(file);
    if (bmp.width > 4096 || bmp.height > 4096) return toast('images up to 4096×4096', true);
    const cv = document.createElement('canvas'); cv.width = bmp.width; cv.height = bmp.height;
    const cx = cv.getContext('2d'); cx.drawImage(bmp, 0, 0);
    importPixels = { width: bmp.width, height: bmp.height, data: cx.getImageData(0, 0, bmp.width, bmp.height).data };
    const base = file.name.replace(/\.[^.]+$/, '').replace(/[^A-Za-z0-9_]/g, '_');
    const modFromDoc = model?.doc.path ? model.doc.title.split(/[\\/]/)[0] : '';
    $('#impMod').value = modFromDoc || localStorageGet('d3designer.mod') || 'MyMod';
    $('#impName').value = base; $('#impFrame').value = base;
    $('#importPreview').textContent = `${file.name}: ${bmp.width}×${bmp.height}`;
    $('#impRef').textContent = 'UI:' + base;
    $('#importDialog').showModal();
  } catch (err) { toast('could not read the picture: ' + err.message, true); }
});
$('#impFrame').addEventListener('input', () => $('#impRef').textContent = 'UI:' + $('#impFrame').value);
$('#importDialog').addEventListener('close', async () => {
  if ($('#importDialog').returnValue !== 'ok' || !importPixels) return;
  const { width, height, data } = importPixels; importPixels = null;
  // base64 in chunks (large pictures)
  let bin = ''; for (let i = 0; i < data.length; i += 0x8000) bin += String.fromCharCode.apply(null, data.subarray(i, i + 0x8000));
  localStorageSet('d3designer.mod', $('#impMod').value.trim());
  try {
    const r = await api('/api/import-art', { mod: $('#impMod').value.trim(), name: $('#impName').value.trim(), width, height, rgba: btoa(bin),
      frames: [{ name: $('#impFrame').value.trim(), x: 0, y: 0, w: width, h: height }] });
    textures = null; await showTextures();
    images.delete(r.frames[0]);
    toast(`${r.frames[0]} imported → ${r.file}`);
    if (artPick) { const p = artPick; artPick = null; $('#art').hidden = true; setValue(p.id, r.frames[0]); }
    else openArt(r.sno);
  } catch (err) { toast(err.message, true); }
});
function localStorageGet(k) { try { return localStorage.getItem(k); } catch { return null; } }
function localStorageSet(k, v) { try { localStorage.setItem(k, v); } catch { } }

function pickTexture(id) {
  artPick = { id, label: PROPS[id]?.name ?? hex(id) };
  $$('.tabs button').find(b => b.dataset.tab === 'art').click();
  const cur = valueOf(primaryNode(), id);
  toast('Choose a texture on the left, then click a frame');
  if (cur && typeof cur.value === 'string') { const hit = textures?.find(t => cur.value.includes(':')); }
}

// ------------------------------------------------------------------------------------------------- model
const full = c => c.parent ? c.parent + '.' + c.name : c.name;
const controlByFull = f => model?.controls.find(c => full(c) === f);
const indexOf = f => controlByFull(f)?.i ?? -1;
const isOwn = n => n && n.definitionLayout === model.layout?.name && !n.fromTemplate;
const nodeOfControl = c => c && nodes.find(n => isOwn(n) && n.definitionIndex === c.i);
const primary = () => sel.length ? controlByFull(sel[sel.length - 1]) : null;
const primaryNode = () => nodeOfControl(primary());
const valueOf = (n, id) => n?.values.find(v => v.id === id);
const num = (n, id) => { const v = valueOf(n, id); return v ? parseFloat(v.value) : undefined; };
const isRootNode = n => !n.parentPath;

function openModel(m) {
  sel = []; fitted = false; collapsed.clear(); viewHidden.clear();
  $('#art').hidden = true;
  setModel(m);
}
function setModel(m) {
  model = m;
  sel = sel.filter(f => controlByFull(f));
  $('#docTitle').textContent = `${m.doc.title}${m.doc.dirty ? ' •' : ''} — SNO ${m.sno}, ${m.controls.length} controls`;
  $('#undo').disabled = !m.canUndo; $('#redo').disabled = !m.canRedo;
  const L = m.layout;
  if (L?.error) toast('layout: ' + L.error, true);
  nodes = L?.nodes ?? [];
  nodeByPath = new Map(nodes.map(n => [n.path, n]));
  screenRect = L && !L.error ? [-L.marginX, -L.marginY, 1600 + L.marginX, 1200 + L.marginY] : [0, 0, 1600, 1200];
  if (!fitted) { fitView(); fitted = true; }
  showTree(); showProps(); draw();
}
function isHidden(n) {
  for (let p = n; p; p = nodeByPath.get(p.parentPath)) if (p.startsHidden && !isRootNode(p)) return true;   // the window itself is always drawn
  return false;
}
/** A control's rectangle; containers without a size of their own get the area their children cover. */
function displayRect(n) {
  const r = n.rect;
  if (r.right - r.left > 0.5 && r.bottom - r.top > 0.5) return r;
  let u = null;
  for (const d of nodes) if (d.path.startsWith(n.path + '.')) {
    const q = d.rect; if (q.right - q.left <= 0.5 || q.bottom - q.top <= 0.5) continue;
    u = u ? { left: Math.min(u.left, q.left), top: Math.min(u.top, q.top), right: Math.max(u.right, q.right), bottom: Math.max(u.bottom, q.bottom) } : { ...q };
  }
  return u ?? { left: r.left, top: r.top, right: r.left + 1, bottom: r.top + 1 };
}
/** The record to edit for a drawn node: its own, or the nearest ancestor that belongs to this layout. */
function editableNode(n) { for (let p = n; p; p = nodeByPath.get(p.parentPath)) if (isOwn(p)) return p; return null; }
function containerFor(n) { for (let p = n; p; p = nodeByPath.get(p.parentPath)) if (isOwn(p) && CONTAINER_TYPES.has(p.type)) return p; return null; }
function defaultContainer() { return nodes.find(n => isOwn(n) && n.name === 'LayoutRoot') ?? nodes.find(isOwn); }

function select(paths, additive) {
  if (additive) for (const p of paths) { const k = sel.indexOf(p); if (k >= 0) sel.splice(k, 1); else sel.push(p); }
  else sel = [...paths];
  showTree(); showProps(); draw();
}

// ------------------------------------------------------------------------------------------------- outline
function showTree() {
  const el = $('#tree');
  if (!model) { el.innerHTML = ''; return; }
  const q = $('#treeFilter').value.toLowerCase();
  const kids = new Map();
  for (const c of model.controls) { const p = c.parent || ''; if (!kids.has(p)) kids.set(p, []); kids.get(p).push(c); }
  const rows = [];
  const walk = (parent, depth) => {
    for (const c of kids.get(parent) ?? []) {
      const f = full(c), has = kids.has(f), match = !q || f.toLowerCase().includes(q);
      const template = c.styles.some(s => s[0] === 1 && s[1] === '1');
      if (match) rows.push(`<div class="item${sel.includes(f) ? ' sel' : ''}${sel[sel.length - 1] === f ? ' prim' : ''}${template ? ' dim' : ''}" draggable="true"
        data-f="${esc(f)}" style="padding-left:${4 + depth * 14}px" title="${esc(f)}${template ? ' (template)' : ''}">
        <span class="tw" data-toggle="${esc(f)}">${has ? (collapsed.has(f) && !q ? '▸' : '▾') : ''}</span>${esc(c.name)}<span class="t">${TYPES[c.type]?.cls ?? 'type ' + c.type}</span><span class="eye" data-eye="${esc(f)}" title="Show/hide in the designer only (Alt+click: show only this one among its siblings)">${viewHidden.has(f) ? '◌' : '👁'}</span></div>`);
      if (has && (q || !collapsed.has(f))) walk(f, depth + 1);
    }
  };
  walk('', 0);
  el.innerHTML = rows.join('') || '<div class="item muted">No controls</div>';
  el.querySelector('.prim')?.scrollIntoView({ block: 'nearest' });
}
$('#treeFilter').addEventListener('input', showTree);
$('#tree').addEventListener('click', e => {
  const eye = e.target.closest('[data-eye]');
  if (eye) {
    const f = eye.dataset.eye;
    if (e.altKey) {   // solo: hide the siblings, show this one
      const c = controlByFull(f);
      for (const s of model.controls.filter(x => x.parent === c.parent)) viewHidden.add(full(s));
      viewHidden.delete(f);
    } else viewHidden.has(f) ? viewHidden.delete(f) : viewHidden.add(f);
    showTree(); draw(); return;
  }
  const tw = e.target.closest('[data-toggle]');
  if (tw && tw.textContent) { const f = tw.dataset.toggle; collapsed.has(f) ? collapsed.delete(f) : collapsed.add(f); showTree(); return; }
  const it = e.target.closest('.item[data-f]'); if (it) select([it.dataset.f], e.ctrlKey || e.shiftKey);
});
$('#tree').addEventListener('dblclick', e => {
  const it = e.target.closest('.item[data-f]'); if (!it) return;
  const f = it.dataset.f; collapsed.has(f) ? collapsed.delete(f) : collapsed.add(f); showTree();
});
// drag a control onto another: it moves into it (keeping its place on screen when it is placed by x/y)
$('#tree').addEventListener('dragstart', e => { const it = e.target.closest('.item[data-f]'); if (it) e.dataTransfer.setData('text/d3node', it.dataset.f); });
$('#tree').addEventListener('dragover', e => { const it = e.target.closest('.item[data-f]'); if (it) { e.preventDefault(); $$('#tree .drop').forEach(x => x.classList.remove('drop')); it.classList.add('drop'); } });
$('#tree').addEventListener('dragleave', () => $$('#tree .drop').forEach(x => x.classList.remove('drop')));
$('#tree').addEventListener('drop', e => {
  $$('#tree .drop').forEach(x => x.classList.remove('drop'));
  const it = e.target.closest('.item[data-f]'), from = e.dataTransfer.getData('text/d3node');
  if (!it || !from || it.dataset.f === from) return;
  e.preventDefault();
  moveInto(from, it.dataset.f);
});
function moveInto(childFull, parentFull) {
  const c = controlByFull(childFull), p = controlByFull(parentFull);
  if (!c || !p) return;
  const cn = nodeOfControl(c), pn = nodeOfControl(p);
  // position values first: the move itself changes record indexes
  const ops = [];
  if (cn && pn && !valueOf(cn, 0x03) && !valueOf(cn, 0x04)) {
    ops.push({ op: 'setInt', control: c.i, id: 0x07, value: Math.round(cn.designRect.left - pn.designRect.left) });
    ops.push({ op: 'setInt', control: c.i, id: 0x08, value: Math.round(cn.designRect.top - pn.designRect.top) });
  }
  ops.push({ op: 'reparent', control: c.i, parent: p.i });
  sel = [parentFull + '.' + c.name];
  collapsed.delete(parentFull);
  send({ op: 'batch', ops });
}

// ------------------------------------------------------------------------------------------------- properties
function showProps() {
  const el = $('#props');
  const c = primary(), n = primaryNode();
  $('#propTarget').textContent = c ? c.name : '';
  if (!c) { el.innerHTML = `<div class="muted pad">${model ? 'Select a control on the surface or in the outline.' : 'Open a layout (Layouts tab) or create one (New…).'}</div>`; return; }
  const own = id => c.styles.some(s => s[0] === id) || c.ints.some(s => s[0] === id) || c.floats.some(s => s[0] === id);
  const shown = new Set();
  const row = id => {
    shown.add(id);
    const info = PROPS[id] ?? { name: hex(id), kind: 'Int', meaning: '' };
    const v = valueOf(n, id), isOwnValue = own(id);
    const raw = v ? v.value : (info.default ?? '');
    let editor;
    if (ENUMS[id]) {
      const cur = info.kind === 'String' ? String(raw).toUpperCase() : String(parseInt(raw) || 0);
      editor = `<select data-id="${id}">${ENUMS[id].map(([val, label]) => `<option value="${val}"${String(val) === cur ? ' selected' : ''}>${label}</option>`).join('')}</select>`;
    } else if (info.kind === 'Bool') {
      editor = `<input type="checkbox" data-id="${id}"${parseInt(raw) ? ' checked' : ''}>`;
    } else if (info.kind === 'Color') {
      const u = (parseInt(raw) >>> 0) || 0;
      editor = `<input type="color" data-color="${id}" value="#${(u & 0xFFFFFF).toString(16).padStart(6, '0')}"><input data-id="${id}" value="${v ? esc(v.value) : ''}" placeholder="0xAARRGGBB">`;
    } else if (TEXTURE_IDS.has(id)) {
      editor = `<input data-id="${id}" value="${esc(v ? v.value : '')}" placeholder="UI:FrameName"><button class="pick" data-pick="${id}" title="Choose from the art">…</button>`;
    } else {
      editor = `<input data-id="${id}" value="${esc(v ? v.value : '')}" placeholder="${esc(info.default ?? '')}"${info.kind === 'Int' || info.kind === 'Float' ? ' inputmode="decimal"' : ''}>`;
    }
    const hint = id === 0x41 && v ? (model.layout.strings?.[v.value] ?? '(no such text)') : '';
    const src = v && v.source !== 'Own' ? ` — from ${v.source.toLowerCase()}` : '';
    return `<div class="prow${v && !isOwnValue ? ' inherited' : ''}" title="${esc(info.meaning)}${src}">
      <span class="pn"><b>${hex(id)}</b>${esc(info.name)}</span><span class="pv">${editor}</span>
      <button class="reset" data-reset="${id}" title="Remove the control's own value"${isOwnValue ? '' : ' disabled'}>×</button>
      ${hint ? `<span class="hint">“${esc(hint)}”</span>` : ''}</div>`;
  };
  const cat = (title, inner, closed) => `<div class="cat${closed ? ' closed' : ''}" data-cat="${title}"><div class="head">${title}</div><div class="body">${inner}</div></div>`;
  const ident = `<div class="prow"><span class="pn">Name</span><span class="pv"><input id="pName" value="${esc(c.name)}"></span><span></span></div>
    <div class="prow"><span class="pn">Type</span><span class="pv"><select id="pType">${Object.values(TYPES).map(t => `<option value="${t.type}"${t.type === c.type ? ' selected' : ''}>${t.type} ${esc(t.cls)}</option>`).join('')}${TYPES[c.type] ? '' : `<option selected value="${c.type}">${c.type}</option>`}</select></span><span></span></div>
    <div class="prow"><span class="pn">Parent</span><span class="pv ro" title="${esc(c.parent)}">${esc(c.parent || '(root)')}</span><span></span></div>
    <div class="prow"><span class="pn">On screen</span><span class="pv ro">${n ? (() => { const r = n.rect; return `${Math.round(r.left)}, ${Math.round(r.top)} — ${Math.round(r.right - r.left)}×${Math.round(r.bottom - r.top)} (${n.hAnchor}/${n.vAnchor})`; })() : 'not drawn'}</span><span></span></div>`;
  const parentNode = n ? nodeByPath.get(n.parentPath) : null;
  const stackNote = parentNode?.type === 38
    ? `<div class="prow"><span class="hint" style="grid-column:1/4">Placed by its stack panel “${esc(parentNode.name)}”: move it by changing the order or the panel's spacing/padding; x/y are ignored.</span></div>` : '';
  let html = cat('Control', ident + stackNote);
  for (const [title, ids] of CATS) {
    if (title === 'Stack panel' && c.type !== 38) continue;
    html += cat(title, ids.map(row).join(''), title === 'Behavior' || title === 'Template & style');
  }
  const rest = new Set([...(n?.values ?? []).map(v => v.id), ...c.styles.map(s => s[0]), ...c.ints.map(s => s[0]), ...c.floats.map(s => s[0])]);
  const others = [...rest].filter(id => !shown.has(id) && id !== 0).sort((a, b) => a - b);
  html += cat('Other values', others.map(row).join('') +
    `<div class="addrow"><input id="addId" placeholder="name or id (hex)" list="propList"><input id="addVal" placeholder="value"><button id="addBtn">Add</button></div>`, others.length === 0);
  el.innerHTML = html;
  el.querySelectorAll('.cat .head').forEach(h => h.addEventListener('click', () => h.parentElement.classList.toggle('closed')));
  $('#pName').addEventListener('change', e => send({ op: 'rename', control: c.i, value: e.target.value }, f => sel = [(c.parent ? c.parent + '.' : '') + e.target.value]));
  $('#pType').addEventListener('change', e => send({ op: 'setType', control: c.i, value: +e.target.value }));
  el.querySelectorAll('[data-id]').forEach(inp => inp.addEventListener('change', () => {
    const id = +inp.dataset.id;
    setValue(id, inp.type === 'checkbox' ? (inp.checked ? 1 : 0) : inp.value);
  }));
  el.querySelectorAll('[data-color]').forEach(inp => inp.addEventListener('change', () => {
    const id = +inp.dataset.color, v = valueOf(n, id), a = v ? ((parseInt(v.value) >>> 24) & 255) : 255;
    setValue(id, '0x' + ((a << 24 >>> 0) + parseInt(inp.value.slice(1), 16)).toString(16).toUpperCase().padStart(8, '0'));
  }));
  el.querySelectorAll('[data-pick]').forEach(b => b.addEventListener('click', () => pickTexture(+b.dataset.pick)));
  el.querySelectorAll('[data-reset]').forEach(b => b.addEventListener('click', () => {
    const id = +b.dataset.reset;
    const kind = c.styles.some(s => s[0] === id) ? 'Style' : c.floats.some(s => s[0] === id) ? 'Float' : 'Int';
    send({ op: 'remove' + kind, control: c.i, id });
  }));
  $('#addBtn').addEventListener('click', () => {
    const t = $('#addId').value.trim();
    let id = /^0x/i.test(t) ? parseInt(t, 16) : /^\d+$/.test(t) ? parseInt(t, 10) : NaN;
    if (Number.isNaN(id)) { const hit = Object.values(PROPS).find(p => p.name.toLowerCase() === t.toLowerCase()); if (hit) id = hit.id; }
    if (Number.isNaN(id)) return toast('give a property name or an id like 0x41', true);
    setValue(id, $('#addVal').value);
  });
}
/** Sets a value on the primary control with the right kind (string / int / float), parsing hex and colours. */
function setValue(id, raw) {
  const c = primary(); if (!c) return;
  const kind = PROPS[id]?.kind ?? 'Int';
  if (kind === 'String') return send({ op: 'setStyle', control: c.i, id, value: String(raw) });
  const text = String(raw).trim();
  if (text === '') return send({ op: (c.floats.some(s => s[0] === id) ? 'removeFloat' : 'removeInt'), control: c.i, id });
  let v = /^0x/i.test(text) ? parseInt(text, 16) : parseFloat(text);
  if (Number.isNaN(v)) return toast(`${PROPS[id]?.name ?? hex(id)} needs a number`, true);
  if (kind === 'Color') v = v | 0;   // ARGB as a signed int
  send({ op: kind === 'Float' ? 'setFloat' : 'setInt', control: c.i, id, value: v });
}

async function send(op, then) {
  if (!model) return;
  try { const m = await api(`/api/doc/${model.doc.id}/op`, op); then?.(); setModel(m); }
  catch (err) { toast(err.message, true); }
}

/** The ops that move/resize a control by deltas: on the values its position comes from (0x03..0x06 or 0x07..0x0A). */
function geometryOps(n, dx, dy, dw, dh) {
  const i = n.definitionIndex, ops = [], set = (id, v) => ops.push({ op: 'setInt', control: i, id, value: Math.round(v) });
  if (valueOf(n, 0x03) || valueOf(n, 0x04)) {
    const l = num(n, 0x03) ?? 0, t = num(n, 0x04) ?? 0;
    if (dx || dw) { if (dx) set(0x03, l + dx); if (valueOf(n, 0x05)) set(0x05, num(n, 0x05) + dx + dw); else if (dw) set(0x09, (num(n, 0x09) ?? 0) + dw); }
    if (dy || dh) { if (dy) set(0x04, t + dy); if (valueOf(n, 0x06)) set(0x06, num(n, 0x06) + dy + dh); else if (dh) set(0x0A, (num(n, 0x0A) ?? 0) + dh); }
  } else {
    if (dx) set(0x07, (num(n, 0x07) ?? 0) + dx);
    if (dy) set(0x08, (num(n, 0x08) ?? 0) + dy);
    if (dw) set(0x09, Math.max(1, (num(n, 0x09) ?? (n.rect.right - n.rect.left)) + dw));
    if (dh) set(0x0A, Math.max(1, (num(n, 0x0A) ?? (n.rect.bottom - n.rect.top)) + dh));
  }
  return ops;
}

function addControl(tool, parentNode, ux, uy) {
  // the drop point in the parent's design space (final rects include the anchor offset)
  const off = parentNode.rect.left - parentNode.designRect.left, offY = parentNode.rect.top - parentNode.designRect.top;
  const x = Math.round(ux - off - parentNode.designRect.left - tool.w / 2), y = Math.round(uy - offY - parentNode.designRect.top - tool.h / 2);
  const parentFull = parentNode.path;
  const name = tool.name;
  send({ op: 'add', parent: parentFull, type: tool.type, name, x, y, w: tool.w, h: tool.h, texture: tool.texture, text: tool.text }, () => {});
  // select the new control after the model arrives (the newest child of the parent)
  const before = new Set(model.controls.map(full));
  const wait = setInterval(() => {
    const added = model.controls.map(full).filter(f => !before.has(f));
    if (added.length) { clearInterval(wait); select([added[0]]); }
  }, 50);
  setTimeout(() => clearInterval(wait), 3000);
}

function group() {
  const items = sel.map(controlByFull).filter(Boolean);
  if (items.length < 1) return toast('select the controls to group (Ctrl+click)', true);
  const parent = items[0].parent;
  if (items.some(c => c.parent !== parent)) return toast('grouped controls must have the same parent', true);
  const ns = items.map(nodeOfControl).filter(Boolean);
  if (ns.length !== items.length) return toast('only drawn controls can be grouped', true);
  const pn = nodeOfControl(controlByFull(parent));
  const px = pn ? pn.designRect.left : 0, py = pn ? pn.designRect.top : 0;
  const box = ns.reduce((b, n) => { const r = n.designRect; return { l: Math.min(b.l, r.left), t: Math.min(b.t, r.top), r: Math.max(b.r, r.right), b: Math.max(b.b, r.bottom) }; },
    { l: Infinity, t: Infinity, r: -Infinity, b: -Infinity });
  const children = ns.map(n => ({ path: n.path, ...(valueOf(n, 0x03) || valueOf(n, 0x04) ? {} : { x: Math.round(n.designRect.left - box.l), y: Math.round(n.designRect.top - box.t) }) }));
  const name = prompt('Name of the new group panel:', 'Group');
  if (!name) return;
  send({ op: 'group', name, children, x: Math.round(box.l - px), y: Math.round(box.t - py), w: Math.round(box.r - box.l), h: Math.round(box.b - box.t) },
    () => { sel = [(parent ? parent + '.' : '') + name]; });
}
$('#btnGroup').addEventListener('click', group);
$('#btnDup').addEventListener('click', () => { const c = primary(); if (c) send({ op: 'duplicate', control: c.i }); });
$('#btnDel').addEventListener('click', () => {
  const items = sel.map(controlByFull).filter(Boolean);
  if (!items.length || !confirm(`Delete ${items.length === 1 ? items[0].name : items.length + ' controls'} and everything inside?`)) return;
  // delete from the end so indexes stay valid
  const ops = items.map(c => c.i).sort((a, b) => b - a).map(i => ({ op: 'delete', control: i }));
  sel = [];
  send({ op: 'batch', ops });
});

// ------------------------------------------------------------------------------------------------- design surface
const canvas = $('#canvas'), g = canvas.getContext('2d');
const images = new Map();
function image(ref) {
  if (!ref) return null;
  if (images.has(ref)) return images.get(ref);
  images.set(ref, null);
  const img = new Image();
  img.onload = () => { images.set(ref, img); draw(); };
  img.src = '/api/frame?ref=' + encodeURIComponent(ref);
  return null;
}
function resizeCanvas() { const st = $('#stage'); canvas.width = st.clientWidth * dpr(); canvas.height = st.clientHeight * dpr(); }
function fitView() {
  resizeCanvas();
  const w = screenRect[2] - screenRect[0], h = screenRect[3] - screenRect[1];
  const s = Math.min(canvas.width / w, canvas.height / h) * 0.96;
  view = { scale: s, ox: (canvas.width - w * s) / 2 - screenRect[0] * s, oy: (canvas.height - h * s) / 2 - screenRect[1] * s };
}
function zoomAt(factor, px, py) {
  const s = Math.min(8, Math.max(0.05, view.scale * factor)), f = s / view.scale;
  view = { scale: s, ox: px - (px - view.ox) * f, oy: py - (py - view.oy) * f };
  draw();
}
const sx = x => view.ox + x * view.scale, sy = y => view.oy + y * view.scale;
function argb(c, alpha = 1) {
  if (c === undefined || c === null) return `rgba(205,157,81,${alpha})`;
  return `rgba(${(c >>> 16) & 255},${(c >>> 8) & 255},${c & 255},${(((c >>> 24) & 255) / 255) * alpha})`;
}
function viewHiddenNode(n) { for (const f of viewHidden) if (n.path === f || n.path.startsWith(f + '.')) return true; return false; }
function visible(n) { if (viewHiddenNode(n)) return false; return mode === 'preview' ? !isHidden(n) : ($('#showHidden').checked || !isHidden(n)); }
function draw() {
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.clearRect(0, 0, canvas.width, canvas.height);
  g.fillStyle = '#000';
  g.fillRect(sx(screenRect[0]), sy(screenRect[1]), (screenRect[2] - screenRect[0]) * view.scale, (screenRect[3] - screenRect[1]) * view.scale);
  if (mode === 'design') { g.strokeStyle = 'rgba(255,255,255,.14)'; g.setLineDash([4, 4]); g.strokeRect(sx(0), sy(0), 1600 * view.scale, 1200 * view.scale); g.setLineDash([]); }
  if (!model) return;
  const outlines = mode === 'design' && $('#showOutlines').checked;
  for (const n of nodes) {
    if (!visible(n)) continue;
    const r = n.rect, x = sx(r.left), y = sy(r.top), w = (r.right - r.left) * view.scale, h = (r.bottom - r.top) * view.scale;
    if (w <= 0 || h <= 0) continue;
    const alpha = (n.alpha ?? 255) / 255;
    const img = image(n.texture);
    if (img) { g.globalAlpha = alpha; g.drawImage(img, x, y, w, h); g.globalAlpha = 1; }
    if (n.text) drawText(n, x, y, w, h, alpha);
    if (outlines) { g.strokeStyle = isOwn(n) ? 'rgba(127,178,229,.32)' : 'rgba(127,178,229,.14)'; g.lineWidth = 1; g.strokeRect(x + .5, y + .5, w, h); }
  }
  if (mode === 'design') drawSelection();
  if (band) { g.strokeStyle = '#7fb2e5'; g.setLineDash([5, 3]); g.lineWidth = 1; g.strokeRect(sx(band.x0), sy(band.y0), (band.x1 - band.x0) * view.scale, (band.y1 - band.y0) * view.scale); g.setLineDash([]); }
}
function drawText(n, x, y, w, h, alpha) {
  const size = (n.fontSize ?? 24) * view.scale;
  if (size < 4) return;
  let text = (model.layout.strings?.[n.text] ?? n.text).replace(/\{[^}]*\}/g, '');
  const exocet = /exocet/i.test(n.font ?? 'ExocetLight');
  if (exocet) text = text.toUpperCase();
  g.save(); g.beginPath(); g.rect(x, y, w, h); g.clip();
  g.font = `${exocet ? '600 ' : ''}${size * (exocet ? 0.8 : 1)}px ${exocet ? '"Palatino Linotype", Georgia' : 'Georgia'}, serif`;
  if ('letterSpacing' in g) g.letterSpacing = exocet ? `${size * 0.08}px` : '0px';
  g.fillStyle = argb(n.textColor, alpha); g.textBaseline = 'middle';
  const al = n.textHAlign ?? 1;
  g.textAlign = al === 2 ? 'center' : al === 3 ? 'right' : 'left';
  const lines = text.split('\n'), lh = size * 1.15, y0 = y + h / 2 - (lines.length - 1) * lh / 2;
  lines.forEach((line, k) => g.fillText(line, al === 2 ? x + w / 2 : al === 3 ? x + w - 2 : x + 2, y0 + k * lh));
  g.restore();
}
const HANDLES = ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w'];
function handlePoints(r) {
  const cx = (r.left + r.right) / 2, cy = (r.top + r.bottom) / 2;
  return { nw: [r.left, r.top], n: [cx, r.top], ne: [r.right, r.top], e: [r.right, cy], se: [r.right, r.bottom], s: [cx, r.bottom], sw: [r.left, r.bottom], w: [r.left, cy] };
}
function drawSelection() {
  const css = getComputedStyle(document.documentElement), color = css.getPropertyValue('--sel').trim() || '#ffcc66';
  sel.forEach((f, k) => {
    const n = nodeOfControl(controlByFull(f)); if (!n) return;
    const r = displayRect(n), x = sx(r.left), y = sy(r.top), w = (r.right - r.left) * view.scale, h = (r.bottom - r.top) * view.scale;
    g.strokeStyle = color; g.lineWidth = 1.5 * dpr(); g.strokeRect(x, y, w, h);
    g.fillStyle = color; g.font = `${11 * dpr()}px Segoe UI`; g.textBaseline = 'bottom'; g.textAlign = 'left';
    g.fillText(n.name, x + 2, y - 2);
    if (k === sel.length - 1 && sel.length === 1 && hasSize(n)) {
      const hp = handlePoints(r), s = 4 * dpr();
      for (const hnd of HANDLES) { const [hx, hy] = hp[hnd]; g.fillStyle = '#111'; g.fillRect(sx(hx) - s - 1, sy(hy) - s - 1, 2 * s + 2, 2 * s + 2); g.fillStyle = color; g.fillRect(sx(hx) - s, sy(hy) - s, 2 * s, 2 * s); }
    }
  });
}
const hasSize = n => n.rect.right - n.rect.left > 0.5 && n.rect.bottom - n.rect.top > 0.5;

// mouse: select, move, resize from 8 handles, rubber band, pan (middle button / Space), wheel zoom
let drag = null, band = null, spaceDown = false;
function toUi(ev) { const b = canvas.getBoundingClientRect(); return [((ev.clientX - b.left) * dpr() - view.ox) / view.scale, ((ev.clientY - b.top) * dpr() - view.oy) / view.scale]; }
function hitHandle(ux, uy) {
  const n = primaryNode(); if (!n || sel.length !== 1 || !hasSize(n)) return null;
  const hp = handlePoints(displayRect(n)), tol = 7 * dpr() / view.scale;
  return HANDLES.find(h => Math.abs(ux - hp[h][0]) < tol && Math.abs(uy - hp[h][1]) < tol) ?? null;
}
function hitNodes(ux, uy) {
  const hits = nodes.filter(n => visible(n) && editableNode(n)).filter(n => { const r = displayRect(n); return ux >= r.left && uy >= r.top && ux <= r.right && uy <= r.bottom; });
  const area = n => { const r = displayRect(n); return (r.right - r.left) * (r.bottom - r.top); };
  return hits.sort((a, b) => area(a) - area(b));
}
canvas.addEventListener('mousedown', ev => {
  canvas.focus?.();
  if (ev.button === 1 || spaceDown) { drag = { mode: 'pan', x: ev.clientX, y: ev.clientY }; ev.preventDefault(); return; }
  if (!model || mode !== 'design' || ev.button !== 0) return;
  const [ux, uy] = toUi(ev);
  const hnd = hitHandle(ux, uy);
  if (hnd) { drag = { mode: 'resize', hnd, sx: ux, sy: uy, dx: 0, dy: 0 }; return; }
  const hits = hitNodes(ux, uy);
  if (!hits.length) { if (!ev.ctrlKey) sel = []; band = { x0: ux, y0: uy, x1: ux, y1: uy, add: ev.ctrlKey }; showTree(); showProps(); draw(); return; }
  let pick = hits[0];
  if (ev.altKey) { const cur = primaryNode(); const k = hits.indexOf(cur); if (k >= 0) pick = hits[(k + 1) % hits.length]; }
  const target = editableNode(pick);
  if (!target) return;
  if (ev.ctrlKey) select([target.path], true);
  else if (!sel.includes(target.path)) select([target.path]);
  else { sel.splice(sel.indexOf(target.path), 1); sel.push(target.path); showProps(); draw(); }
  drag = { mode: 'move', sx: ux, sy: uy, dx: 0, dy: 0 };
});
window.addEventListener('mousemove', ev => {
  if (model && ev.target === canvas) { const [ux, uy] = toUi(ev); $('#status').textContent = `${Math.round(ux)}, ${Math.round(uy)}   ${sel.length ? sel[sel.length - 1] + (sel.length > 1 ? `  (+${sel.length - 1})` : '') : ''}`; }
  if (band) { [band.x1, band.y1] = toUi(ev); draw(); return; }
  if (!drag) return;
  if (drag.mode === 'pan') { view.ox += (ev.clientX - drag.x) * dpr(); view.oy += (ev.clientY - drag.y) * dpr(); drag.x = ev.clientX; drag.y = ev.clientY; draw(); return; }
  const [ux, uy] = toUi(ev), dx = Math.round(ux - drag.sx), dy = Math.round(uy - drag.sy);
  if (!dx && !dy) return;
  drag.sx += dx; drag.sy += dy; drag.dx += dx; drag.dy += dy;
  const shift = (n, l, t, r, b) => { n.rect.left += l; n.rect.top += t; n.rect.right += r; n.rect.bottom += b; };
  if (drag.mode === 'move') {
    for (const f of sel) { const n = nodeOfControl(controlByFull(f)); if (!n) continue; for (const d of nodes) if (d === n || d.path.startsWith(n.path + '.')) shift(d, dx, dy, dx, dy); }
  } else {
    const n = primaryNode(), h = drag.hnd;
    shift(n, h.includes('w') ? dx : 0, h.includes('n') ? dy : 0, h.includes('e') ? dx : 0, h.includes('s') ? dy : 0);
  }
  draw();
});
window.addEventListener('mouseup', () => {
  if (band) {
    const b = band; band = null;
    const l = Math.min(b.x0, b.x1), r = Math.max(b.x0, b.x1), t = Math.min(b.y0, b.y1), bo = Math.max(b.y0, b.y1);
    if (r - l > 3 && bo - t > 3) {
      const inside = nodes.filter(n => isOwn(n) && visible(n) && !isRootNode(n)).filter(n => { const q = displayRect(n); return q.left >= l && q.right <= r && q.top >= t && q.bottom <= bo; });
      // keep only the outermost of nested hits
      const paths = inside.map(n => n.path).filter(p => !inside.some(o => p.startsWith(o.path + '.')));
      select(paths, b.add);
    } else draw();
    return;
  }
  if (!drag) return;
  const d = drag; drag = null;
  if (d.mode === 'pan' || (!d.dx && !d.dy)) return;
  let ops = [];
  if (d.mode === 'move') for (const f of sel) { const n = nodeOfControl(controlByFull(f)); if (n) ops = ops.concat(geometryOps(n, d.dx, d.dy, 0, 0)); }
  else {
    const n = primaryNode(), h = d.hnd;
    const mx = h.includes('w') ? d.dx : 0, my = h.includes('n') ? d.dy : 0;
    const dw = h.includes('e') ? d.dx : h.includes('w') ? -d.dx : 0, dh = h.includes('s') ? d.dy : h.includes('n') ? -d.dy : 0;
    ops = geometryOps(n, mx, my, dw, dh);
  }
  if (ops.length) send({ op: 'batch', ops });
});
canvas.addEventListener('wheel', ev => {
  ev.preventDefault();
  const b = canvas.getBoundingClientRect();
  zoomAt(ev.deltaY < 0 ? 1.15 : 1 / 1.15, (ev.clientX - b.left) * dpr(), (ev.clientY - b.top) * dpr());
}, { passive: false });
canvas.addEventListener('mousemove', ev => {
  if (drag || !model || mode !== 'design') return;
  const [ux, uy] = toUi(ev), h = hitHandle(ux, uy);
  canvas.style.cursor = spaceDown ? 'grab' : h ? ({ n: 'ns', s: 'ns', e: 'ew', w: 'ew', nw: 'nwse', se: 'nwse', ne: 'nesw', sw: 'nesw' }[h] + '-resize') : 'default';
});
// toolbox drop
canvas.addEventListener('dragover', e => { if ([...e.dataTransfer.types].includes('text/d3tool')) e.preventDefault(); });
canvas.addEventListener('drop', e => {
  const k = e.dataTransfer.getData('text/d3tool'); if (k === '' || !model) return;
  e.preventDefault();
  const [ux, uy] = toUi(e);
  const hit = hitNodes(ux, uy).map(containerFor).find(Boolean) ?? defaultContainer();
  if (!hit) return toast('open a layout first', true);
  addControl(TOOLS[+k], hit, ux, uy);
});
new ResizeObserver(() => { const keep = { ...view }; resizeCanvas(); view = keep; draw(); }).observe($('#stage'));

// ------------------------------------------------------------------------------------------------- commands
async function history(kind) { if (model) try { setModel(await api(`/api/doc/${model.doc.id}/${kind}`, {})); } catch (err) { toast(err.message, true); } }
async function save(asNew) {
  if (!model) return;
  let path;
  if (asNew || !model.doc.path) {
    const name = model.doc.title.replace(/^.*[\\/]/, '').replace(/\.ui$/i, '');
    path = prompt('Save into the project folder as (a mod\'s data\\Base\\UI\\<Name>.ui):', `MyMod\\data\\Base\\UI\\${name}.ui`);
    if (!path) return;
  }
  try { setModel(await api(`/api/doc/${model.doc.id}/save`, path ? { path } : {})); toast('Saved'); loadCatalog(); } catch (err) { toast(err.message, true); }
}
$('#undo').addEventListener('click', () => history('undo'));
$('#redo').addEventListener('click', () => history('redo'));
$('#save').addEventListener('click', () => save(false));
$('#saveAs').addEventListener('click', () => save(true));
$('#zoomIn').addEventListener('click', () => zoomAt(1.25, canvas.width / 2, canvas.height / 2));
$('#zoomOut').addEventListener('click', () => zoomAt(0.8, canvas.width / 2, canvas.height / 2));
$('#zoomFit').addEventListener('click', () => { fitView(); draw(); });
$('#showOutlines').addEventListener('change', draw);
$('#showHidden').addEventListener('change', draw);
$$('#mode button').forEach(b => b.addEventListener('click', () => { mode = b.dataset.mode; $$('#mode button').forEach(x => x.classList.toggle('on', x === b)); draw(); }));
$('#screen').addEventListener('change', async e => {
  const [w, h] = e.target.value.split('x').map(Number);
  await api('/api/screen', { width: w, height: h });
  if (model) { fitted = false; setModel(await api(`/api/doc/${model.doc.id}`)); }
});
$('#newLayout').addEventListener('click', () => $('#newDialog').showModal());
$('#newDialog').addEventListener('close', async () => {
  if ($('#newDialog').returnValue !== 'ok') return;
  try { openModel(await api('/api/new', { name: $('#newName').value.trim(), sno: +$('#newSno').value })); toast('New layout: drag controls from the toolbox'); }
  catch (err) { toast(err.message, true); }
});
window.addEventListener('keydown', ev => {
  if (ev.key === ' ' && ev.target === document.body) { spaceDown = true; ev.preventDefault(); }
  if (/INPUT|SELECT|TEXTAREA/.test(ev.target.tagName)) return;
  if (ev.ctrlKey && ev.key === 'z') { ev.preventDefault(); history('undo'); }
  else if (ev.ctrlKey && (ev.key === 'y' || (ev.shiftKey && ev.key === 'Z'))) { ev.preventDefault(); history('redo'); }
  else if (ev.ctrlKey && ev.key === 's') { ev.preventDefault(); save(false); }
  else if (ev.ctrlKey && ev.key === 'd') { ev.preventDefault(); $('#btnDup').click(); }
  else if (ev.ctrlKey && ev.key === 'g') { ev.preventDefault(); group(); }
  else if (ev.key === 'Delete') $('#btnDel').click();
  else if (ev.key === 'Escape') select([]);
  else if (ev.key === '0') { fitView(); draw(); }
  else if (sel.length && ev.key.startsWith('Arrow')) {
    const step = ev.shiftKey ? 10 : 1, d = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }[ev.key];
    ev.preventDefault();
    let ops = []; for (const f of sel) { const n = nodeOfControl(controlByFull(f)); if (n) ops = ops.concat(geometryOps(n, d[0], d[1], 0, 0)); }
    if (ops.length) send({ op: 'batch', ops });
  }
});
window.addEventListener('keyup', ev => { if (ev.key === ' ') spaceDown = false; });

// splitters: drag to resize the side panels and the outline
function splitter(el, onMove) {
  el.addEventListener('mousedown', e => {
    e.preventDefault();
    const move = m => onMove(m), up = () => { window.removeEventListener('mousemove', move); window.removeEventListener('mouseup', up); fitView(); draw(); };
    window.addEventListener('mousemove', move); window.addEventListener('mouseup', up);
  });
}
const rootStyle = document.documentElement.style;
splitter($('[data-split=left]'), m => rootStyle.setProperty('--left', Math.max(160, Math.min(500, m.clientX)) + 'px'));
splitter($('[data-split=right]'), m => rootStyle.setProperty('--right', Math.max(240, Math.min(700, window.innerWidth - m.clientX)) + 'px'));
splitter($('#treeSplit'), m => { const r = $('#right').getBoundingClientRect(); rootStyle.setProperty('--tree', Math.max(10, Math.min(80, (m.clientY - r.top - 60) / r.height * 100)) + '%'); });

if (matchMedia('(prefers-color-scheme: light)').matches) document.documentElement.dataset.theme = 'light';
(async () => {
  for (const p of await api('/api/props')) PROPS[p.id] = p;
  for (const t of await api('/api/types')) TYPES[t.type] = t;
  $('#propList').innerHTML = Object.values(PROPS).filter(p => p.name).map(p => `<option value="${esc(p.name)}">${hex(p.id)} ${esc(p.meaning ?? '')}</option>`).join('');
  resizeCanvas(); fitView(); draw(); showProps();
  await loadCatalog();
  // deep link: #stock/<Layout>[!<control full path>] or #custom/<relative path>[!<control>]
  const m = /^#(stock|custom)\/([^!]+)(?:!(.+))?$/.exec(decodeURIComponent(location.hash));
  if (m) try { openModel(await api('/api/open', { source: m[1], name: m[2] })); if (m[3]) select([m[3]]); } catch (err) { toast(err.message, true); }
})();
