/*
 * 手机端朝向 / 网格几何的验证探针（开发工具，不参与打包）。
 *
 * 用最小的 DOM 桩把**真的** wwwroot/js/player.js 跑起来，检查四件事：
 *   1) 启动路径无异常（TDZ / 拼写 / 调用顺序的问题都会在这里现形）；
 *   2) **extra 的取值**是否符合定稿规则（第二十三轮：只跟视口方向走）；
 *   3) **内容盒子**的比例是否等于**电脑画布**的宽高比（halfW : halfH），
 *      以及"转过去之后"是否放得进屏幕、格子像素是否 = 电脑端的等比缩放；
 *   4) 没底图时是否还能按锚点画（而不是退回"贴左上角、一格 37.8px"的老网格）。
 *
 * 【第二十三轮定稿的规则】只看**视口方向**，两个值：
 *
 *      视口竖（设备正立 / 倒立）      → extra = 90
 *      视口横（向左横排 / 向右横排）  → extra = 0    ← 与电脑画布那一张完全一样
 *
 * 依据 = 用户逐姿态确认的原话：
 *   「倒立和向左横排（设备逆时针转 90 度）的地图朝向是对的，
 *     正立和向右横排的方向是反的」
 * —— 两组各错 180°，而它们恰好就是"竖屏组 / 横屏组"。
 *
 * ---- 被实机推翻的两条旧推导（留在这里，别再犯）----
 * · 第二十一轮 `extra ≡ 0`：把画面焊死在屏幕上 ⇒ 地图跟着设备转。
 * · 第二十二轮 `extra = 设备转角 − 90`：假设"四种姿态在世界里朝同一边"，
 *   但用户实机看到的是"两种对、两种反 180°" ⇒ 前提不成立。
 * ⇒ **不要再去假设某个"世界朝向"是正确的；以实机逐姿态的结论为准。**
 *
 * 只对源码做一处改动：在 "// 启动" 前插一段 hook 把内部函数暴露给探针，
 * 其余逐字节照跑（跑的是磁盘上那份文件，不是复制品）。
 *
 * 用法：
 *   node scripts\probe_player_orientation.js
 *   node scripts\probe_player_orientation.js path\to\some\other\player.js
 *
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
    '    globalThis.__probe = {\n' +
    '        applyRotation: applyRotation, computeExtraRotation: computeExtraRotation,\n' +
    '        totalRotation: totalRotation, imageFitRect: imageFitRect, viewportSize: viewportSize,\n' +
    '        layoutBox: layoutBox, computeGridFrame: computeGridFrame,\n' +
    '        deviceRotationDeg: deviceRotationDeg, rev: PLAYER_JS_REV,\n' +
    '        portraitExtra: PORTRAIT_EXTRA,\n' +
    '        getExtra: function () { return extraRot; },\n' +
    '        go: function () { applyRotation(); },\n' +
    '        setMapRotation: function (v) { mapRotation = v; },\n' +
    '        setGridAnchor: function (a) { gridAnchor = a; },\n' +
    '        setMapNatural: function (w, h) { mapImg.naturalWidth = w; mapImg.naturalHeight = h; },\n' +
    '        setObjectFit: function (v) { mapImg.style.objectFit = v; },\n' +
    '        setSearch: function (s) { location.search = s; },\n' +
    '    };\n' + MARK);

// ---------------- 极简 DOM 桩 ----------------
const CTX = new Proxy({}, {
    get: function () { return function () {}; },
    set: function () { return true; },
});

function makeEl(id) {
    const el = { id: id, style: {}, textContent: '', value: '', disabled: false,
        width: 1, height: 1, naturalWidth: 0, naturalHeight: 0,
        addEventListener: function () {}, removeEventListener: function () {},
        appendChild: function () {}, removeAttribute: function () {}, setAttribute: function () {},
        getContext: function () { return CTX; }, className: '', src: '' };
    // 用 style.width/height 反推布局尺寸：等价于"元素尺寸就是 JS 写进去的那个"
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
    addEventListener: function () {},
    cookie: '',
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

globalThis.window = win;
globalThis.document = documentStub;
globalThis.location = win.location;
globalThis.screen = win.screen;
globalThis.Image = function () { return makeEl('img'); };
globalThis.fetch = function () { return Promise.resolve({ json: function () { return Promise.resolve({}); } }); };

// ---------------- 跑真的 player.js ----------------
let bootError = null;
try {
    eval(src);                                    // eslint-disable-line no-eval
} catch (e) {
    bootError = e;
}

const probe = globalThis.__probe;
let failures = 0;

function check(ok, label) {
    if (!ok) { failures++; console.log('  [FAIL] ' + label); }
    else { console.log('  [PASS] ' + label); }
}

function norm(v) { return ((v % 360) + 360) % 360; }

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
check(typeof probe.computeExtraRotation === 'function', '探针 hook 已注入');
check(typeof probe.computeGridFrame === 'function', '探针 hook 能看到 computeGridFrame');

// ---------------- 用例 ----------------
// 造一个**自洽的电脑端**当基准：画布 1200x720、一格 60px（= cm/格 × 37.8）。
// 于是 halfW = 1200/(2*60) = 10 格、halfH = 720/(2*60) = 6 格 —— 这正是下发的锚点。
// 底图 1672x941 按 cover 铺进画布 → 显示尺寸 1279.4x720 → cellsX = 21.32、cellsY = 12。
// 没底图时锚点描述的是**画布本身** → cellsX = 20、cellsY = 12。
// 手机端唯一的硬指标：**格子像素 = 60 × (盒子宽 / 1200)**。
const CANVAS_PX_W = 1200, CANVAS_PX_H = 720;
const CELL_PX = 60;
const HALF_W = CANVAS_PX_W / (2 * CELL_PX);
const HALF_H = CANVAS_PX_H / (2 * CELL_PX);
const MAP_W = 1672, MAP_H = 941;
const COVER = Math.max(CANVAS_PX_W / MAP_W, CANVAS_PX_H / MAP_H);
const ANCHOR = {
    cellsX: MAP_W * COVER / CELL_PX,
    cellsY: MAP_H * COVER / CELL_PX,
    halfW: HALF_W, halfH: HALF_H,
};
const ANCHOR_NOIMG = {
    cellsX: HALF_W * 2, cellsY: HALF_H * 2,
    halfW: HALF_W, halfH: HALF_H,
};

/**
 * 摆一次场景并读回所有关心的量。
 * wo    = window.orientation（0/90/180/-90）；null 表示"不报 legacy 角，改读 angle"。
 * opt.noOrientationApi = true → 两个 API 都读不到（桌面 / 老浏览器）。
 */
function render(cw, ch, wo, mapRot, opt) {
    const o = opt || {};
    const host = documentStub.getElementById('player-map-container');
    host._cw = cw; host._ch = ch;

    if (o.noOrientationApi) {
        delete win.orientation;
        win.screen.orientation = null;
    } else {
        if (wo === null || wo === undefined) delete win.orientation;
        else win.orientation = wo;
        win.screen.orientation = {
            type: o.type || 'portrait-primary',
            angle: typeof o.angle === 'number' ? o.angle : (wo || 0),
            addEventListener: function () {},
        };
    }

    probe.setSearch(o.search || '');
    probe.setMapRotation(mapRot || 0);
    probe.setGridAnchor(o.anchor === undefined ? ANCHOR : o.anchor);
    probe.setMapNatural(o.hasImage === false ? 0 : MAP_W, o.hasImage === false ? 0 : MAP_H);
    probe.setObjectFit('cover');                   // 电脑端 "fill" 对应的就是 cover
    probe.go();

    const wrap = documentStub.getElementById('player-map-wrapper');
    const boxW = parseInt(wrap.style.width, 10) || 0;
    const boxH = parseInt(wrap.style.height, 10) || 0;
    const frame = probe.computeGridFrame();
    const fit = probe.imageFitRect(boxW, boxH);
    const extra = probe.getExtra();
    const deg = probe.deviceRotationDeg();
    // 转到 90/270 时，屏幕上的占位是宽高互换的
    const quarter = extra === 90 || extra === 270;
    const onScreenW = quarter ? boxH : boxW;
    const onScreenH = quarter ? boxW : boxH;

    return { extra: extra, total: probe.totalRotation(), deg: deg,
        boxW: boxW, boxH: boxH, onScreenW: onScreenW, onScreenH: onScreenH,
        fit: fit, transform: wrap.style.transform,
        frame: frame, layoutBox: probe.layoutBox() };
}

// 四种手持姿态：视口**跟着转**（正常手机）。
// wantExtra 就是用户的逐姿态结论（第二十三轮）。
const POSES = [
    { label: '正立（充电口朝下）',   cw: 400, ch: 800, wo: 0,   wantExtra: 90, face: '竖' },
    { label: '倒立（充电口朝上）',   cw: 400, ch: 800, wo: 180, wantExtra: 90, face: '竖' },
    { label: '向左横排（上沿朝左）', cw: 800, ch: 400, wo: 90,  wantExtra: 0,  face: '横' },
    { label: '向右横排（上沿朝右）', cw: 800, ch: 400, wo: -90, wantExtra: 0,  face: '横' },
];

console.log('');
console.log('=== 四种手持姿态（视口跟着转）===');
console.log('  姿态                     视口        wo    extra  盒子       屏幕上占位');
const poseRows = [];
for (const c of POSES) {
    const r = render(c.cw, c.ch, c.wo, 0);
    poseRows.push({ c: c, r: r });
    console.log('  ' + c.label.padEnd(24) +
        (c.cw + 'x' + c.ch).padEnd(12) + String(c.wo).padStart(4) + '  ' +
        String(r.extra).padStart(4) + '  ' +
        (r.boxW + 'x' + r.boxH).padEnd(11) +
        r.onScreenW + 'x' + r.onScreenH);
}

console.log('');
console.log('=== 断言：extra 只跟**视口方向**走（第二十三轮定稿）===');
for (const row of poseRows) {
    check(row.r.extra === row.c.wantExtra,
        row.c.label + ' : extra = ' + row.c.wantExtra + '（实际 ' + row.r.extra + '）');
}
// 用户的结论是"正立/向右横排 反了"（各差 180°），而这两组恰好是 竖屏组 / 横屏组。
// 所以关键性质是：**同一组内两个姿态必须给出同一个 extra**（不能再靠 0/180 区分）。
const upright = poseRows[0].r, upsideDown = poseRows[1].r;
const leftLand = poseRows[2].r, rightLand = poseRows[3].r;
check(upright.extra === upsideDown.extra,
    '正立 与 倒立 的 extra 相同（' + upright.extra + '）—— 竖屏组两个方向不再各转一个角度');
check(leftLand.extra === rightLand.extra,
    '向左横排 与 向右横排 的 extra 相同（' + leftLand.extra + '）—— 横屏组同理');
check(norm(upright.extra - leftLand.extra) === 90,
    '竖屏组与横屏组相差 90°（' + upright.extra + ' vs ' + leftLand.extra + '）= "长边贴长边"');
check(upright.extra === probe.portraitExtra,
    '竖屏值 = PORTRAIT_EXTRA（' + probe.portraitExtra + '）');
check(leftLand.extra === 0, '横屏值 = 0（和电脑画布那张脸一模一样）');
// 上一轮（r22）是 正立 270 / 向右横排 180 —— 用户说这两个"反了"，即要各翻 180°
check(norm(upright.extra + 180) === 270 && norm(rightLand.extra + 180) === 180,
    '对照第二十二轮（正立 270 / 向右横排 180）：新值正好各翻 180°，与用户判定吻合');

console.log('');
console.log('=== 断言：不看设备转角（视口才是判据）===');
// 同一视口、不同 wo → extra 必须完全一样
const vpPortraitA = render(400, 800, 0, 0);
const vpPortraitB = render(400, 800, 180, 0);
const vpPortraitC = render(400, 800, null, 0, { noOrientationApi: true });
check(vpPortraitA.extra === vpPortraitB.extra && vpPortraitB.extra === vpPortraitC.extra,
    '竖视口下 wo=0 / wo=180 / 读不到 API —— extra 都是 ' + vpPortraitA.extra);
const vpLandA = render(900, 500, 90, 0);
const vpLandB = render(900, 500, 270, 0);
const vpLandC = render(900, 500, null, 0, { noOrientationApi: true });
check(vpLandA.extra === vpLandB.extra && vpLandB.extra === vpLandC.extra,
    '横视口下 wo=90 / wo=270 / 读不到 API —— extra 都是 ' + vpLandA.extra);
const desktop = render(1280, 720, 0, 0);
check(desktop.extra === 0, '桌面浏览器（横视口）→ extra = 0，当电脑看（实际 ' + desktop.extra + '）');
const desktopTall = render(600, 900, 0, 0);
check(desktopTall.extra === 90, '窄高窗口（竖视口）也照样贴长边 → 90（实际 ' + desktopTall.extra + '）');

console.log('');
console.log('=== 断言：内容盒子 = 电脑画布的宽高比（halfW : halfH）===');
const wantAspect = HALF_W / HALF_H;                      // 10 / 6
const aspectBad = poseRows.filter(function (x) {
    return Math.abs(x.r.boxW / x.r.boxH - wantAspect) > 0.01;
});
check(aspectBad.length === 0,
    '所有姿态盒子宽高比 = 画布宽高比 ' + wantAspect.toFixed(4) +
    '（偏差 >0.01 的姿态数 ' + aspectBad.length + '）');
const overflow = poseRows.filter(function (x) {
    return x.r.onScreenW > x.c.cw + 1 || x.r.onScreenH > x.c.ch + 1;
});
check(overflow.length === 0, '**转过去之后**始终放得进屏幕（不裁切、不外溢）');
check(upright.boxW > 600,
    '竖屏(400x800)下的盒子不缩水：' + upright.boxW + 'x' + upright.boxH +
    '（若拿未互换的视口去量，会掉到 ~400x240）');

console.log('');
console.log('=== 断言：格子像素 = 电脑端的 60px × (盒子宽 / 画布宽) —— 疏密度与电脑完全一致 ===');
console.log('  姿态                    盒子       期望 cellPx   实际 cellPx   相对偏差');
const densityBad = [];
for (const x of poseRows) {
    const want = CELL_PX * (x.r.boxW / CANVAS_PX_W);
    const got = x.r.frame.cellW;
    const dev = Math.abs(got - want) / want;
    if (dev > 0.005) densityBad.push(x.c.label);
    console.log('  ' + x.c.label.padEnd(24) + (x.r.boxW + 'x' + x.r.boxH).padEnd(11) +
        want.toFixed(3).padStart(9) + '   ' + got.toFixed(3).padStart(9) +
        '   ' + (dev * 100).toFixed(3) + '%');
}
check(densityBad.length === 0,
    '所有姿态的格子像素都 = 电脑端的等比缩放（偏差 >0.5% 的：' +
    (densityBad.join(' / ') || '无') + '）');

const b0 = upright;
check(Math.abs(b0.frame.cellW - b0.frame.cellH) < 0.01,
    '格子始终是正方形（电脑端 cm/格 × 37.8 是正方形，这边必须跟着）');
check(Math.abs(b0.frame.ox - (-HALF_W * b0.frame.cellW)) < 0.01,
    'ox = -halfW × cellW（电脑画布左边那一格的位置）');
check(Math.abs(b0.frame.oy - (-HALF_H * b0.frame.cellH)) < 0.01,
    'oy = -halfH × cellH');
const samePattern = poseRows.every(function (x) {
    const f = x.r.frame;
    return Math.abs(f.cellW / x.r.boxW - b0.frame.cellW / b0.boxW) < 0.01;
});
check(samePattern, '所有姿态的 cellW/boxW 恒定（= 同一张图，只是整体大小/摆法不同）');
check(Math.abs(upright.frame.cellW - leftLand.frame.cellW) < 0.05,
    '竖屏 与 横屏 的格子像素相同（' + upright.frame.cellW.toFixed(3) + ' vs ' +
    leftLand.frame.cellW.toFixed(3) + '）⇒ 转屏不会改变疏密度');

console.log('');
console.log('=== 断言：视口 = 电脑画布时，手机必须与电脑**逐像素同一套** ===');
// 真实参数：电脑端画布 1200x800、2.5cm/格 → cell = 2.5 × 37.8 = 94.5px；
// 底图 地板.webp 1672x941，按 cover 铺进画布 → 显示 1421.5x800 → cellsX=15.042、cellsY=8.466。
// 「向左横排」（视口 1200x800，横）时 extra = 0 ⇒ 盒子就该是整块画布 1200x800、
// 格子 94.5px、原点 (-600,-400) —— 与电脑端 SelfCheck 出来的
// build_logs\selfcheck\02-grid-map-2.5cm-fill.png 是同一套几何。
const REAL = { canvasW: 1200, canvasH: 800, cell: 94.5, mapW: 1672, mapH: 941 };
const realCover = Math.max(REAL.canvasW / REAL.mapW, REAL.canvasH / REAL.mapH);
const realAnchor = {
    cellsX: REAL.mapW * realCover / REAL.cell,
    cellsY: REAL.mapH * realCover / REAL.cell,
    halfW: REAL.canvasW / (2 * REAL.cell),
    halfH: REAL.canvasH / (2 * REAL.cell),
};
const real = render(REAL.canvasW, REAL.canvasH, 90, 0, { anchor: realAnchor });
console.log('  向左横排 · 视口 1200x800（横）');
console.log('  extra ' + real.extra + '   盒子 ' + real.boxW + 'x' + real.boxH +
    '   cellPx ' + real.frame.cellW.toFixed(4) +
    '   原点 (' + real.frame.ox.toFixed(3) + ', ' + real.frame.oy.toFixed(3) + ')');
check(real.extra === 0, '横视口 extra = 0（就是电脑画布那张脸）');
check(real.boxW === REAL.canvasW && real.boxH === REAL.canvasH,
    '视口 = 画布时盒子就是整块画布（' + REAL.canvasW + 'x' + REAL.canvasH + '）');
check(Math.abs(real.frame.cellW - REAL.cell) < 1e-6,
    '格子像素 = 电脑端的 94.5px（实际 ' + real.frame.cellW.toFixed(6) + '）');
check(Math.abs(real.frame.ox + REAL.canvasW / 2) < 1e-6 &&
    Math.abs(real.frame.oy + REAL.canvasH / 2) < 1e-6,
    '网格原点 = 画布左上角 (-600, -400)（实际 ' +
    real.frame.ox.toFixed(3) + ', ' + real.frame.oy.toFixed(3) + '）');
check(real.frame.anchored === true, '走的是锚点这条路（与电脑端同一套几何）');

// 反过来：竖屏 800x1200（正立，extra 90）——"转过去之后"的视口同样是 1200x800，
// 所以盒子/格子/原点必须和上面**完全一致**。
const realPortrait = render(800, 1200, 0, 0, { anchor: realAnchor });
console.log('  正立 · 视口 800x1200（竖，把手机转过来看同一块画布）');
console.log('  extra ' + realPortrait.extra + '   盒子 ' + realPortrait.boxW + 'x' + realPortrait.boxH +
    '   cellPx ' + realPortrait.frame.cellW.toFixed(4));
check(realPortrait.extra === 90, '竖视口 extra = 90');
check(realPortrait.boxW === real.boxW && realPortrait.boxH === real.boxH,
    '两种拿法得到**同一块画布**（' + realPortrait.boxW + 'x' + realPortrait.boxH +
    ' = ' + real.boxW + 'x' + real.boxH + '）');
check(Math.abs(realPortrait.frame.cellW - real.frame.cellW) < 1e-6 &&
    Math.abs(realPortrait.frame.ox - real.frame.ox) < 1e-6 &&
    Math.abs(realPortrait.frame.oy - real.frame.oy) < 1e-6,
    '格子像素 / 网格原点也完全一致 ⇒ 转屏不改变画面内容，只换摆法');

console.log('');
console.log('=== 断言：没底图时也要按锚点（画布）画网格 ===');
const noImg = render(400, 800, 0, 0, { hasImage: false, anchor: ANCHOR_NOIMG });
const withImg = render(400, 800, 0, 0, { anchor: ANCHOR });
check(noImg.frame.anchored === true,
    '没底图时网格依然走"锚点"这条路（不再退回贴左上角的老网格）');
// 盒子被取整到整数像素，所以这里用**相对**容差（0.5%）
check(Math.abs(noImg.frame.cellW - withImg.frame.cellW) / withImg.frame.cellW < 0.005 &&
    Math.abs(noImg.frame.oy - withImg.frame.oy) / Math.abs(withImg.frame.oy) < 0.005,
    '没底图 / 有底图 的格子像素与原点一致（' +
    withImg.frame.cellW.toFixed(2) + ' vs ' + noImg.frame.cellW.toFixed(2) +
    'px）—— 电脑端这两者本来就是同一个 cell');
check(Math.abs(noImg.frame.cellW - CELL_PX * (noImg.boxW / CANVAS_PX_W)) < 0.01,
    '没底图时的格子也等于电脑端的等比缩放（不再"细碎"）：' +
    noImg.frame.cellW.toFixed(2) + 'px');
const noAnchor = render(400, 800, 0, 0, { anchor: null });
check(noAnchor.frame.anchored === false,
    '老版本电脑端（不下发锚点）才会退回贴左上角的等距网格');
check(Math.abs(noAnchor.boxW / noAnchor.boxH - 0.5) < 0.02,
    '没锚点时盒子退回"长边 × 短边"并按 extra 换向（400x800 竖屏 + 转90 → 盒子 400x800，' +
    '实际 ' + noAnchor.boxW + 'x' + noAnchor.boxH + '；转过去正好铺满 800x400）');

console.log('');
console.log('=== 断言：?extra= 是**叠加微调** ===');
const trim180 = render(400, 800, 0, 0, { search: '?extra=180' });
check(trim180.extra === 270, '竖视口 + ?extra=180 → 270（把两个值整体翻面）（实际 ' + trim180.extra + '）');
const trim90 = render(400, 800, 0, 0, { search: '?extra=90' });
check(trim90.extra === 180, '竖视口 + ?extra=90 → 180（实际 ' + trim90.extra + '）');
const trimLand = render(800, 400, 90, 0, { search: '?extra=180' });
check(trimLand.extra === 180, '横视口 + ?extra=180 → 180（实际 ' + trimLand.extra + '）');
const back = render(400, 800, 0, 0, { search: '' });
check(back.extra === 90, '去掉参数后回到默认 90（实际 ' + back.extra + '）');

console.log('');
console.log('=== 断言：网格/底图绑定不受 extra 影响 ===');
check(Math.abs(b0.frame.rotate - 0) < 1e-9,
    '网格自身的朝向只由 mapRotation 决定（rotate = -mapRotation），与 extra 无关');
const rot90 = render(400, 800, 0, 90);
check(Math.abs(rot90.frame.rotate + Math.PI / 2) < 1e-9,
    '电脑端把底图转了 90° 时，网格跟着转 -90°（绑定的老契约没动）');
check(rot90.total === 180,
    '电脑端转 90° + 竖视口(extra 90) ⇒ total 180（实际 ' + rot90.total + '）');

console.log('');
console.log('=== 设备转角读数（只给 ?debug=1 看，符号不能写反）===');
const WO_CASES = [
    { wo: 0, want: 0, note: '正立' },
    { wo: 90, want: 90, note: '向左横排' },
    { wo: 180, want: 180, note: '倒立' },
    { wo: -90, want: 270, note: '向右横排（顺 90° 报 -90）' },
];
for (const c of WO_CASES) {
    render(400, 800, c.wo, 0);
    check(probe.deviceRotationDeg() === c.want,
        'window.orientation=' + String(c.wo).padStart(3) + '（' + c.note + '）→ ' +
        c.want + '°（实际 ' + probe.deviceRotationDeg() + '）');
}
// screen.orientation.angle 与它**相反**（兼容规范 wo = -angle）
const ANGLE_CASES = [
    { a: 0, want: 0, note: '正立' },
    { a: 270, want: 90, note: '向左横排' },
    { a: 180, want: 180, note: '倒立' },
    { a: 90, want: 270, note: '向右横排' },
];
for (const c of ANGLE_CASES) {
    render(400, 800, null, 0, { angle: c.a });
    check(probe.deviceRotationDeg() === c.want,
        'angle=' + String(c.a).padStart(3) + '（' + c.note + '）→ ' + c.want +
        '°（实际 ' + probe.deviceRotationDeg() + '）');
}

console.log('');
console.log('=== 断言：电脑端把底图转过 90/270 时，盒子仍然放得进屏幕（回归锁）===');
// 第二十三轮抓到的真 bug：盒子整体在屏幕上转的是 total = mapRotation + extra，
// 但以前是按 extra 换向的 ⇒ 底图转着的时候 total 变成 180/0，盒子就不该换/该换，
// 结果整块溢出屏幕被裁掉（实测 667x400 塞进 400x800），底图与网格全对不上。
const rotBad = [];
for (const rot of [0, 90, 180, 270]) {
    for (const c of POSES) {
        const r = render(c.cw, c.ch, c.wo, rot);
        const t = norm(r.total);
        const qt = (t === 90 || t === 270);
        const w = qt ? r.boxH : r.boxW;
        const h = qt ? r.boxW : r.boxH;
        if (w > c.cw + 1 || h > c.ch + 1) {
            rotBad.push('底图转' + rot + '°/' + c.label + '→' + w + 'x' + h);
        }
    }
}
check(rotBad.length === 0,
    '4 种底图旋转 × 4 种手持 = 16 组，**转过去之后**都放得进屏幕（超出的：' +
    (rotBad.join('; ') || '无') + '）');

console.log('');
console.log('=== 断言：盒子比例 = 电脑"**逻辑**视口"的比例（底图转 90/270 时与屏幕换向）===');
// 电脑端转底图是"把整块画布绕中心转过去画"的，所以逻辑视口的宽高在 90/270 时是互换的；
// 锚点里的 halfW/halfH 是**屏幕**视口的，盒子必须按"逻辑视口"取。
//
// ⚠️ 电脑端算 cellsX/cellsY 用的是**逻辑视口**（MapCanvas.ComputeImageRect），
//    所以这里也得按转过的视口重建锚点 —— 拿没转过的锚点去配 mapRotation=90 是自欺欺人。
function anchorForMapRot(mapRot) {
    const r = norm(mapRot);
    const q = (r === 90 || r === 270);
    const lw = q ? CANVAS_PX_H : CANVAS_PX_W;
    const lh = q ? CANVAS_PX_W : CANVAS_PX_H;
    const cover = Math.max(lw / MAP_W, lh / MAP_H);
    return {
        cellsX: MAP_W * cover / CELL_PX,
        cellsY: MAP_H * cover / CELL_PX,
        halfW: HALF_W, halfH: HALF_H,          // halfW/halfH 用**屏幕**视口，与旋转无关
    };
}
for (const rot of [0, 90, 180, 270]) {
    const r = render(400, 800, 0, rot, { anchor: anchorForMapRot(rot) });
    const mapQ = (rot === 90 || rot === 270);
    const want = mapQ ? HALF_H / HALF_W : HALF_W / HALF_H;
    check(Math.abs(r.boxW / r.boxH - want) / want < 0.01,
        '底图转 ' + String(rot).padStart(3) + '° : 盒子比例 ' + (r.boxW / r.boxH).toFixed(4) +
        ' = 逻辑视口比例 ' + want.toFixed(4));
}

console.log('');
console.log('=== 断言：底图转过 90/270 时，格子像素仍然 = 电脑端的等比缩放 ===');
for (const rot of [0, 90, 180, 270]) {
    const r = render(400, 800, 0, rot, { anchor: anchorForMapRot(rot) });
    const mapQ = (rot === 90 || rot === 270);
    const logicalCells = mapQ ? HALF_H * 2 : HALF_W * 2;
    const want = r.boxW / logicalCells;                 // 盒子宽 ÷ 逻辑视口几格
    const dev = Math.abs(r.frame.cellW - want) / want;
    check(dev < 0.005,
        '底图转 ' + String(rot).padStart(3) + '° : cellPx ' + r.frame.cellW.toFixed(3) +
        ' ≈ 盒子宽/' + logicalCells.toFixed(2) + ' 格 = ' + want.toFixed(3) +
        '（偏差 ' + (dev * 100).toFixed(3) + '%）');
}

console.log('');
console.log('=== 断言：?box=fill 铺满屏幕（可选的取舍开关）===');
const fitBox = render(400, 800, 0, 0);
const fillBox = render(400, 800, 0, 0, { search: '?box=fill' });
const qtF = (fillBox.extra === 90 || fillBox.extra === 270);
const fw = qtF ? fillBox.boxH : fillBox.boxW;
const fh = qtF ? fillBox.boxW : fillBox.boxH;
const dw = qtF ? fitBox.boxH : fitBox.boxW;
const dh = qtF ? fitBox.boxW : fitBox.boxH;
console.log('  默认(留边) 转过去 ' + dw + 'x' + dh + ' / 视口 400x800');
console.log('  box=fill  转过去 ' + fw + 'x' + fh + ' / 视口 400x800');
check(dw <= 400 + 1 && dh <= 800 + 1, '默认：整块都看得见（放得进视口）');
check(fw >= 400 - 1 && fh >= 800 - 1, 'box=fill：两个方向都铺满（会有裁切）');
check(fillBox.boxW >= fitBox.boxW, 'box=fill 的盒子不小于默认（是"放大盖住"）');

console.log('');
console.log(failures === 0 ? 'ALL CHECKS PASSED' : failures + ' CHECK(S) FAILED');
process.exit(failures === 0 ? 0 : 1);
