/*
 * 玩家页「设置 → 语言」的验证探针（开发工具，不参与打包）。
 *
 * 用最小的 DOM 桩把**真的** wwwroot/js/player.js 跑起来，检查五件事：
 *   1) 启动路径无异常（新加的 i18n / 菜单代码有没有 TDZ、拼写、API 缺失）；
 *   2) 中英两份词表的**键完全对齐**（漏一条就会有一条永远显示中文）；
 *   3) 占位符数量一致（中文写 {0}{1}、英文只写 {0} 的话，参数会被吞掉）；
 *   4) 语言顺序 = 电脑版规则：**系统语言置顶**，其余按英文名首字母；
 *   5) 选中语言后 T() 立刻换词、localStorage 记住（网页端没有确认框/回退）。
 *
 * 和 probe_player_orientation.js 同一套办法：只往源码里插一个 hook 把内部函数
 * 暴露出来，其余逐字节照跑磁盘上那份 player.js。
 *
 * 用法：node scripts\probe_player_i18n.js
 * 退出码 0 = 全部断言通过。
 */
'use strict';

const fs = require('fs');
const path = require('path');

const target = process.argv[2] || path.join(__dirname, '..', 'wwwroot', 'js', 'player.js');
if (!fs.existsSync(target)) {
    console.error('找不到 player.js: ' + target);
    process.exit(2);
}

let src = fs.readFileSync(target, 'utf8');

const MARK = '    // 启动';
if (src.indexOf(MARK) < 0) {
    console.error('源码里找不到 "// 启动" 标记，无法注入探针（约定变了？）');
    process.exit(2);
}

src = src.replace(MARK,
    '    globalThis.__i18nProbe = {\n' +
    '        T: T, setLanguage: setLanguage, langOrder: langOrder,\n' +
    '        STRINGS: STRINGS, LANGS: LANGS, normalizeLang: normalizeLang,\n' +
    '        nativeName: nativeName, rev: PLAYER_JS_REV,\n' +
    '        getLang: function () { return uiLang; },\n' +
    '        reapply: function () { applyI18n(); },\n' +
    '    };\n' + MARK);

// ---------------- 极简 DOM 桩 ----------------
const CTX = new Proxy({}, {
    get: function () { return function () {}; },
    set: function () { return true; },
});

function makeEl(id) {
    const el = {
        id: id, style: {}, textContent: '', value: '', disabled: false,
        width: 1, height: 1, naturalWidth: 0, naturalHeight: 0,
        className: '', src: '', innerHTML: '',
        addEventListener: function () {}, removeEventListener: function () {},
        appendChild: function () {}, removeAttribute: function () {}, setAttribute: function () {},
        getContext: function () { return CTX; },
        getBoundingClientRect: function () {
            return { left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0 };
        },
        classList: {
            add: function () {}, remove: function () {}, toggle: function () {},
            contains: function () { return false; },
        },
        querySelectorAll: function () { return []; },
    };
    Object.defineProperty(el, 'offsetWidth', { get: function () { return parseInt(el.style.width, 10) || 0; } });
    Object.defineProperty(el, 'offsetHeight', { get: function () { return parseInt(el.style.height, 10) || 0; } });
    Object.defineProperty(el, 'clientWidth', { get: function () { return el._cw || 0; } });
    Object.defineProperty(el, 'clientHeight', { get: function () { return el._ch || 0; } });
    return el;
}

const els = {};
const documentStub = {
    getElementById: function (id) { return els[id] || (els[id] = makeEl(id)); },
    createElement: function (tag) { return makeEl('created:' + tag); },
    querySelectorAll: function () { return []; },
    addEventListener: function () {},
    cookie: '',
    title: '',
    documentElement: makeEl('html'),
    body: makeEl('body'),
    visibilityState: 'visible',
};

const socketStub = { on: function () {}, emit: function () {}, connected: false, reconnect: function () {} };
const win = {
    addEventListener: function () {},
    screen: {
        width: 400, height: 800,
        orientation: { type: 'portrait-primary', angle: 0, addEventListener: function () {} },
    },
    io: function () { return socketStub; },
    innerWidth: 400, innerHeight: 800,
    location: { search: '' },
    setTimeout: setTimeout,
    setInterval: function () { return 0; },
    clearInterval: function () {},
    orientation: 0,
};

const stored = {};
const localStorageStub = {
    getItem: function (k) { return Object.prototype.hasOwnProperty.call(stored, k) ? stored[k] : null; },
    setItem: function (k, v) { stored[k] = String(v); },
};

globalThis.window = win;
globalThis.document = documentStub;
globalThis.location = win.location;
globalThis.screen = win.screen;
globalThis.Image = function () { return makeEl('img'); };
globalThis.fetch = function () { return Promise.resolve({ json: function () { return Promise.resolve({}); } }); };

// Node 22 自带只读的 navigator；用它模拟不同系统语言
const navStub = { language: 'zh-CN' };
Object.defineProperty(globalThis, 'navigator', { value: navStub, configurable: true, writable: true });
Object.defineProperty(globalThis, 'localStorage', { value: localStorageStub, configurable: true, writable: true });

// ---------------- 跑真的 player.js ----------------
let bootError = null;
try {
    eval(src);                                    // eslint-disable-line no-eval
} catch (e) {
    bootError = e;
}

const probe = globalThis.__i18nProbe;
let failures = 0;

function check(ok, label) {
    if (!ok) { failures++; console.log('  [FAIL] ' + label); }
    else { console.log('  [PASS] ' + label); }
}

console.log('target : ' + target);
console.log('rev    : ' + (probe && probe.rev ? probe.rev : '(读不到)'));
console.log('');

console.log('=== 启动路径 ===');
check(bootError === null, 'player.js 启动无异常' + (bootError ? '  -> ' + bootError : ''));
if (bootError || !probe) {
    console.log('');
    console.log('启动就失败了，后面的用例没有意义。');
    process.exit(1);
}

const zh = probe.STRINGS['zh-cn'] || {};
const en = probe.STRINGS['en-us'] || {};

// ---------------- 词表完整性 ----------------
console.log('');
console.log('=== 词表对齐 ===');

const zhKeys = Object.keys(zh);
const enKeys = Object.keys(en);
const missingEn = zhKeys.filter(function (k) { return !(k in en); });
const missingZh = enKeys.filter(function (k) { return !(k in zh); });
const empty = zhKeys.concat(enKeys).filter(function (k) {
    return !String(zh[k] === undefined ? en[k] : zh[k]).length ||
           !String(en[k] === undefined ? zh[k] : en[k]).length;
});

check(zhKeys.length > 50, '中文词条 ' + zhKeys.length + ' 条');
check(missingEn.length === 0, '英文不缺键' + (missingEn.length ? '  -> ' + missingEn.join(', ') : ''));
check(missingZh.length === 0, '中文不缺键' + (missingZh.length ? '  -> ' + missingZh.join(', ') : ''));
check(empty.length === 0, '没有空词条' + (empty.length ? '  -> ' + empty.join(', ') : ''));

// 占位符数量必须一致：{0} 在中文里有、英文里没有，参数就被吞了
function placeholders(text) {
    const list = [];
    String(text).replace(/\{(\d)\}/g, function (_, d) { list.push(d); });
    return list.sort().join(',');
}
const badParams = zhKeys.filter(function (k) {
    return en[k] !== undefined && placeholders(zh[k]) !== placeholders(en[k]);
});
check(badParams.length === 0,
    '中英占位符一致' + (badParams.length ? '  -> ' + badParams.join(', ') : ''));

// ---------------- 语言顺序 ----------------
console.log('');
console.log('=== 语言顺序（系统语言置顶，其余按英文名）===');

navStub.language = 'zh-CN';
let order = probe.langOrder();
check(order.length === probe.LANGS.length, '列表里 ' + order.length + ' 种语言');
check(order[0].code === 'zh-cn', '中文系统 → 简体中文置顶（推荐）');
check(order[1] && order[1].code === 'en-us', '其余是 English');

navStub.language = 'en-GB';
order = probe.langOrder();
check(order[0].code === 'en-us', '英文系统 → English 置顶（推荐）');
check(order[1] && order[1].code === 'zh-cn', '其余是简体中文');

navStub.language = 'zh-CN';     // 还原，后面的用例按中文系统算

// ---------------- 切换语言 ----------------
console.log('');
console.log('=== 切换语言（立刻生效，无确认/回退）===');

check(probe.getLang() === 'zh-cn', '默认跟随系统（zh-CN）');
check(probe.T('fab.settings') === '设置', '中文：fab.settings = 设置');

probe.setLanguage('en-us');
check(probe.getLang() === 'en-us', 'setLanguage 之后当前语言 = en-us');
check(probe.T('fab.settings') === 'Settings', '英文：fab.settings = Settings');
check(probe.T('login.enter') === 'Enter', '英文：login.enter = Enter');
check(probe.T('status.online') === '● Online', '英文：状态条');
check(stored['dnd_player_lang'] === 'en-us', '选择已写进 localStorage');

// 带参数：房间号 / 玩家离开
check(probe.T('room.number', '12345678') === 'Room 12345678', '英文参数：房间号');
check(probe.T('toast.left', 'Ann') === 'Ann left the room', '英文参数：离开提示');
check(probe.T('login.fill', ' and password').indexOf(' and password') > 0, '英文参数：/join 提示');

probe.setLanguage('zh-cn');
check(probe.T('room.number', '12345678') === '房间号 12345678', '切回中文：房间号');
check(probe.T('toast.left', 'Ann') === 'Ann 离开了房间', '切回中文：离开提示');

// 普通名称里的 $ 不能被当成替换模式
check(probe.T('toast.left', 'A$&B') === 'A$&B 离开了房间', '玩家名里的 $ 原样保留');

// normalize
check(probe.normalizeLang('zh-Hans-CN') === 'zh-cn', 'zh-Hans-CN → zh-cn');
check(probe.normalizeLang('en_US') === 'en-us', 'en_US → en-us');
check(probe.normalizeLang('fr-FR') === 'zh-cn', '不认识的语言 → 兜底中文');

// 重新 apply 不抛异常（这一步会碰 querySelectorAll / createElement / classList）
let reapplyError = null;
try { probe.reapply(); } catch (e) { reapplyError = e; }
check(reapplyError === null, 'applyI18n 重放无异常' + (reapplyError ? '  -> ' + reapplyError : ''));

console.log('');
if (failures === 0) {
    console.log('ALL CHECKS PASSED');
    process.exit(0);
}
console.log('FAILED: ' + failures + ' item(s)');
process.exit(1);
