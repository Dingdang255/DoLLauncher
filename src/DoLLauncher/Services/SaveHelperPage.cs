using System.Text.Json;

namespace DoLLauncher.Services;

/// <summary>
/// 存档助手页。由内置服务器以游戏同源（同端口）提供，所以能直接读写该版本独立的 IndexedDB，
/// 也能把存档备份 POST 到启动器根目录的 backups 里，实现不下载到本地的备份。
/// </summary>
public static class SaveHelperPage
{
    /// <summary>
    /// 构建存档助手页；autoOnly 为 true 时只显示备份提示，不加载可编辑界面。
    /// </summary>
    public static string Build(string versionName, bool autoOnly = false)
    {
        var versionJson = JsonSerializer.Serialize(versionName);
        var html = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>__PAGE_TITLE__</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  [hidden] { display: none !important; }
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
  .item.dragging { opacity: .45; }
  .item.dropTarget { background: #2b3340; box-shadow: inset 0 0 0 1px #5c9dff; border-radius: 6px; }
  .item .handle { color: #6f7683; cursor: grab; flex: none; user-select: none; }
  .editor { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; padding: 8px 0; border-bottom: 1px solid #2d323b; }
  .editor input { background: #1d2027; color: #e8eaf0; border: 1px solid #3d4351; border-radius: 6px; padding: 5px 8px; font-size: 12px; min-width: 180px; }
  #varSearch { background: #1d2027; color: #e8eaf0; border: 1px solid #3d4351; border-radius: 8px; padding: 8px 10px; font-size: 13px; flex: 1; min-width: 160px; }
  #varList { max-height: 300px; overflow: auto; margin-top: 10px; font-size: 12px; }
  #varList .vrow { display: flex; gap: 8px; padding: 4px 0; border-bottom: 1px solid #2d323b; cursor: pointer; }
  #varList .vrow:hover { background: #2b3038; }
  #varList .vname { color: #9fc2ff; flex: none; min-width: 160px; }
  #varList .vval { color: #8d94a1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  #varValue { width: 100%; margin-top: 8px; background: #1b1e24; color: #d5dbe6; border: 1px solid #333843; border-radius: 8px; padding: 10px; font: 12px/1.5 Consolas, monospace; }
  #varPath { color: #9fc2ff; font: 12px Consolas, monospace; }
  #autoWrap { max-width: 420px; margin: 12vh auto 0; text-align: center; }
  #autoWrap h1 { font-size: 18px; margin: 0 0 10px; }
  #autoWrap #autoMsg { color: #cfd5e0; margin: 0 0 8px; }
  #autoWrap #autoDetail { color: #8d94a1; font-size: 12px; margin: 0; }
  .spin {
    width: 26px; height: 26px; margin: 0 auto 14px; border-radius: 50%;
    border: 3px solid #3d4351; border-top-color: #5c9dff; animation: spin .9s linear infinite;
  }
  @keyframes spin { to { transform: rotate(360deg); } }
</style>
</head>
<body>
<div class="wrap" id="autoWrap"__AUTO_HIDDEN__>
  <div class="spin" id="autoSpin"></div>
  <h1>存档助手</h1>
  <p id="autoMsg">正在备份你的存档，请稍候…</p>
  <p id="autoDetail">备份完成后此页面会自动关闭。</p>
</div>

<div class="wrap" id="mainWrap"__MAIN_HIDDEN__>
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
      <button id="btnVarsSlots" disabled>编辑变量</button>
      <button class="danger" id="btnDeleteSlots" disabled>删除选中槽</button>
      <span id="slotSelInfo" class="hint" style="margin:0">已选 0 个</span>
    </div>
    <div class="editor" id="slotInfoEditor" hidden>
      <span class="meta">名称</span><input id="bulkName" placeholder="自定义存档名">
      <span class="meta">标题</span><input id="bulkTitle" placeholder="存档标题">
      <button class="mini primary" id="bulkApply">应用修改</button>
      <button class="mini" id="bulkCancel">取消</button>
      <span class="meta" id="bulkInfo"></span>
    </div>
    <div class="hint">选中 1 个槽时可直接改名称 / 标题；导出、删除支持多选，编辑变量只支持单选；选中多个时只保留导出与删除可用。拖动某一槽放到另一槽上，可把两者槽位互换。修改前建议先备份。</div>
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

  <div class="card" id="varCard" style="display:none">
    <h2>6. 变量编辑 <span id="varSlotInfo" class="hint" style="margin:0"></span></h2>
    <div class="row">
      <input id="varSearch" placeholder="搜索变量名…">
      <button id="varClose">关闭</button>
      <button class="mini" id="varReload">重新读取</button>
    </div>
    <div id="varList"></div>
    <div id="varEditor" style="display:none">
      <div id="varPath"></div>
      <textarea id="varValue" rows="10" spellcheck="false"></textarea>
      <div class="row" style="margin-top:8px">
        <button class="primary" id="varSave">保存修改</button>
        <button id="varCancel">取消</button>
      </div>
      <div class="hint">内容按 JSON 解析后写回当前选中槽；字符串请用双引号包裹，例如 <code>"abc"</code>。修改后需刷新游戏页面再载入存档。</div>
    </div>
  </div>

  <div class="card">
    <h2>运行记录</h2>
    <pre id="log"></pre>
    <p class="hint"><a class="back" href="/">← 返回游戏</a></p>
  </div>
</div>

<script>
const VERSION = __VERSION_JSON__;
const AUTO_ONLY = __AUTO_ONLY__;
const logEl = document.getElementById('log');
const dbsEl = document.getElementById('dbs');
const slotsEl = document.getElementById('slots');
const lbEl = document.getElementById('lbList');
const fileInput = document.getElementById('fileInput');
const slotFile = document.getElementById('slotFile');
const slotSelInfo = document.getElementById('slotSelInfo');
const btnExportSlots = document.getElementById('btnExportSlots');
const btnDeleteSlots = document.getElementById('btnDeleteSlots');
const btnVarsSlots = document.getElementById('btnVarsSlots');
const slotInfoEditor = document.getElementById('slotInfoEditor');
const bulkName = document.getElementById('bulkName');
const bulkTitle = document.getElementById('bulkTitle');
const bulkInfo = document.getElementById('bulkInfo');
const bulkApply = document.getElementById('bulkApply');
const btnImportSlots = document.getElementById('btnImportSlots');
const importModes = document.getElementById('importModes');
const importPreview = document.getElementById('importPreview');
let SLOT_ITEMS = [];
// 名称 / 标题编辑的基准值，内容与基准一致时「应用修改」不可用
let slotInfoBaseline = { name: '', title: '' };
let importState = null;
let VAR_STATE = null;
const varCard = document.getElementById('varCard');
const varList = document.getElementById('varList');
const varEditor = document.getElementById('varEditor');
const varSearch = document.getElementById('varSearch');
const varValue = document.getElementById('varValue');
const varPath = document.getElementById('varPath');
const varSlotInfo = document.getElementById('varSlotInfo');
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

function reqCursor(store) {
  return new Promise((resolve, reject) => {
    const out = [];
    const r = store.openCursor();
    r.onsuccess = () => {
      const c = r.result;
      if (!c) { resolve(out); return; }
      out.push({ key: c.primaryKey, value: c.value });
      c.continue();
    };
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
      const keyPath = os.keyPath;
      const autoIncrement = os.autoIncrement;
      const pairs = await reqCursor(os);
      stores.push({
        name: sn,
        keyPath: keyPath,
        autoIncrement: autoIncrement,
        records: pairs.map(p => keyPath == null ? { key: p.key, value: p.value } : { value: p.value })
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
      let staleExport = false;
      for (const s of d.stores) {
        if (!names.includes(s.name)) { log('跳过不存在的表 ' + s.name); continue; }
        const os = tx.objectStore(s.name);
        const recs = Array.isArray(s.records) ? s.records : [];
        const needKey = os.keyPath == null && !os.autoIncrement;
        if (needKey && recs.some(r => r.key === undefined || r.key === null)) {
          staleExport = true;
          log('跳过 ' + s.name + '：导出文件缺少主键（旧版导出未记录），已保留原数据');
          continue;
        }
        os.clear();
        let failed = 0;
        for (const r of recs) {
          try {
            if (os.keyPath != null) os.put(r.value);
            else if (os.autoIncrement && (r.key === undefined || r.key === null)) os.put(r.value);
            else os.put(r.value, r.key);
            count++;
          } catch (e) {
            failed++;
            if (failed <= 3) log('写入 ' + s.name + ' 失败：' + e.message);
          }
        }
        if (failed > 3) log('写入 ' + s.name + ' 另有 ' + (failed - 3) + ' 条失败，共失败 ' + failed + ' 条');
      }
      await txDone(tx);
      db.close();
      if (staleExport) log('提示：部分表因文件缺少主键未能写入，请用当前版本的存档助手重新导出后再导入。');
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

function slotBody(rec) {
  if (!rec || typeof rec !== 'object') return {};
  if (rec.data && typeof rec.data === 'object') return rec.data;
  return rec;
}

function slotName(detail, slot) {
  const body = slotBody(detail);
  const meta = (body.metadata && typeof body.metadata === 'object') ? body.metadata : {};
  const candidates = [meta.saveName, body.name, body.saveName, body.title, body.desc, body.description, detail && detail.name];
  for (const c of candidates) {
    if (typeof c === 'string' && c.trim().length > 0) return c.trim();
  }
  return '存档 ' + slot;
}

function slotTimeOf(info) {
  const body = slotBody(info);
  const v = body.date || body.time || body.saveTime;
  if (v === undefined || v === null || v === '') return '';
  if (typeof v === 'number') {
    const d = new Date(v);
    return Number.isNaN(d.getTime()) ? String(v) : d.toLocaleString();
  }
  return String(v);
}

function pickedSlotIndexes() {
  return [...slotsEl.querySelectorAll('input.slotCheck:checked')].map(el => Number(el.getAttribute('data-idx')));
}

function pickedItems() {
  return pickedSlotIndexes().map(i => SLOT_ITEMS[i]).filter(Boolean);
}

function slotInfoValues(item) {
  const body = slotBody(item && item.detail);
  const meta = (body.metadata && typeof body.metadata === 'object') ? body.metadata : {};
  return {
    saveName: typeof meta.saveName === 'string' ? meta.saveName : '',
    title: typeof body.title === 'string' ? body.title : ''
  };
}

function updateBulkApplyState() {
  const same = bulkName.value.trim() === slotInfoBaseline.name &&
    bulkTitle.value.trim() === slotInfoBaseline.title;
  bulkApply.disabled = same;
}

function updateSlotSelection() {
  const picked = pickedSlotIndexes().length;
  slotSelInfo.textContent = '已选 ' + picked + ' 个 · 共 ' + SLOT_ITEMS.length + ' 个';
  const none = picked === 0;
  const single = picked === 1;
  btnExportSlots.disabled = none;
  btnDeleteSlots.disabled = none;
  btnVarsSlots.disabled = !single;
  slotInfoEditor.hidden = !single;
  if (single) {
    const item = pickedItems()[0];
    const values = slotInfoValues(item);
    bulkName.value = values.saveName;
    bulkTitle.value = values.title;
    slotInfoBaseline = { name: values.saveName.trim(), title: values.title.trim() };
    bulkInfo.textContent = '槽 ' + item.slot;
  } else {
    bulkInfo.textContent = '';
    slotInfoBaseline = { name: '', title: '' };
  }
  updateBulkApplyState();
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
        const key = escapeHtml(d.name) + '|' + escapeHtml(String(slot));
        const meta = 'slot=' + slot + (multiDb ? ' · ' + d.name : '') + (time ? ' · ' + time : '');
        parts.push('<div class="slotWrap" data-wrap="' + key + '">' +
          '<div class="item" draggable="true" data-slot="' + key + '">' +
          '<span class="handle" title="拖动到另一个槽上可互换槽位">☰</span>' +
          '<input type="checkbox" class="slotCheck" data-idx="' + idx + '">' +
          '<span class="grow"><b>' + escapeHtml(title) + '</b> ' +
          '<span class="meta">' + escapeHtml(meta) + '</span></span></div></div>');
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

async function deleteSelectedSlots() {
  const items = pickedItems();
  if (items.length === 0) { log('请先勾选要删除的存档槽。'); return; }
  const slotsText = items.map(x => x.slot).join('、');
  if (!confirm('确定删除选中的 ' + items.length + ' 个存档槽（' + slotsText + '）？此操作不可撤销，建议先导出或备份。')) return;
  const groups = new Map();
  for (const it of items) {
    if (!groups.has(it.db)) groups.set(it.db, []);
    groups.get(it.db).push(it.slot);
  }
  let done = 0;
  for (const [dbName, slots] of groups) {
    try {
      const { db } = await openVersionDb(dbName);
      try {
        const names = [...db.objectStoreNames].filter(n => n === 'saves' || n === 'details');
        const tx = db.transaction(names, 'readwrite');
        for (const n of names) {
          const os = tx.objectStore(n);
          for (const s of slots) os.delete(Number.isNaN(Number(s)) ? s : Number(s));
        }
        await txDone(tx);
        done += slots.length;
      } finally {
        db.close();
      }
    } catch (e) {
      log('删除 ' + dbName + ' 的选中槽失败：' + e.message);
    }
  }
  log('已删除 ' + done + ' 个存档槽');
  loadSlots(); refreshDbs();
}

function splitKey(key) {
  const i = key.indexOf('|');
  return i < 0 ? [key, ''] : [key.slice(0, i), key.slice(i + 1)];
}

function recSlotOf(pair) {
  const v = pair.value;
  if (v && v.slot !== undefined && v.slot !== null) return String(v.slot);
  return String(pair.key);
}

function withSlot(value, slot) {
  const copy = Object.assign({}, value);
  copy.slot = slot;
  return copy;
}

function putRecord(os, value, key) {
  if (os.keyPath != null) os.put(value);
  else os.put(value, key);
}

async function openVersionDb(dbName) {
  const dbs = await listDbs();
  const d = dbs.find(x => x.name === dbName) || { name: dbName };
  return { db: await openDb(d.name, d.version), version: d.version };
}

async function swapSlots(dbName, slotA, slotB) {
  if (String(slotA) === String(slotB)) return;
  const { db } = await openVersionDb(dbName);
  try {
    const names = [...db.objectStoreNames].filter(n => n === 'saves' || n === 'details');
    const pairs = {};
    for (const n of names) pairs[n] = await reqCursor(db.transaction(n, 'readonly').objectStore(n));
    const tx = db.transaction(names, 'readwrite');
    let moved = 0;
    for (const n of names) {
      const os = tx.objectStore(n);
      const a = pairs[n].find(p => recSlotOf(p) === String(slotA));
      const b = pairs[n].find(p => recSlotOf(p) === String(slotB));
      if (!a && !b) continue;
      if (a) os.delete(a.key);
      if (b) os.delete(b.key);
      if (a) {
        const target = b ? b.value.slot : (Number.isNaN(Number(slotB)) ? slotB : Number(slotB));
        putRecord(os, withSlot(a.value, target), target);
        moved++;
      }
      if (b) {
        const target = a ? a.value.slot : (Number.isNaN(Number(slotA)) ? slotA : Number(slotA));
        putRecord(os, withSlot(b.value, target), target);
        moved++;
      }
    }
    await txDone(tx);
    log('已互换槽位 ' + slotA + ' ⇄ ' + slotB + '（写入 ' + moved + ' 条）');
    loadSlots();
  } catch (e) {
    log('互换槽位失败：' + e.message);
  } finally {
    db.close();
  }
}

async function updateSlotInfos(items, fields) {
  if (items.length === 0) return false;
  const groups = new Map();
  for (const it of items) {
    if (!groups.has(it.db)) groups.set(it.db, []);
    groups.get(it.db).push(it.slot);
  }
  let done = 0;
  let failed = 0;
  for (const [dbName, slots] of groups) {
    try {
      const { db } = await openVersionDb(dbName);
      try {
        if (!db.objectStoreNames.contains('details')) { log('这个库里没有 details 表，无法编辑槽信息。'); failed++; continue; }
        const pairs = await reqCursor(db.transaction('details', 'readonly').objectStore('details'));
        const tx = db.transaction('details', 'readwrite');
        const os = tx.objectStore('details');
        for (const slot of slots) {
          const found = pairs.find(p => recSlotOf(p) === String(slot));
          const rec = found ? Object.assign({}, found.value) : { slot: Number.isNaN(Number(slot)) ? slot : Number(slot), data: {} };
          const body = (rec.data && typeof rec.data === 'object') ? Object.assign({}, rec.data) : {};
          const meta = (body.metadata && typeof body.metadata === 'object') ? Object.assign({}, body.metadata) : {};
          if (fields.saveName !== undefined) {
            if (fields.saveName) meta.saveName = fields.saveName;
            else delete meta.saveName;
          }
          if (fields.title !== undefined) {
            if (fields.title) body.title = fields.title;
            else delete body.title;
          }
          body.metadata = meta;
          rec.data = body;
          putRecord(os, rec, found ? found.key : rec.slot);
          done++;
        }
        await txDone(tx);
      } finally {
        db.close();
      }
    } catch (e) {
      failed++;
      log('更新 ' + dbName + ' 的槽信息失败：' + e.message);
    }
  }
  log('已更新 ' + done + ' 个存档槽的信息' + (failed > 0 ? '，' + failed + ' 个失败' : ''));
  if (failed > 0) return false;
  loadSlots();
  return true;
}

function cloneDeep(value) {
  if (value === undefined) return undefined;
  try {
    if (typeof structuredClone === 'function') return structuredClone(value);
  } catch (e) { /* 含函数等不可克隆内容时退回 JSON 复制 */ }
  return JSON.parse(JSON.stringify(value));
}

// 与游戏内 State.deltaEncode / deltaDecode 使用同一套 SugarCube Diff 规则。
const DIFF_OP = { Delete: 0, SpliceArray: 1, Copy: 2, CopyDate: 3 };

function hasOwn(obj, key) {
  return Object.prototype.hasOwnProperty.call(obj, key);
}

function diffIsNumeric(value) {
  const num = typeof value === 'number' ? value : Number(value);
  return !Number.isNaN(num) && Number.isFinite(num);
}

function jsonDiff(a, b) {
  const tag = Object.prototype.toString;
  const aIsArray = a instanceof Array;
  const delta = Object.create(null);
  const keys = [...Object.keys(a), ...Object.keys(b)].sort().filter((val, i, arr) => i === 0 || arr[i - 1] !== val);
  let aOpKey;
  for (const key of keys) {
    const aVal = a[key];
    const bVal = b[key];
    if (hasOwn(a, key)) {
      if (hasOwn(b, key)) {
        if (aVal === bVal) continue;
        if (typeof aVal === typeof bVal) {
          if (typeof aVal === 'function') {
            if (aVal.toString() !== bVal.toString()) delta[key] = [DIFF_OP.Copy, bVal];
          } else if (typeof aVal !== 'object' || aVal === null) {
            delta[key] = [DIFF_OP.Copy, bVal];
          } else {
            const aType = tag.call(aVal);
            if (aType === tag.call(bVal)) {
              if (aVal instanceof Date) {
                if (aVal.getTime() !== bVal.getTime()) delta[key] = [DIFF_OP.Copy, cloneDeep(bVal)];
              } else if (aVal instanceof Map) delta[key] = [DIFF_OP.Copy, cloneDeep(bVal)];
              else if (aVal instanceof RegExp) {
                if (aVal.toString() !== bVal.toString()) delta[key] = [DIFF_OP.Copy, bVal];
              } else if (aVal instanceof Set) delta[key] = [DIFF_OP.Copy, cloneDeep(bVal)];
              else if (aVal instanceof Array || aType === '[object Object]') {
                const sub = jsonDiff(aVal, bVal);
                if (sub !== null) delta[key] = sub;
              } else delta[key] = [DIFF_OP.Copy, cloneDeep(bVal)];
            } else delta[key] = [DIFF_OP.Copy, cloneDeep(bVal)];
          }
        } else {
          delta[key] = [DIFF_OP.Copy, (typeof bVal !== 'object' || bVal === null) ? bVal : cloneDeep(bVal)];
        }
      } else if (aIsArray && diffIsNumeric(key)) {
        const index = Number(key);
        if (!aOpKey) {
          aOpKey = '';
          do { aOpKey += '~'; } while (keys.includes(aOpKey));
          delta[aOpKey] = [DIFF_OP.SpliceArray, index, index];
        }
        if (index < delta[aOpKey][1]) delta[aOpKey][1] = index;
        if (index > delta[aOpKey][2]) delta[aOpKey][2] = index;
      } else {
        delta[key] = DIFF_OP.Delete;
      }
    } else {
      delta[key] = [DIFF_OP.Copy, (typeof bVal !== 'object' || bVal === null) ? bVal : cloneDeep(bVal)];
    }
  }
  return Object.keys(delta).length > 0 ? delta : null;
}

function jsonPatch(orig, delta) {
  const keys = delta ? Object.keys(delta) : [];
  const patched = cloneDeep(orig);
  for (const key of keys) {
    const value = delta[key];
    if (value === DIFF_OP.Delete) delete patched[key];
    else if (value instanceof Array) {
      switch (value[0]) {
        case DIFF_OP.SpliceArray: patched.splice(value[1], value[2] - value[1] + 1); break;
        case DIFF_OP.Copy: patched[key] = cloneDeep(value[1]); break;
        case DIFF_OP.CopyDate: patched[key] = new Date(value[1]); break;
        default: patched[key] = value;
      }
    } else patched[key] = jsonPatch(patched[key], value);
  }
  return patched;
}

function decodeSaveHistory(data) {
  if (!data || typeof data !== 'object') return null;
  if (Array.isArray(data.history)) return { history: data.history, field: 'history' };
  if (Array.isArray(data.delta)) {
    const history = [cloneDeep(data.delta[0])];
    for (let i = 1; i < data.delta.length; i++) history.push(jsonPatch(history[i - 1], data.delta[i]));
    return { history: history, field: 'delta' };
  }
  return null;
}

function encodeSaveHistory(history, field) {
  if (field === 'delta') {
    const delta = [history[0]];
    for (let i = 1; i < history.length; i++) delta.push(jsonDiff(history[i - 1], history[i]));
    return { delta: delta };
  }
  return { history: history };
}

function varMoment(state) {
  const h = state.history;
  const i = Number(state.dataIndex);
  const idx = Number.isInteger(i) && i >= 0 && i < h.length ? i : h.length - 1;
  return h[idx] || {};
}

function previewValue(value) {
  let text;
  try { text = JSON.stringify(value); } catch (e) { text = String(value); }
  if (text === undefined) text = String(value);
  return text.length > 120 ? text.slice(0, 120) + '…' : text;
}

function renderVarList() {
  if (!VAR_STATE) return;
  const moment = varMoment(VAR_STATE);
  const vars = (moment.variables && typeof moment.variables === 'object') ? moment.variables : {};
  const keyword = varSearch.value.trim().toLowerCase();
  const names = Object.keys(vars).filter(k => !keyword || k.toLowerCase().includes(keyword)).sort();
  const limit = 300;
  const shown = names.slice(0, limit);
  if (names.length === 0) {
    varList.innerHTML = '<div class="hint">没有匹配的变量。</div>';
    return;
  }
  const rows = shown.map(k => '<div class="vrow" data-var="' + escapeHtml(k) + '">' +
    '<span class="vname">' + escapeHtml(k) + '</span>' +
    '<span class="vval">' + escapeHtml(previewValue(vars[k])) + '</span></div>');
  if (names.length > limit) rows.push('<div class="hint">共 ' + names.length + ' 个，仅显示前 ' + limit + ' 个，请用搜索缩小范围。</div>');
  varList.innerHTML = rows.join('');
}

async function openVarPanel(item) {
  if (!item) { log('请先勾选要编辑变量的存档槽。'); return; }
  const { db } = await openVersionDb(item.db);
  try {
    if (!db.objectStoreNames.contains('saves')) { log('这个库里没有 saves 表。'); return; }
    const pairs = await reqCursor(db.transaction('saves', 'readonly').objectStore('saves'));
    const found = pairs.find(p => recSlotOf(p) === String(item.slot));
    if (!found) { log('没有找到槽 ' + item.slot + ' 的存档数据。'); return; }
    const data = found.value.data || {};
    const decoded = decodeSaveHistory(data);
    if (!decoded) { log('槽 ' + item.slot + ' 的数据里没有可识别的内容（无 history / delta）。'); return; }
    VAR_STATE = {
      db: item.db,
      slot: item.slot,
      key: found.key,
      dataIndex: data.index,
      field: decoded.field,
      history: cloneDeep(decoded.history),
      editKey: null
    };
    varCard.style.display = '';
    varEditor.style.display = 'none';
    varSearch.value = '';
    varSlotInfo.textContent = '槽 ' + item.slot + ' · ' + (decoded.field === 'delta' ? '增量存档（已还原为完整历史）' : '完整存档');
    renderVarList();
    varCard.scrollIntoView({ behavior: 'smooth', block: 'start' });
  } catch (e) {
    log('读取变量失败：' + e.message);
  } finally {
    db.close();
  }
}

async function writeVarState() {
  const st = VAR_STATE;
  const { db } = await openVersionDb(st.db);
  try {
    const pairs = await reqCursor(db.transaction('saves', 'readonly').objectStore('saves'));
    const found = pairs.find(p => recSlotOf(p) === String(st.slot));
    if (!found) { log('写回失败：找不到槽 ' + st.slot); return; }
    const rec = cloneDeep(found.value);
    const data = (rec.data && typeof rec.data === 'object') ? rec.data : {};
    const encoded = encodeSaveHistory(st.history, st.field);
    if (st.field === 'delta') { data.delta = encoded.delta; delete data.history; }
    else { data.history = encoded.history; delete data.delta; }
    data.index = Number.isInteger(Number(st.dataIndex)) ? Number(st.dataIndex) : st.history.length - 1;
    rec.data = data;
    const tx = db.transaction('saves', 'readwrite');
    putRecord(tx.objectStore('saves'), rec, found.key);
    await txDone(tx);
    log('已写入槽 ' + st.slot + ' 的变量 ' + st.editKey);
    renderVarList();
  } catch (e) {
    log('写回变量失败：' + e.message);
  } finally {
    db.close();
  }
}

async function saveVariable() {
  const st = VAR_STATE;
  if (!st || !st.editKey) return;
  let value;
  try { value = JSON.parse(varValue.value); }
  catch (e) { log('内容不是合法 JSON：' + e.message); return; }
  const moment = varMoment(st);
  if (!moment.variables || typeof moment.variables !== 'object') moment.variables = {};
  moment.variables[st.editKey] = value;
  await writeVarState();
  varEditor.style.display = 'none';
  st.editKey = null;
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

async function applyBulkInfo() {
  const items = pickedItems();
  if (items.length !== 1) { log('编辑信息只能对单个选中槽使用。'); return; }
  const name = bulkName.value.trim();
  const title = bulkTitle.value.trim();
  const item = items[0];
  // 写回期间先禁用按钮，失败时再按实际内容恢复
  bulkApply.disabled = true;
  slotInfoEditor.hidden = true;
  const ok = await updateSlotInfos(items, { saveName: name, title: title });
  if (!ok) {
    slotInfoEditor.hidden = false;
    updateBulkApplyState();
    log('名称 / 标题写回失败，可修改后重试。');
    return;
  }
  slotInfoBaseline = { name: name, title: title };
  updateBulkApplyState();
  log('名称 / 标题已写回槽 ' + item.slot);
}

bulkApply.addEventListener('click', () => applyBulkInfo().catch(e => log(e.message)));
bulkName.addEventListener('input', updateBulkApplyState);
bulkTitle.addEventListener('input', updateBulkApplyState);
document.getElementById('bulkCancel').addEventListener('click', () => {
  slotsEl.querySelectorAll('input.slotCheck').forEach(el => { el.checked = false; });
  updateSlotSelection();
});
btnVarsSlots.addEventListener('click', () => {
  const items = pickedItems();
  if (items.length !== 1) { log('编辑变量只能对单个选中槽使用。'); return; }
  openVarPanel(items[0]).catch(e => log(e.message));
});

let dragKey = null;
slotsEl.addEventListener('dragstart', e => {
  const item = e.target.closest ? e.target.closest('.item[data-slot]') : null;
  if (!item) return;
  dragKey = item.getAttribute('data-slot');
  item.classList.add('dragging');
  if (e.dataTransfer) {
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', dragKey);
  }
});
slotsEl.addEventListener('dragend', () => {
  dragKey = null;
  slotsEl.querySelectorAll('.item').forEach(el => el.classList.remove('dragging', 'dropTarget'));
});
slotsEl.addEventListener('dragover', e => {
  const item = e.target.closest ? e.target.closest('.item[data-slot]') : null;
  if (!item || !dragKey || item.getAttribute('data-slot') === dragKey) return;
  e.preventDefault();
  if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
  item.classList.add('dropTarget');
});
slotsEl.addEventListener('dragleave', e => {
  const item = e.target.closest ? e.target.closest('.item[data-slot]') : null;
  if (item) item.classList.remove('dropTarget');
});
slotsEl.addEventListener('drop', e => {
  const item = e.target.closest ? e.target.closest('.item[data-slot]') : null;
  if (!item || !dragKey) return;
  e.preventDefault();
  const src = dragKey;
  const dst = item.getAttribute('data-slot');
  dragKey = null;
  slotsEl.querySelectorAll('.item').forEach(el => el.classList.remove('dragging', 'dropTarget'));
  if (src === dst) return;
  const [srcDb, srcSlot] = splitKey(src);
  const [dstDb, dstSlot] = splitKey(dst);
  if (srcDb !== dstDb) { log('只能在同一存档库内互换槽位。'); return; }
  swapSlots(srcDb, srcSlot, dstSlot).catch(x => log(x.message));
});

varList.addEventListener('click', e => {
  const row = e.target.closest ? e.target.closest('.vrow') : null;
  if (!row || !VAR_STATE) return;
  const key = row.getAttribute('data-var');
  const moment = varMoment(VAR_STATE);
  const vars = (moment.variables && typeof moment.variables === 'object') ? moment.variables : {};
  if (!(key in vars)) return;
  VAR_STATE.editKey = key;
  varPath.textContent = 'variables.' + key;
  varValue.value = JSON.stringify(vars[key], null, 1);
  varEditor.style.display = '';
  varValue.focus();
});

varSearch.addEventListener('input', renderVarList);
document.getElementById('varClose').addEventListener('click', () => {
  varCard.style.display = 'none';
  varEditor.style.display = 'none';
  VAR_STATE = null;
});
document.getElementById('varReload').addEventListener('click', () => {
  if (!VAR_STATE) return;
  openVarPanel({ db: VAR_STATE.db, slot: VAR_STATE.slot }).catch(x => log(x.message));
});
document.getElementById('varSave').addEventListener('click', () => saveVariable().catch(x => log(x.message)));
document.getElementById('varCancel').addEventListener('click', () => {
  varEditor.style.display = 'none';
  if (VAR_STATE) VAR_STATE.editKey = null;
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
btnDeleteSlots.addEventListener('click', () => deleteSelectedSlots().catch(e => log(e.message)));

if (AUTO_ONLY) {
  // 启动器触发的自动备份：只显示提示，不加载可编辑界面
  (async () => {
    const msg = document.getElementById('autoMsg');
    const detail = document.getElementById('autoDetail');
    const spin = document.getElementById('autoSpin');
    try {
      const name = await backupToLauncher(true);
      if (!name) {
        spin.style.display = 'none';
        msg.textContent = '没有可备份的存档。';
        detail.textContent = '备份结束，此页面可以关闭。';
        return;
      }
      spin.style.display = 'none';
      msg.textContent = '存档备份完成';
      detail.textContent = '已保存为 ' + name + '，此页面将自动关闭；若未关闭可手动关闭。';
      setTimeout(() => { try { window.close(); } catch (e) { } }, 1500);
    } catch (e) {
      spin.style.display = 'none';
      msg.textContent = '备份失败：' + e.message;
      detail.textContent = '请关闭此页面，回到启动器查看日志或重试。';
    }
  })();
} else {
  refreshDbs().catch(e => log(e.message));
  loadSlots().catch(e => log(e.message));
  loadLauncherBackups();
}
</script>
<script>
  /* 关页检测：与游戏页共用同一套端点，页面全部关闭后启动器会停掉本地服务器 */
  (function () {
    try {
      var PAGE = 'h' + Math.random().toString(36).slice(2);
      var HB = '/__launcher/api/heartbeat?page=' + PAGE;
      var CLOSED = '/__launcher/api/page-closed?page=' + PAGE;
      function beat() {
        try { fetch(HB, { method: 'POST', keepalive: true }).catch(function () { }); } catch (e) { }
      }
      beat();
      setInterval(beat, __HEARTBEAT_MS__);
      window.addEventListener('pagehide', function () {
        try {
          if (navigator.sendBeacon) navigator.sendBeacon(CLOSED, '');
          else fetch(CLOSED, { method: 'POST', keepalive: true }).catch(function () { });
        } catch (e) { }
      });
    } catch (e) { }
  })();
</script>
</body>
</html>
""";
        return html
            .Replace("__VERSION_JSON__", versionJson)
            .Replace("__PAGE_TITLE__", autoOnly ? "正在备份存档" : "存档助手")
            .Replace("__MAIN_HIDDEN__", autoOnly ? " hidden" : "")
            .Replace("__AUTO_HIDDEN__", autoOnly ? "" : " hidden")
            .Replace("__AUTO_ONLY__", autoOnly ? "true" : "false")
            .Replace("__HEARTBEAT_MS__", (Math.Clamp(AppConfig.Load().General.HeartbeatSeconds, 1, 60) * 1000).ToString());
    }
}
