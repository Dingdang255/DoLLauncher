using System.Text.Json;

namespace DoLLauncher.Services;

/// <summary>
/// 存档助手页。由内置服务器以游戏同源（同端口）提供，所以能直接读写该版本独立的 IndexedDB，
/// 也能把存档备份 POST 到启动器根目录的 backups 里，实现不下载到本地的备份。
/// </summary>
public static class SaveHelperPage
{
    public static string Build(string versionName)
    {
        var versionJson = JsonSerializer.Serialize(versionName);
        var html = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>存档助手</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 24px; min-height: 100vh;
    background: #191b20; color: #e8eaf0;
    font: 14px/1.6 "Microsoft YaHei UI", "Segoe UI", system-ui, sans-serif;
  }
  .wrap { max-width: 900px; margin: 0 auto; }
  h1 { font-size: 22px; margin: 0 0 6px; }
  p.lead { color: #9aa1ad; margin: 0 0 18px; }
  .card { background: #24272e; border: 1px solid #353a45; border-radius: 12px; padding: 18px; margin-bottom: 16px; }
  .card h2 { font-size: 15px; margin: 0 0 10px; color: #cfd5e0; }
  .row { display: flex; flex-wrap: wrap; gap: 10px; align-items: center; }
  button {
    background: #2f3542; color: #e8eaf0; border: 1px solid #3d4351; border-radius: 8px;
    padding: 9px 16px; font-size: 13px; cursor: pointer;
  }
  button:hover { opacity: .86; }
  button.primary { background: #5c9dff; border-color: #5c9dff; color: #10131a; font-weight: 600; }
  button.danger { color: #e06c6c; border-color: #6e3a3a; }
  button.mini { padding: 4px 10px; font-size: 12px; }
  .hint { color: #8d94a1; font-size: 12px; margin-top: 10px; }
  code { background: #1d2027; padding: 1px 6px; border-radius: 5px; color: #9fc2ff; }
  #log {
    margin: 0; padding: 12px; height: 220px; overflow: auto; white-space: pre-wrap;
    background: #1b1e24; border: 1px solid #333843; border-radius: 8px;
    font: 12px/1.55 Consolas, "Courier New", monospace; color: #b8c0cd;
  }
  #dbs, #slots, #lbList { font-size: 12px; color: #9aa1ad; }
  .item { display: flex; align-items: center; gap: 8px; padding: 6px 0; border-bottom: 1px solid #2d323b; }
  .item .grow { flex: 1; min-width: 0; }
  .item b { color: #dfe4ec; font-weight: 600; }
  .item span.meta { color: #8d94a1; }
  .item input[type=checkbox] { width: 16px; height: 16px; accent-color: #5c9dff; flex: none; }
  a.back { color: #7fb3ff; }
  input[type=file] { color: #b8c0cd; font-size: 12px; }
  button:disabled { opacity: .45; cursor: not-allowed; }
  .status { font-size: 12px; padding: 2px 8px; border-radius: 999px; border: 1px solid #3d4351; color: #9aa1ad; flex: none; }
  .status.over { color: #ffb36b; border-color: #6b4b2a; background: #2e2620; }
  .status.skip { color: #8d94a1; border-color: #3d4351; background: #23262c; }
  .status.new { color: #7fd18b; border-color: #2e5b37; background: #1f2a22; }
  .modes { display: flex; flex-wrap: wrap; gap: 14px; margin-top: 12px; }
  .radio { display: inline-flex; align-items: center; gap: 6px; font-size: 13px; color: #b8c0cd; cursor: pointer; }
  .radio input { accent-color: #5c9dff; }
  #importPreview { margin-top: 10px; max-height: 260px; overflow: auto; }
  .warn { color: #ffb36b; font-size: 12px; padding: 8px 0; }
</style>
</head>
<body>
<div class="wrap">
  <h1>存档助手</h1>
  <p class="lead">当前版本：<code id="vname"></code>　·　此页与游戏同源，读写的是这个版本专属的浏览器存档库。</p>

  <div class="card">
    <h2>1. 检测到的存档库</h2>
    <div id="dbs">检测中…</div>
    <div class="hint">DoL 的存档存放在 IndexedDB：库名 <code>idb</code>，表 <code>saves</code> / <code>details</code>。换版本或换浏览器 origin 后，旧存档不会自动带过来。</div>
  </div>

  <div class="card">
    <h2>2. 启动器备份（不下载到本地）</h2>
    <div class="row">
      <button class="primary" id="btnBackup">立即备份到启动器</button>
      <button id="btnReloadBackups">刷新备份列表</button>
    </div>
    <div id="lbList" style="margin-top:10px">加载中…</div>
    <div class="hint">备份会写入启动器根目录 <code>backups\版本名\saves</code>，可以直接在启动器里还原或删除。</div>
  </div>

  <div class="card">
    <h2>3. 存档槽</h2>
    <div id="slots">检测中…</div>
    <div class="row" style="margin-top:12px">
      <button id="btnSelectAll">全选</button>
      <button id="btnSelectNone">清空选择</button>
      <button class="primary" id="btnExportSlots" disabled>导出选中槽</button>
      <span id="slotSelInfo" class="hint" style="margin:0">已选 0 个</span>
    </div>
    <div class="hint">勾选多个槽可以一次导出成一个多槽文件；「导出此槽」仍然只导出单个槽。</div>
  </div>

  <div class="card">
    <h2>4. 导入存档槽（支持多槽）</h2>
    <div class="row">
      <input type="file" id="slotFile" accept=".json,application/json">
      <button id="btnReadSlotFile">读取槽文件</button>
      <button class="primary" id="btnImportSlots" disabled>开始导入</button>
    </div>
    <div class="modes" id="importModes" style="display:none">
      <label class="radio"><input type="radio" name="importMode" value="overwrite" checked> 覆盖同名槽</label>
      <label class="radio"><input type="radio" name="importMode" value="skip"> 仅导入空槽（跳过已有）</label>
      <label class="radio"><input type="radio" name="importMode" value="remap"> 导入到空槽（自动换槽）</label>
    </div>
    <div id="importPreview"></div>
    <div class="hint">支持单槽文件 <code>dol-slot/1</code> 和多槽文件 <code>dol-slots/1</code>；切换上面的策略会立即刷新每个槽的处理结果。</div>
  </div>

  <div class="card">
    <h2>5. 整库导入 / 导出</h2>
    <div class="row">
      <input type="file" id="fileInput" accept=".json,application/json">
      <button id="btnImport">导入整库到当前版本</button>
      <button class="primary" id="btnExport">导出整库到文件</button>
    </div>
    <div class="hint">整库文件 <code>dol-saves/1</code> 包含当前版本的全部存档槽与存档详情；导入时先清空对应表再整体写入，等于把存档库还原成文件里的状态。与上面按槽操作的区别：第 3、4 节只搬选中的槽，并能选择覆盖 / 仅空槽 / 自动换槽；这里是一次性备份或还原整个存档库。导入前请关闭其它游戏标签页，导入完成后刷新游戏页面。</div>
  </div>

  <div class="card">
    <h2>运行记录</h2>
    <pre id="log"></pre>
    <p class="hint"><a class="back" href="/">← 返回游戏</a></p>
  </div>
</div>

<script>
const VERSION = __VERSION_JSON__;
const logEl = document.getElementById('log');
const dbsEl = document.getElementById('dbs');
const slotsEl = document.getElementById('slots');
const lbEl = document.getElementById('lbList');
const fileInput = document.getElementById('fileInput');
const slotFile = document.getElementById('slotFile');
const slotSelInfo = document.getElementById('slotSelInfo');
const btnExportSlots = document.getElementById('btnExportSlots');
const btnImportSlots = document.getElementById('btnImportSlots');
const importModes = document.getElementById('importModes');
const importPreview = document.getElementById('importPreview');
let SLOT_ITEMS = [];
let importState = null;
document.getElementById('vname').textContent = VERSION || '未知';

function log(msg) {
  logEl.textContent += msg + '\n';
  logEl.scrollTop = logEl.scrollHeight;
}

const DEFAULT_STORES = [
  { name: 'saves', keyPath: 'slot' },
  { name: 'details', keyPath: 'slot' }
];

function openDb(name, version, stores) {
  return new Promise((resolve, reject) => {
    const req = version ? indexedDB.open(name, version) : indexedDB.open(name);
    req.onupgradeneeded = () => {
      const db = req.result;
      for (const st of (stores || DEFAULT_STORES)) {
        if (!db.objectStoreNames.contains(st.name)) {
          db.createObjectStore(st.name, { keyPath: st.keyPath === undefined ? 'slot' : st.keyPath });
        }
      }
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
    req.onblocked = () => reject(new Error('数据库被其它页面占用，请先关闭游戏标签页'));
  });
}

function reqAll(store) {
  return new Promise((resolve, reject) => {
    const r = store.getAll();
    r.onsuccess = () => resolve(r.result);
    r.onerror = () => reject(r.error);
  });
}

function txDone(tx) {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
    tx.onabort = () => reject(tx.error);
  });
}

async function listDbs() {
  try {
    const arr = await indexedDB.databases();
    return arr.map(d => ({ name: d.name, version: d.version })).filter(d => !!d.name);
  } catch (e) {
    return [{ name: 'idb', version: undefined }];
  }
}

async function refreshDbs() {
  dbsEl.textContent = '检测中…';
  const dbs = await listDbs();
  if (dbs.length === 0) { dbsEl.textContent = '还没有任何数据库（这个版本可能从未打开过游戏）。'; return; }
  const parts = [];
  for (const d of dbs) {
    try {
      const db = await openDb(d.name, d.version);
      const stores = [...db.objectStoreNames].map(s => s);
      const counts = [];
      for (const s of stores) {
        const recs = await reqAll(db.transaction(s, 'readonly').objectStore(s));
        counts.push(s + '(' + recs.length + ')');
      }
      db.close();
      parts.push('<code>' + d.name + '</code> v' + d.version + ' → ' + (counts.length ? counts.join('，') : '空'));
    } catch (e) {
      parts.push('<code>' + d.name + '</code>（无法打开：' + e.message + '）');
    }
  }
  dbsEl.innerHTML = parts.join('<br>');
}

function replacer(key, value) {
  if (value instanceof ArrayBuffer) {
    return { __t: 'ab', d: Array.from(new Uint8Array(value)) };
  }
  if (value && ArrayBuffer.isView(value)) {
    return { __t: 'ta', d: Array.from(new Uint8Array(value.buffer)) };
  }
  return value;
}

function reviver(key, value) {
  if (value && typeof value === 'object' && value.__t === 'ab' && Array.isArray(value.d)) {
    return new Uint8Array(value.d).buffer;
  }
  if (value && typeof value === 'object' && value.__t === 'ta' && Array.isArray(value.d)) {
    return new Uint8Array(value.d);
  }
  return value;
}

function stamp() {
  const d = new Date();
  const p = n => String(n).padStart(2, '0');
  return d.getFullYear() + p(d.getMonth() + 1) + p(d.getDate()) + '-' + p(d.getHours()) + p(d.getMinutes()) + p(d.getSeconds());
}

function download(text, name) {
  const blob = new Blob([text], { type: 'application/json' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 2000);
}

async function collectPayload() {
  const dbs = await listDbs();
  if (dbs.length === 0) return null;
  const out = [];
  for (const d of dbs) {
    let db;
    try {
      db = await openDb(d.name, d.version);
    } catch (e) {
      log('跳过 ' + d.name + '：' + e.message);
      continue;
    }
    const names = [...db.objectStoreNames];
    if (names.length === 0) { db.close(); continue; }
    const stores = [];
    for (const sn of names) {
      const os = db.transaction(sn, 'readonly').objectStore(sn);
      const recs = await reqAll(os);
      stores.push({
        name: sn,
        keyPath: os.keyPath,
        autoIncrement: os.autoIncrement,
        records: recs.map(v => ({ value: v }))
      });
    }
    out.push({ name: db.name, version: db.version, stores });
    db.close();
  }
  return { format: 'dol-saves/1', exportedAt: new Date().toISOString(), version: VERSION, dbs: out };
}

async function exportAll() {
  const payload = await collectPayload();
  if (!payload || payload.dbs.length === 0) { log('没有可导出的数据库。'); return; }
  download(JSON.stringify(payload, replacer, 1), 'dol-saves-' + (VERSION || 'unknown') + '-' + stamp() + '.json');
  log('导出完成，共 ' + payload.dbs.length + ' 个数据库。');
}

async function backupToLauncher(auto) {
  const payload = await collectPayload();
  if (!payload || payload.dbs.length === 0) { log('没有可备份的数据库。'); return null; }
  const text = JSON.stringify(payload, replacer, 1);
  const name = (auto ? 'auto-saves-' : 'saves-') + stamp() + '.json';
  const url = '/__launcher/api/backup?file=' + encodeURIComponent(name) + '&auto=' + (auto ? '1' : '0');
  const res = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'text/plain;charset=UTF-8' }, body: text });
  if (!res.ok) throw new Error('服务器返回 ' + res.status);
  const data = await res.json();
  log((auto ? '自动' : '') + '备份完成：' + data.name);
  return data.name;
}

async function importPayload(payload, sourceName) {
  if (!payload || payload.format !== 'dol-saves/1' || !Array.isArray(payload.dbs)) {
    log('这不是存档助手导出的文件。');
    return;
  }
  log('开始导入 ' + sourceName + '（来源版本 ' + (payload.version || '未知') + '）');
  for (const d of payload.dbs) {
    try {
      const stores = d.stores.map(s => ({ name: s.name, keyPath: s.keyPath === undefined ? 'slot' : s.keyPath }));
      const db = await openDb(d.name, d.version, stores);
      const names = [...db.objectStoreNames];
      if (names.length === 0) { log('跳过空的数据库 ' + d.name); db.close(); continue; }
      const tx = db.transaction(names, 'readwrite');
      let count = 0;
      for (const s of d.stores) {
        if (!names.includes(s.name)) { log('跳过不存在的表 ' + s.name); continue; }
        const os = tx.objectStore(s.name);
        os.clear();
        for (const r of s.records) {
          try {
            if (os.keyPath != null) os.put(r.value);
            else os.put(r.value, r.key);
            count++;
          } catch (e) {
            log('写入 ' + s.name + ' 失败：' + e.message);
          }
        }
      }
      await txDone(tx);
      db.close();
      log('已写入 ' + d.name + '：' + count + ' 条');
    } catch (e) {
      log('导入 ' + d.name + ' 失败：' + e.message);
    }
  }
  log('导入完成。请刷新游戏页面，再到游戏里「载入存档」查看。');
  refreshDbs();
  loadSlots();
}

async function importAll() {
  const file = fileInput.files && fileInput.files[0];
  if (!file) { log('请先选择一个 .json 存档文件。'); return; }
  let payload;
  try { payload = JSON.parse(await file.text(), reviver); }
  catch (e) { log('读取失败：' + e.message); return; }
  await importPayload(payload, file.name);
}

function slotName(detail, slot) {
  if (!detail || typeof detail !== 'object') return '存档 ' + slot;
  const candidates = [detail.name, detail.saveName, detail.title, detail.desc, detail.description];
  for (const c of candidates) {
    if (typeof c === 'string' && c.trim().length > 0) return c.trim();
  }
  return '存档 ' + slot;
}

function slotTimeOf(info) {
  if (!info || typeof info !== 'object') return '';
  const v = info.time || info.saveTime || info.date;
  return v ? String(v) : '';
}

function pickedSlotIndexes() {
  return [...slotsEl.querySelectorAll('input.slotCheck:checked')].map(el => Number(el.getAttribute('data-idx')));
}

function updateSlotSelection() {
  const picked = pickedSlotIndexes().length;
  slotSelInfo.textContent = '已选 ' + picked + ' 个 · 共 ' + SLOT_ITEMS.length + ' 个';
  btnExportSlots.disabled = picked === 0;
}

async function loadSlots() {
  slotsEl.textContent = '检测中…';
  SLOT_ITEMS = [];
  const dbs = await listDbs();
  if (dbs.length === 0) { slotsEl.textContent = '还没有存档库。'; updateSlotSelection(); return; }
  const multiDb = dbs.length > 1;
  const parts = [];
  for (const d of dbs) {
    let db;
    try { db = await openDb(d.name, d.version); }
    catch (e) {
      parts.push('<div class="item"><span class="meta">无法打开 ' + escapeHtml(d.name) + '：' + escapeHtml(e.message) + '</span></div>');
      continue;
    }

    const saves = db.objectStoreNames.contains('saves')
      ? await reqAll(db.transaction('saves', 'readonly').objectStore('saves')) : [];
    const details = db.objectStoreNames.contains('details')
      ? await reqAll(db.transaction('details', 'readonly').objectStore('details')) : [];
    const detailMap = new Map(details.map(x => [String(x.slot), x]));
    saves.sort((a, b) => {
      const x = Number(a.slot), y = Number(b.slot);
      if (!Number.isNaN(x) && !Number.isNaN(y)) return x - y;
      return String(a.slot).localeCompare(String(b.slot), 'zh-CN');
    });

    if (saves.length === 0) {
      parts.push('<div class="item"><span class="grow"><b>' + escapeHtml(d.name) + '</b> ' +
        '<span class="meta">没有存档</span></span></div>');
    } else {
      for (const rec of saves) {
        const slot = rec.slot;
        const idx = SLOT_ITEMS.length;
        const info = detailMap.get(String(slot)) || null;
        SLOT_ITEMS.push({ db: d.name, dbVersion: d.version, slot: slot, save: rec, detail: info });
        const title = slotName(info, slot);
        const time = slotTimeOf(info);
        const meta = 'slot=' + slot + (multiDb ? ' · ' + d.name : '') + (time ? ' · ' + time : '');
        parts.push('<div class="item">' +
          '<input type="checkbox" class="slotCheck" data-idx="' + idx + '">' +
          '<span class="grow"><b>' + escapeHtml(title) + '</b> ' +
          '<span class="meta">' + escapeHtml(meta) + '</span></span>' +
          '<button class="mini" data-export="' + escapeHtml(d.name) + '|' + escapeHtml(String(slot)) + '">导出此槽</button>' +
          '<button class="mini danger" data-del="' + escapeHtml(d.name) + '|' + escapeHtml(String(slot)) + '">删除此槽</button></div>');
      }
    }
    db.close();
  }
  slotsEl.innerHTML = parts.join('');
  updateSlotSelection();
}

async function exportSelectedSlots() {
  const picked = pickedSlotIndexes();
  if (picked.length === 0) { log('请先勾选至少一个存档槽。'); return; }
  const entries = picked.map(i => {
    const item = SLOT_ITEMS[i];
    if (!item) return null;
    return {
      db: item.db,
      slot: item.slot,
      name: slotName(item.detail, item.slot),
      time: slotTimeOf(item.detail),
      saves: item.save || null,
      details: item.detail || null
    };
  }).filter(Boolean);
  if (entries.length === 0) { log('没有可导出的存档槽。'); return; }
  const payload = {
    format: 'dol-slots/1',
    exportedAt: new Date().toISOString(),
    version: VERSION,
    sourceDb: entries[0].db,
    slots: entries
  };
  const tag = entries.length === 1 ? 'slot1' : 'slots' + entries.length;
  download(JSON.stringify(payload, replacer, 1),
    'dol-' + tag + '-' + safeName(VERSION || 'unknown') + '-' + stamp() + '.json');
  log('已导出 ' + entries.length + ' 个存档槽：' + entries.map(e => e.slot).join('、'));
}

function safeName(text) {
  return String(text).replace(/[\\/:*?"<>|\s]+/g, '_');
}

function escapeHtml(text) {
  return String(text).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
}

async function exportSlot(dbName, slot) {
  const dbs = await listDbs();
  const d = dbs.find(x => x.name === dbName) || { name: dbName };
  const db = await openDb(d.name, d.version);
  const saves = db.objectStoreNames.contains('saves')
    ? (await reqAll(db.transaction('saves', 'readonly').objectStore('saves'))).filter(r => String(r.slot) === slot) : [];
  const details = db.objectStoreNames.contains('details')
    ? (await reqAll(db.transaction('details', 'readonly').objectStore('details'))).filter(r => String(r.slot) === slot) : [];
  db.close();
  const payload = {
    format: 'dol-slot/1', exportedAt: new Date().toISOString(), version: VERSION, db: d.name,
    saves: saves, details: details
  };
  download(JSON.stringify(payload, replacer, 1), 'dol-slot-' + slot + '-' + stamp() + '.json');
  log('已导出存档槽 ' + slot);
}

async function deleteSlot(dbName, slot) {
  const dbs = await listDbs();
  const d = dbs.find(x => x.name === dbName) || { name: dbName };
  const db = await openDb(d.name, d.version);
  const names = [...db.objectStoreNames].filter(n => n === 'saves' || n === 'details');
  const tx = db.transaction(names, 'readwrite');
  for (const n of names) tx.objectStore(n).delete(Number.isNaN(Number(slot)) ? slot : Number(slot));
  await txDone(tx);
  db.close();
  log('已删除存档槽 ' + slot);
  loadSlots(); refreshDbs();
}

function firstRecord(value) {
  if (Array.isArray(value)) return value.length ? value[0] : null;
  if (value && typeof value === 'object') return value;
  return null;
}

function readSlotEntries(payload) {
  if (!payload) return null;
  if (payload.format === 'dol-slots/1' && Array.isArray(payload.slots)) {
    const entries = payload.slots.map(item => {
      if (!item || typeof item !== 'object') return null;
      const saveRec = firstRecord(item.saves);
      const detailRec = firstRecord(item.details);
      let slot = item.slot;
      if (slot === undefined || slot === null) slot = saveRec ? saveRec.slot : detailRec ? detailRec.slot : null;
      if (slot === undefined || slot === null) return null;
      return {
        db: item.db || payload.sourceDb || 'idb',
        slot: slot,
        name: item.name || slotName(detailRec, slot),
        time: item.time || slotTimeOf(detailRec),
        saves: saveRec,
        details: detailRec
      };
    }).filter(Boolean);
    return { db: payload.sourceDb || (entries[0] && entries[0].db) || 'idb', entries: entries };
  }
  if (payload.format === 'dol-slot/1') {
    const saveRec = firstRecord(payload.saves);
    const detailRec = firstRecord(payload.details);
    const slot = saveRec ? saveRec.slot : detailRec ? detailRec.slot : null;
    if (slot === undefined || slot === null) return { db: payload.db || 'idb', entries: [] };
    return {
      db: payload.db || 'idb',
      entries: [{
        db: payload.db || 'idb',
        slot: slot,
        name: slotName(detailRec, slot),
        time: slotTimeOf(detailRec),
        saves: saveRec,
        details: detailRec
      }]
    };
  }
  return null;
}

async function readImportFile() {
  const file = slotFile.files && slotFile.files[0];
  if (!file) { log('请先选择一个槽文件。'); return; }
  let payload;
  try { payload = JSON.parse(await file.text(), reviver); }
  catch (e) { log('读取失败：' + e.message); return; }

  importState = null;
  btnImportSlots.disabled = true;
  importModes.style.display = 'none';
  if (payload && payload.format === 'dol-saves/1') {
    importPreview.innerHTML = '<div class="warn">这是整库备份文件，请用第 5 节的「导入整库到当前版本」。</div>';
    return;
  }
  const parsed = readSlotEntries(payload);
  if (!parsed) {
    importPreview.innerHTML = '<div class="warn">无法识别这个文件（需要 dol-slot/1 或 dol-slots/1 格式）。</div>';
    return;
  }
  if (parsed.entries.length === 0) {
    importPreview.innerHTML = '<div class="warn">文件里没有可识别的存档槽。</div>';
    return;
  }
  importState = { fileName: file.name, sourceVersion: payload.version || '', entries: parsed.entries };
  importModes.style.display = 'flex';
  btnImportSlots.disabled = false;
  await renderImportPreview();
  log('已读取 ' + file.name + '，共 ' + parsed.entries.length + ' 个槽。');
}

function currentImportMode() {
  const el = document.querySelector('input[name=importMode]:checked');
  return el ? el.value : 'overwrite';
}

async function existingSlotsFor(dbName) {
  const dbs = await listDbs();
  const d = dbs.find(x => x.name === dbName) || { name: dbName, version: undefined };
  const db = await openDb(d.name, d.version);
  let set = new Set();
  if (db.objectStoreNames.contains('saves')) {
    const recs = await reqAll(db.transaction('saves', 'readonly').objectStore('saves'));
    set = new Set(recs.map(r => String(r.slot)));
  }
  db.close();
  return set;
}

function buildImportPlan(entries, existingByDb, mode) {
  const usedByDb = new Map();
  const plan = [];
  for (const entry of entries) {
    const db = entry.db || 'idb';
    const existing = existingByDb.get(db) || new Set();
    if (!usedByDb.has(db)) usedByDb.set(db, new Set(existing));
    const used = usedByDb.get(db);
    const source = String(entry.slot);

    if (mode === 'skip' && used.has(source)) {
      plan.push({ entry: entry, target: null, action: 'skip' });
      continue;
    }

    let target = source;
    if (mode === 'remap' && used.has(source)) {
      let n = 1;
      while (used.has(String(n))) n++;
      target = String(n);
    }
    used.add(target);
    let action;
    if (mode === 'overwrite') action = existing.has(source) ? 'over' : 'new';
    else if (mode === 'skip') action = 'new';
    else action = target !== source ? 'remap' : (existing.has(source) ? 'over' : 'new');
    plan.push({ entry: entry, target: target, action: action });
  }
  return plan;
}

const IMPORT_ACTION_LABELS = {
  over: ['覆盖', 'over'],
  skip: ['跳过（槽已占用）', 'skip'],
  new: ['写入', 'new'],
  remap: ['换到空槽', 'new']
};

async function renderImportPreview() {
  if (!importState) return;
  importPreview.textContent = '分析中…';
  const mode = currentImportMode();
  const existingByDb = new Map();
  for (const db of new Set(importState.entries.map(e => e.db || 'idb'))) {
    existingByDb.set(db, await existingSlotsFor(db));
  }
  const plan = buildImportPlan(importState.entries, existingByDb, mode);
  const rows = plan.map(p => {
    const label = IMPORT_ACTION_LABELS[p.action] || [p.action, ''];
    const target = p.target === null ? '保持原样' : '目标 slot=' + p.target;
    return '<div class="item"><span class="grow"><b>' + escapeHtml(p.entry.name || ('存档 ' + p.entry.slot)) + '</b> ' +
      '<span class="meta">源 slot=' + escapeHtml(String(p.entry.slot)) + ' · ' + escapeHtml(target) +
      (p.entry.time ? ' · ' + escapeHtml(p.entry.time) : '') + '</span></span>' +
      '<span class="status ' + label[1] + '">' + label[0] + '</span></div>';
  });
  const counts = {};
  for (const p of plan) counts[p.action] = (counts[p.action] || 0) + 1;
  const summary = Object.keys(counts).map(k => {
    const label = IMPORT_ACTION_LABELS[k] || [k];
    return label[0] + ' ' + counts[k];
  }).join(' · ');
  importPreview.innerHTML = '<div class="hint" style="margin:0 0 6px">' +
    escapeHtml(importState.fileName) + ' · 来源版本 ' + escapeHtml(importState.sourceVersion || '未知') +
    ' · ' + escapeHtml(summary) + '</div>' + rows.join('');
}

function castSlotValue(original, target) {
  if (typeof original === 'number') return Number(target);
  if (typeof original === 'string' && /^\d+$/.test(original)) return Number(target);
  return target;
}

async function runSlotImport() {
  if (!importState) { log('请先读取槽文件。'); return; }
  const mode = currentImportMode();
  const existingByDb = new Map();
  for (const db of new Set(importState.entries.map(e => e.db || 'idb'))) {
    existingByDb.set(db, await existingSlotsFor(db));
  }
  const plan = buildImportPlan(importState.entries, existingByDb, mode);
  const groups = new Map();
  for (const p of plan) {
    if (p.action === 'skip') continue;
    const db = p.entry.db || 'idb';
    if (!groups.has(db)) groups.set(db, []);
    groups.get(db).push(p);
  }

  let written = 0;
  let skipped = plan.filter(p => p.action === 'skip').length;
  let failed = 0;
  for (const [dbName, list] of groups) {
    let db;
    try { db = await openDb(dbName, undefined); }
    catch (e) { log('打开 ' + dbName + ' 失败：' + e.message); failed += list.length; continue; }
    const names = [...db.objectStoreNames].filter(n => n === 'saves' || n === 'details');
    if (names.length === 0) {
      log(dbName + ' 里没有存档表，跳过。');
      failed += list.length;
      db.close();
      continue;
    }
    const tx = db.transaction(names, 'readwrite');
    const savesStore = names.includes('saves') ? tx.objectStore('saves') : null;
    const detailsStore = names.includes('details') ? tx.objectStore('details') : null;
    for (const p of list) {
      const entry = p.entry;
      const remapped = String(entry.slot) !== p.target;
      try {
        if (savesStore && entry.saves) {
          const rec = Object.assign({}, entry.saves);
          if (remapped) rec.slot = castSlotValue(entry.slot, p.target);
          savesStore.put(rec);
        }
        if (detailsStore && entry.details) {
          const rec = Object.assign({}, entry.details);
          if (remapped) rec.slot = castSlotValue(entry.slot, p.target);
          detailsStore.put(rec);
        }
        written++;
      } catch (e) {
        failed++;
        log('写入槽 ' + entry.slot + ' 失败：' + e.message);
      }
    }
    await txDone(tx);
    db.close();
  }

  log('导入完成：写入 ' + written + ' 个槽' +
    (skipped ? '，跳过 ' + skipped + ' 个已有槽' : '') +
    (failed ? '，失败 ' + failed + ' 个' : '') + '。请刷新游戏页面。');
  importState = null;
  importPreview.textContent = '';
  importModes.style.display = 'none';
  btnImportSlots.disabled = true;
  slotFile.value = '';
  loadSlots(); refreshDbs();
}

async function loadLauncherBackups() {
  lbEl.textContent = '加载中…';
  try {
    const res = await fetch('/__launcher/api/backup-list');
    const data = await res.json();
    if (!data.saves || data.saves.length === 0) { lbEl.textContent = '还没有启动器备份。'; return; }
    lbEl.innerHTML = data.saves.map(s =>
      '<div class="item"><span class="grow"><b>' + escapeHtml(s.name) + '</b> ' +
      '<span class="meta">' + escapeHtml(s.time) + ' · ' + escapeHtml(s.size) + '</span></span>' +
      '<button class="mini" data-restore="' + escapeHtml(s.name) + '">还原</button>' +
      '<button class="mini danger" data-lbdel="' + escapeHtml(s.name) + '">删除</button></div>'
    ).join('');
  } catch (e) {
    lbEl.textContent = '读取备份列表失败：' + e.message;
  }
}

async function restoreLauncher(name) {
  try {
    const res = await fetch('/__launcher/api/backup?file=' + encodeURIComponent(name));
    if (!res.ok) throw new Error('服务器返回 ' + res.status);
    const payload = JSON.parse(await res.text(), reviver);
    await importPayload(payload, name);
  } catch (e) {
    log('还原失败：' + e.message);
  }
}

async function deleteLauncher(name) {
  try {
    const res = await fetch('/__launcher/api/backup-delete?file=' + encodeURIComponent(name), { method: 'POST', body: '' });
    const data = await res.json();
    log(data.ok ? '已删除备份 ' + name : '删除失败');
    loadLauncherBackups();
  } catch (e) {
    log('删除失败：' + e.message);
  }
}

slotsEl.addEventListener('click', e => {
  const t = e.target;
  if (!t || t.tagName !== 'BUTTON') return;
  const exp = t.getAttribute('data-export');
  const del = t.getAttribute('data-del');
  if (exp) { const [db, slot] = exp.split('|'); exportSlot(db, slot).catch(x => log(x.message)); }
  if (del) { const [db, slot] = del.split('|'); deleteSlot(db, slot).catch(x => log(x.message)); }
});

slotsEl.addEventListener('change', e => {
  if (e.target && e.target.classList && e.target.classList.contains('slotCheck')) updateSlotSelection();
});

document.querySelectorAll('input[name=importMode]').forEach(el => {
  el.addEventListener('change', () => renderImportPreview().catch(x => log(x.message)));
});

lbEl.addEventListener('click', e => {
  const t = e.target;
  if (!t || t.tagName !== 'BUTTON') return;
  const restore = t.getAttribute('data-restore');
  const del = t.getAttribute('data-lbdel');
  if (restore) restoreLauncher(restore);
  if (del) deleteLauncher(del);
});

document.getElementById('btnExport').addEventListener('click', () => exportAll().catch(e => log(e.message)));
document.getElementById('btnBackup').addEventListener('click', () => backupToLauncher(false).then(loadLauncherBackups).catch(e => log(e.message)));
document.getElementById('btnReloadBackups').addEventListener('click', () => loadLauncherBackups());
document.getElementById('btnImport').addEventListener('click', () => importAll().catch(e => log(e.message)));
document.getElementById('btnReadSlotFile').addEventListener('click', () => readImportFile().catch(e => log(e.message)));
document.getElementById('btnImportSlots').addEventListener('click', () => runSlotImport().catch(e => log(e.message)));
document.getElementById('btnSelectAll').addEventListener('click', () => {
  slotsEl.querySelectorAll('input.slotCheck').forEach(el => { el.checked = true; });
  updateSlotSelection();
});
document.getElementById('btnSelectNone').addEventListener('click', () => {
  slotsEl.querySelectorAll('input.slotCheck').forEach(el => { el.checked = false; });
  updateSlotSelection();
});
btnExportSlots.addEventListener('click', () => exportSelectedSlots().catch(e => log(e.message)));

refreshDbs().catch(e => log(e.message));
loadSlots().catch(e => log(e.message));
loadLauncherBackups();

// 启动器自动备份：?auto=1 时静默备份并尝试关闭页面
const auto = new URLSearchParams(location.search).get('auto') === '1';
if (auto) {
  backupToLauncher(true).then(() => {
    log('自动备份完成，本页面可以关闭。');
    setTimeout(() => { try { window.close(); } catch (e) { } }, 1200);
  }).catch(e => log('自动备份失败：' + e.message));
}
</script>
</body>
</html>
""";
        return html.Replace("__VERSION_JSON__", versionJson);
    }
}
