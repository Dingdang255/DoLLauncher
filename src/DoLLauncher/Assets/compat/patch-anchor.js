// DoLL 补丁锚点兼容层
// ModI18N 的补丁用绝对字符偏移（pos）定位，ModLoader / 游戏更新后偏移会漂移，
// 一旦超出它自带的 ±2 字符与 ±(10~30) 字符模糊窗口，补丁就会被整条跳过。
// 这里在它真正打补丁前，按原文（from）重新定位并改写 pos：
//   唯一命中 → 用实际位置；多处命中 → 取离原 pos 最近的一处；找不到 → 保持原样并计数，绝不瞎猜。
(() => {
    'use strict';
    const TAG = '[DoLL-compat]';
    const PATCHED = '__dollAnchorCompat';

    const matchesAt = (text, from, pos) =>
        typeof pos === 'number' && pos >= 0 && pos + from.length <= text.length && text.startsWith(from, pos);

    function reanchor(list, getText, stats) {
        for (const v of list || []) {
            if (!v || typeof v.from !== 'string' || v.from.length === 0) continue;
            const text = getText(v);
            if (typeof text !== 'string' || text.length === 0) continue;
            if (matchesAt(text, v.from, v.pos)) { stats.hit++; continue; }

            let index = text.indexOf(v.from);
            if (index < 0) {
                stats.miss++;
                const key = v.passageName || v.fileName || '?';
                stats.missFiles.set(key, (stats.missFiles.get(key) || 0) + 1);
                continue;
            }

            let best = index;
            let count = 1;
            let next = index;
            while ((next = text.indexOf(v.from, next + 1)) >= 0) {
                count++;
                if (Math.abs(next - v.pos) < Math.abs(best - v.pos)) best = next;
            }
            v.pos = best;
            if (count > 1) stats.multi++; else stats.fixed++;
        }
    }

    function run() {
        const i18n = window.modI18N;
        const typeB = i18n && i18n.typeB;
        if (!typeB) return;

        const manager = window.modSC2DataManager;
        const sc2 = manager && typeof manager.getSC2DataInfoAfterPatch === 'function'
            ? manager.getSC2DataInfoAfterPatch()
            : null;
        if (!sc2) return;

        const itemsOf = (holder) => (holder && holder.items) || [];
        const nameOf = (holder, item) =>
            holder && typeof holder.getNoPathNameFromString === 'function'
                ? holder.getNoPathNameFromString(item.name)
                : item.name;

        const scripts = new Map();
        const styles = new Map();
        const passages = new Map();
        for (const item of itemsOf(sc2.scriptFileItems)) scripts.set(nameOf(sc2.scriptFileItems, item), item.content);
        for (const item of itemsOf(sc2.styleFileItems)) styles.set(nameOf(sc2.styleFileItems, item), item.content);
        for (const item of itemsOf(sc2.passageDataItems)) passages.set(item.name, item.content);

        const stats = { hit: 0, fixed: 0, multi: 0, miss: 0, missFiles: new Map() };
        reanchor(typeB.OutputText, (v) =>
            v.js ? scripts.get(v.fileName) : v.css ? styles.get(v.fileName) : undefined, stats);
        reanchor(typeB.InputStoryScript, (v) =>
            passages.get(v.passageName) || scripts.get(v.fileName), stats);

        const miss = Array.from(stats.missFiles.entries())
            .sort((a, b) => b[1] - a[1])
            .slice(0, 6)
            .map(([file, count]) => file + ':' + count)
            .join('、');

        console.log(TAG, '锚点检查完成', '命中=' + stats.hit, '重定位=' + stats.fixed,
            '多处命中=' + stats.multi, '未找到=' + stats.miss + (miss ? '（' + miss + '）' : ''));

        if (stats.miss > 0) {
            console.warn(TAG, '有 ' + stats.miss + ' 条补丁的原文在当前 ModLoader/游戏输出里已不存在，' +
                '相关文本会保持原文；请更新汉化包或改用匹配当前 ModLoader 的版本。');
        }
    }

    function wrap(instance) {
        if (!instance || instance[PATCHED]) return;
        const original = instance.startReplace;
        if (typeof original !== 'function') return;
        instance[PATCHED] = true;
        instance.startReplace = function () {
            try { run(); } catch (e) { console.error(TAG, '锚点重定位失败', e); }
            return original.apply(this, arguments);
        };
    }

    if (window.modI18N) {
        wrap(window.modI18N);
    } else {
        // 兼容脚本先于 ModI18N 加载的情况：拦截实例赋值后再包装。
        let current;
        try {
            Object.defineProperty(window, 'modI18N', {
                configurable: true,
                get() { return current; },
                set(value) { current = value; wrap(value); },
            });
        } catch (e) {
            console.error(TAG, '无法监听 modI18N 实例', e);
        }
    }
})();
