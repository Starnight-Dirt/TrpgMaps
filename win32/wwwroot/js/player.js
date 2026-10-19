/*
 * 玩家视图逻辑。
 *
 * 房间：地址形态是 http://ip:port/<房间号>/player[?passwd=密码]
 *  1) 开局先问一次 /api/room：现在这个房间是几号、要不要密码；
 *  2) 地址栏里的房间号 / cookie 里存的房间号，只要有一个和它对不上，
 *     就先拿旧 token 向**旧房间**发一次退出（旧房间一般已经不在了，那也算退出），
 *     然后清掉 cookie 里的 token + 房间号，以**新玩家**重新加入；
 *  3) 房间要密码时：地址里带对了（扫码进来的就是这种）→ 登录面板只问玩家名；
 *     没带或不对 → 面板上多出一个「房间密码」输入框。
 *
 * 令牌流程：
 *  1) 第一次用某个名称进入时，不带 token；服务器颁发随机 token 并写入 cookie；
 *  2) 之后（刷新/掉线重连）带着 cookie 里的 name + token 再进来，服务器校验通过才放行；
 *  3) 别人用同一个名称进来（没有 token 或 token 不对）会被拒绝并提示名称重复。
 *
 * 房间锁定：
 *  服务器锁定房间后，只有名册里已有名称 + 正确 token 的老玩家能重连，新名称会被拒绝。
 *
 * 退出房间：右下角那个圆形悬浮按钮 → 「退出房间」。
 *  服务器确认收到 → 把这个人从名册里删掉（令牌一起作废）、并让所有人看到
 *  "xxx 离开了房间"；连不上服务器（房间已经离线）→ 本地照样算退出，清掉 cookie。
 */
(function () {
    'use strict';

    /**
     * 玩家页脚本的修订号。
     *
     * ⚠️ **改这个文件就顺手把它改掉。** 平板上加 `?debug=1` 时左下角第一行会打出来，
     * 一眼就能确认"平板跑的到底是哪一版"。浏览器缓存 + 旧 exe 里的旧 wwwroot
     * 已经骗过我们好几次了（改了 js、平板上看着却纹丝不动）。
     */
    const PLAYER_JS_REV = 'r28-2026-10-19-设置菜单与语言';
    // 顺手挂到 window 上：目视夹具（scripts\player_orientation_fixture.html）会读它，
    // 用来确认"页面上跑的到底是哪一版"。不参与任何逻辑。
    window.__playerJsRev = PLAYER_JS_REV;

    const socket = window.io();

    const loginOverlay = document.getElementById('login-overlay');
    const nameInput = document.getElementById('player-name-input');
    const loginBtn = document.getElementById('login-btn');
    const loginError = document.getElementById('login-error');
    const loginInfo = document.getElementById('login-info');
    const displayName = document.getElementById('display-player-name');
    const toolbar = document.getElementById('player-toolbar');
    const statusEl = document.getElementById('connection-status');
    const mapImg = document.getElementById('player-map-image');
    const wrapper = document.getElementById('player-map-wrapper');
    const canvas = document.getElementById('player-grid-canvas');
    const drawCanvas = document.getElementById('player-draw-canvas');
    const lockNotice = document.getElementById('room-lock-notice');
    const roomInfo = document.getElementById('room-info');
    const passInput = document.getElementById('room-pass-input');
    const fab = document.getElementById('player-fab');
    const fabMenu = document.getElementById('player-fab-menu');
    const exitBtn = document.getElementById('exit-room-btn');
    const toastEl = document.getElementById('player-toast');
    const roomNumInput = document.getElementById('room-number-input');

    // 悬浮菜单：分页（根 / 设置 / 语言）与语言列表
    const settingsBtn = document.getElementById('settings-btn');
    const languageBtn = document.getElementById('language-btn');
    const languageValue = document.getElementById('language-value');
    const languageList = document.getElementById('language-list');

    /**
     * 这一页是不是 `/join`（手填房间号的那种）。
     *
     * 页面本身和玩家页是**同一份** player.html / player.js —— `/join` 是"扫码之外的入口"，
     * 给玩家在电脑上打开、或者二维码坏了时用。它比玩家页**多**一个房间号输入框，
     * 密码框也一直亮着，所以是三个框：房间号 / 玩家名 / 密码。
     * 三个都对上之后再走**和玩家页完全一样**的流程（见 submitJoin）。
     */
    const JOIN_MODE = /\/join\/?$/i.test(location.pathname);

    const COOKIE_NAME = 'dnd_player_name';
    const COOKIE_TOKEN = 'dnd_player_token';
    const COOKIE_ROOM = 'dnd_player_room';
    const COOKIE_DAYS = 1;

    // 密码本来就在地址的 query 里（扫码进来的那种）。页面内部跳转/重排会把它丢掉，
    // 所以顺手存一份到 sessionStorage —— 只活到这个标签页关闭为止。
    const PASSWD_KEY = 'dnd_room_passwd';

    // 状态条配色。"在线"用亮绿：这个指示就是给玩家"能不能收到更新"看的，
    // 以前那种柔和的 #6a6 在一眼扫过去时和灰色没差多少。
    const ONLINE_COLOR = '#00e676';
    const OFFLINE_COLOR = '#ff6666';


    // ============================================================
    //  界面语言（网页端自己的，和 DM 电脑上的程序语言互不影响）
    //
    //  入口：悬浮按钮 → 设置 → 语言。
    //  顺序规则和电脑版一致：**系统语言置顶并标「推荐」**，其余按英文名首字母排序。
    //  和电脑版只有一点不同：这里选了就立刻生效、直接记住，
    //  没有「保留 / 取消 + 15 秒回退」的确认框 —— 网页上再弹一层太啰嗦。
    // ============================================================

    const LANG_KEY = 'dnd_player_lang';

    // 新增语言只要往这里加一行、再补一份 STRINGS。
    const LANGS = [
        { code: 'zh-cn', native: '简体中文', english: 'Chinese (Simplified)' },
        { code: 'en-us', native: 'English', english: 'English (United States)' },
    ];

    // 「推荐」用每门语言自己的写法（和电脑版的 Loc.TextIn 一致）
    const LANG_RECOMMENDED = { 'zh-cn': '（推荐）', 'en-us': ' (Recommended)' };

    const STRINGS = {
        'zh-cn': {
            'app.title': 'TrpgMaps 玩家视图',
            'login.title': '🎲 加入游戏',
            'login.locked': '🔒 房间已锁定',
            'login.room.placeholder': '房间号（8 位数字）',
            'login.name.placeholder': '输入你的玩家名称',
            'login.pass.placeholder': '房间密码',
            'login.enter': '进入',
            'map.alt': '地图',
            'common.unknown': '未知',
            'status.online': '● 在线',
            'status.offline': '● 离线',
            'fab.menu': '菜单',
            'fab.settings': '设置',
            'fab.exit': '退出房间',
            'fab.language': '语言',
            'fab.back': '← 返回',

            'login.fill': '请填写房间号{0}，以及你的玩家名称',
            'login.fill.passpart': '与房间密码',
            'login.room.invalid': '房间号是 8 位数字',
            'login.room.notfound': '房间不存在',
            'login.room.check': '请核对 DM 屏幕上显示的 8 位房间号',
            'login.room.gone': '房间已不在（房间号已更换）',
            'login.pass.wrong': '房间密码错误',
            'login.room.needpass': '房间 {0} 需要正确的密码',
            'login.room.needpass2': '房间号 {0} 需要密码',
            'login.netfail': '连不上服务器，请检查网络',
            'login.needname': '请输入名称',
            'login.needpass': '请输入房间密码',
            'login.failed': '登录失败',
            'login.nametaken': '该名称已被占用；如果这是你本人，请用同一浏览器重新进入',
            'login.tokenmismatch': '身份校验失败（令牌不匹配），该名称已被占用',
            'login.roomlocked': '房间已锁定，暂不接受新玩家加入',
            'login.locked.info': '房间已锁定，只有原有玩家可以用原名称重连',
            'login.busy.room': '正在查找房间...',
            'login.busy.enter': '正在进入...',
            'login.busy.connect': '正在连接...',
            'login.busy.auto': '正在自动登录...',
            'login.hint': '请输入你的名称',
            'login.hint.saved': '请输入名称后进入',
            'login.rejoin': '{0}　点「进入」重新加入。',

            'room.updated': '房间已更新（{0} → {1}）',
            'room.number': '房间号 {0}',
            'room.switching': '房间已更换，正在退出旧房间…',
            'room.left.old': '已退出旧房间 {0}，正在加入房间 {1}',

            'exit.doing': '正在退出…',
            'exit.done': '已退出房间',
            'exit.gone': '房间已不在，已在本地退出',
            'exit.offline': '房间已离线，已在本地退出',

            'toast.left': '{0} 离开了房间',
            'toast.anon': '有玩家',
            'kick.alert': '你已被 DM 踢出游戏',
            'kick.info': '可用原名称重新进入',
            'kick.error': '你已被DM踢出游戏',
            'server.moved': '服务器地址已更新，正在重新连接...',
        },

        'en-us': {
            'app.title': 'TrpgMaps Player View',
            'login.title': '🎲 Join the game',
            'login.locked': '🔒 Room locked',
            'login.room.placeholder': 'Room number (8 digits)',
            'login.name.placeholder': 'Your player name',
            'login.pass.placeholder': 'Room password',
            'login.enter': 'Enter',
            'map.alt': 'Map',
            'common.unknown': 'Unknown',
            'status.online': '● Online',
            'status.offline': '● Offline',
            'fab.menu': 'Menu',
            'fab.settings': 'Settings',
            'fab.exit': 'Leave room',
            'fab.language': 'Language',
            'fab.back': '← Back',

            'login.fill': 'Enter the room number{0} and your player name',
            'login.fill.passpart': ' and password',
            'login.room.invalid': 'The room number has 8 digits',
            'login.room.notfound': 'Room not found',
            'login.room.check': 'Check the 8-digit room number shown on the DM screen',
            'login.room.gone': 'The room is gone (its number changed)',
            'login.pass.wrong': 'Wrong room password',
            'login.room.needpass': 'Room {0} needs the correct password',
            'login.room.needpass2': 'Room {0} requires a password',
            'login.netfail': "Can't reach the server — check your network",
            'login.needname': 'Enter a name',
            'login.needpass': 'Enter the room password',
            'login.failed': 'Sign-in failed',
            'login.nametaken': "That name is taken. If it's you, re-join from the same browser",
            'login.tokenmismatch': 'Identity check failed (token mismatch); that name is already taken',
            'login.roomlocked': "The room is locked and isn't accepting new players",
            'login.locked.info': 'Room is locked; only existing players can re-join with the same name',
            'login.busy.room': 'Looking up the room...',
            'login.busy.enter': 'Joining...',
            'login.busy.connect': 'Connecting...',
            'login.busy.auto': 'Signing in automatically...',
            'login.hint': 'Enter your name',
            'login.hint.saved': 'Enter your name to continue',
            'login.rejoin': '{0} — choose Enter to join again.',

            'room.updated': 'Room updated ({0} → {1})',
            'room.number': 'Room {0}',
            'room.switching': 'The room changed — leaving the old one…',
            'room.left.old': 'Left room {0}; joining room {1}',

            'exit.doing': 'Leaving…',
            'exit.done': 'Left the room',
            'exit.gone': 'The room is gone; left locally',
            'exit.offline': 'The room is offline; left locally',

            'toast.left': '{0} left the room',
            'toast.anon': 'A player',
            'kick.alert': 'The DM removed you from the game',
            'kick.info': 'You can re-join with the same name',
            'kick.error': 'You were removed by the DM',
            'server.moved': 'Server address changed — reconnecting...',
        },
    };

    /** zh-* → zh-cn、en-* → en-us，其余按中文。 */
    function normalizeLang(code) {
        const v = String(code || '').toLowerCase().replace('_', '-');
        if (v.indexOf('zh') === 0) return 'zh-cn';
        if (v.indexOf('en') === 0) return 'en-us';
        return 'zh-cn';
    }

    /** 这台设备/浏览器的系统语言（下拉框里标「推荐」的那一项）。 */
    function systemLang() {
        return normalizeLang(navigator.language || navigator.userLanguage || 'zh-cn');
    }

    /** 语言列表：系统语言置顶，其余按英文名首字母排序（与电脑版同一套规则）。 */
    function langOrder() {
        const recommended = systemLang();
        const first = [];
        const rest = [];
        LANGS.forEach(function (lang) {
            if (lang.code === recommended) first.push(lang);
            else rest.push(lang);
        });
        rest.sort(function (a, b) {
            return a.english < b.english ? -1 : (a.english > b.english ? 1 : 0);
        });
        return first.concat(rest);
    }

    function nativeName(code) {
        for (let i = 0; i < LANGS.length; i++) {
            if (LANGS[i].code === normalizeLang(code)) return LANGS[i].native;
        }
        return code;
    }

    // 用函数做替换，避免把玩家名里的 $& / $1 当成替换模式
    function fill(text, a, b) {
        return String(text)
            .replace(/\{0\}/g, function () { return a === undefined || a === null ? '' : String(a); })
            .replace(/\{1\}/g, function () { return b === undefined || b === null ? '' : String(b); });
    }

    function T(key, a, b) {
        const table = STRINGS[uiLang] || STRINGS['zh-cn'];
        let text = table[key];
        if (text === undefined) text = STRINGS['zh-cn'][key];
        if (text === undefined) text = key;
        return fill(text, a, b);
    }

    /**
     * `?lang=en-us` / `?lang=zh-CN`：只是**首次默认值**。
     * 本机已经在菜单里选过就以那次为准（否则设了英文、刷新一下又被地址改回去）。
     * 用途：给英语玩家发一条现成的链接；自动化检查也靠它切语言。
     */
    function readLangFromQuery() {
        const m = /[?&]lang=([^&]*)/.exec(location.search || '');
        if (!m) return '';
        try { return decodeURIComponent(m[1]); } catch (e) { return m[1]; }
    }

    let uiLang = normalizeLang(readLangFromQuery() || systemLang());
    try {
        const savedLang = localStorage.getItem(LANG_KEY);
        if (savedLang) uiLang = normalizeLang(savedLang);
    } catch (e) { /* 隐私模式下 localStorage 会抛 */ }

    // 动态提示的"最后状态"：记的是**键**而不是已经翻译好的文字，
    // 这样切语言时能整段重放一遍，不会中英混着。
    let infoState = null;
    let errorState = null;
    let roomState = null;

    function setInfo(key, a, b) {
        if (!key) { infoState = null; loginInfo.textContent = ''; return; }
        infoState = { key: key, a: a, b: b };
        loginInfo.textContent = T(key, a, b);
    }

    function setError(key, a, b) {
        if (!key) { errorState = null; loginError.textContent = ''; return; }
        errorState = { key: key, a: a, b: b };
        loginError.textContent = T(key, a, b);
    }

    /** 服务器原话（已经是中文/后端决定），没法跟着切语言。 */
    function setErrorRaw(text) {
        if (!text) { errorState = null; loginError.textContent = ''; return; }
        errorState = { raw: text };
        loginError.textContent = text;
    }

    function renderInfo() {
        if (!infoState) { loginInfo.textContent = ''; return; }
        if (infoState.raw) { loginInfo.textContent = infoState.raw; return; }
        if (infoState.exitKey) {
            loginInfo.textContent = T('login.rejoin', T(infoState.exitKey, infoState.a, infoState.b));
            return;
        }
        loginInfo.textContent = T(infoState.key, infoState.a, infoState.b);
    }

    function renderError() {
        if (!errorState) { loginError.textContent = ''; return; }
        if (errorState.raw) { loginError.textContent = errorState.raw; return; }
        loginError.textContent = T(errorState.key, errorState.a, errorState.b);
    }

    /** 登录失败的 code → 本地文案（服务器那句 msg 是中文，不能直接拿来用）。 */
    function loginFailureText(code, serverMsg) {
        switch (code) {
            case 'NAME_TAKEN': return T('login.nametaken');
            case 'TOKEN_MISMATCH': return T('login.tokenmismatch');
            case 'EMPTY_NAME': return T('login.needname');
            case 'ROOM_LOCKED': return T('login.roomlocked');
            case 'ROOM_GONE': return T('login.room.gone');
            case 'BAD_PASSWORD': return T('login.pass.wrong');
            default: return serverMsg || T('login.failed');
        }
    }

    function refreshConnectionStatus() {
        if (!statusEl) return;
        statusEl.textContent = socket.connected ? T('status.online') : T('status.offline');
        statusEl.style.color = socket.connected ? ONLINE_COLOR : OFFLINE_COLOR;
    }

    /** 把当前页面上所有静态文字刷成 uiLang。 */
    function applyI18n() {
        document.title = T('app.title');
        document.documentElement.lang = uiLang === 'zh-cn' ? 'zh-CN' : 'en-US';

        // 用 Array.prototype.forEach.call：老 iPad Safari 上 NodeList 还没有 forEach
        Array.prototype.forEach.call(document.querySelectorAll('[data-i18n]'), function (el) {
            el.textContent = T(el.getAttribute('data-i18n'));
        });
        Array.prototype.forEach.call(document.querySelectorAll('[data-i18n-placeholder]'), function (el) {
            el.setAttribute('placeholder', T(el.getAttribute('data-i18n-placeholder')));
        });
        Array.prototype.forEach.call(document.querySelectorAll('[data-i18n-alt]'), function (el) {
            el.setAttribute('alt', T(el.getAttribute('data-i18n-alt')));
        });

        if (fab) fab.setAttribute('aria-label', T('fab.menu'));
        if (languageValue) languageValue.textContent = nativeName(uiLang);
        if (displayName) displayName.textContent = playerName || T('common.unknown');
        if (exitBtn && !exitBtn.disabled) exitBtn.textContent = T('fab.exit');

        renderInfo();
        renderError();
        if (roomState && roomInfo) roomInfo.textContent = T(roomState.key, roomState.a, roomState.b);

        refreshConnectionStatus();
        renderLangList();
    }

    /** 选一门语言：立刻生效 + 记住；没有确认框，也没有回退。 */
    function setLanguage(code) {
        uiLang = normalizeLang(code);
        try { localStorage.setItem(LANG_KEY, uiLang); } catch (e) { /* 隐私模式 */ }
        applyI18n();
        showMenuPage('language');     // 留在语言页，能立刻看到勾选挪到哪一行
    }

    function renderLangList() {
        if (!languageList) return;
        languageList.innerHTML = '';
        langOrder().forEach(function (lang, index) {
            const btn = document.createElement('button');
            btn.className = 'fab-item lang-item' + (lang.code === uiLang ? ' active' : '');
            btn.setAttribute('data-lang', lang.code);

            const label = document.createElement('span');
            label.textContent = lang.native;
            if (index === 0) {
                const rec = document.createElement('span');
                rec.className = 'lang-recommended';
                rec.textContent = LANG_RECOMMENDED[lang.code] || '';
                label.appendChild(rec);
            }

            const check = document.createElement('span');
            check.className = 'lang-check';
            check.textContent = '✓';

            btn.appendChild(label);
            btn.appendChild(check);
            btn.addEventListener('click', function () { setLanguage(lang.code); });
            languageList.appendChild(btn);
        });
    }

    let playerName = '';
    let loggedIn = false;
    let loginBusy = false;

    // ---------- 房间 ----------
    // roomInUrl    : 地址栏路径里的房间号（玩家扫的那个码里带的）
    // serverRoom   : 服务器**当前**的房间号（每次启动都换一个新的）
    // needPassword : 服务器说这个房间要密码
    // roomPassword : 手上有的密码（来自 ?passwd= 或玩家手输）
    let roomInUrl = '';
    let serverRoom = '';
    let needPassword = false;
    let roomPassword = '';
    let passwordRejected = false;   // 上一次登录被服务器判"密码不对"
    let roomReady = false;          // /api/room 问完了没有
    let roomRetried = false;        // ROOM_GONE 之后只自动重来一次，免得死循环
    let exited = false;             // 已经退出房间（再点"进入"应该是刷新页面重新加入）

    // `?debug=1` 那个读数面板的刷新函数（setupOrientationDebug 填进来）。
    // 房间信息是异步拿到的，拿到之后要立刻让它刷一次 —— 否则面板上会先
    // 挂着"服务器 (未取到)"，真机取证时看着像出了问题。
    let refreshDebugPanel = null;

    // 拖动/缩放状态
    let scale = 1;
    let translateX = 0;
    let translateY = 0;
    let lastTouchDist = 0;
    let lastTouchX = 0;
    let lastTouchY = 0;
    let isDragging = false;

    // 电脑端设定的底图顺时针角度（0/90/180/270），随 map_change / request_map 下发
    let mapRotation = 0;

    // 网格线颜色：'white' / 'black'。由电脑端按底图平均亮度判定后随载荷下发，
    // 手机端不自己判断（见 map_change 里的说明）。
    let gridColor = 'white';

    // 底图显示模式：'fill'（放大盖满、保持比例）或 'contain'（缩到全部放进）。
    // 规则由电脑端下发，手机端只负责照着摆 —— 两端规则只要有一点不一样，
    // 网格和底图就会错位。
    let mapFitMode = 'fill';

    // 电脑端下发的「网格 ↔ 底图」锚点（电脑端见 Core/GridAnchor.cs）：
    //   { cellsX, cellsY, halfW, halfH }
    // 这 4 个数就定死了"第几格该贴在底图的哪一块"，与两端的屏幕尺寸都无关，
    // 所以手机那一格多大都无所谓，位置一定对得上。
    let gridAnchor = null;

    // 当前网格坐标系（drawPlayerGrid 里算好，绘图层复用同一份，否则两者会对不齐）。
    // 结构见 computeGridFrame()。
    let gridFrame = null;

    // ---------- Cookie 工具（正确处理中文） ----------
    function getCookie(name) {
        const value = `; ${document.cookie}`;
        const parts = value.split(`; ${name}=`);
        if (parts.length === 2) {
            const encoded = parts.pop().split(';').shift();
            if (!encoded) return null;
            try {
                return decodeURIComponent(encoded);
            } catch {
                return encoded;
            }
        }
        return null;
    }

    function setCookie(name, value, days) {
        const expires = new Date(Date.now() + days * 864e5).toUTCString();
        document.cookie = name + '=' + encodeURIComponent(value) + '; expires=' + expires + '; path=/';
    }

    /**
     * POST 一个 JSON 并等回包，可选超时。
     *
     * 超时用 `Promise.race` 而不是 `AbortController`：老一点的 iPad Safari 上
     * 前者一定有。**退出房间**那一步尤其需要它 —— 房间已经离线时 fetch 会一直挂着，
     * 玩家点了"退出"却半天没反应，比直接算他退出更糟。
     */
    function postWithTimeout(url, body, timeoutMs) {
        const request = fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body || {}),
            cache: 'no-store',
        }).then((res) => res.json().catch(() => ({})));

        if (!timeoutMs) return request;

        return Promise.race([
            request,
            new Promise((_, reject) => {
                setTimeout(() => reject(new Error('timeout')), timeoutMs);
            }),
        ]);
    }

    function clearIdentity() {
        document.cookie = COOKIE_NAME + '=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/';
        document.cookie = COOKIE_TOKEN + '=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/';
        document.cookie = COOKIE_ROOM + '=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/';
    }

    /**
     * 只清 token 与房间号，**留着玩家名**。
     *
     * 房间换号 / 主动退出之后多半还是同一个人接着玩，名字留着能少打一次。
     * （规定要求清掉的就是 token 和房间号这两样。）
     */
    function clearRoomIdentity() {
        document.cookie = COOKIE_TOKEN + '=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/';
        document.cookie = COOKIE_ROOM + '=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/';
    }

    // ---------- 房间号 / 房间密码 ----------

    /** 从地址路径里读房间号：`/<数字>/player`。老地址（`/player`）读不到，给空串。 */
    function readRoomFromPath() {
        const m = /\/(\d{6,12})\/player\/?$/i.exec(location.pathname);
        return m ? m[1] : '';
    }

    /** `?passwd=` 里的密码（扫码进来时带的那个）。 */
    function readPasswdFromQuery() {
        const m = /[?&]passwd=([^&]*)/.exec(location.search);
        if (!m) return '';
        try {
            return decodeURIComponent(m[1].replace(/\+/g, ' '));
        } catch (e) {
            return m[1];
        }
    }

    /**
     * 密码的来源：地址里的 query 优先，其次本标签页之前存下的那份。
     *
     * ⚠️ 页面里有些跳转（比如服务器换地址之后的整页刷新）会把 query 丢掉，
     * 所以不能只在开局读一次地址。
     */
    function loadStoredPasswd() {
        const fromQuery = readPasswdFromQuery();
        if (fromQuery) {
            roomPassword = fromQuery;
            try { sessionStorage.setItem(PASSWD_KEY, fromQuery); } catch (e) { /* 隐私模式 */ }
            return;
        }
        try { roomPassword = sessionStorage.getItem(PASSWD_KEY) || ''; } catch (e) { roomPassword = ''; }
    }

    /** 密码框：`/join` 页一直亮着；玩家页只在"房间要密码 + 手上那份肯定不对"时出现。 */
    function updatePasswordField() {
        const show = JOIN_MODE || (needPassword && (!roomPassword || passwordRejected));
        passInput.style.display = show ? 'block' : 'none';
    }

    /** 房间信息那一行（键 + 参数，切语言时能重放）。 */
    function showRoomInfo(key, warn, a, b) {
        if (!roomInfo) return;
        if (!key) {
            roomState = null;
            roomInfo.textContent = '';
            roomInfo.style.display = 'none';
            roomInfo.className = '';
            return;
        }
        roomState = { key: key, a: a, b: b };
        roomInfo.textContent = T(key, a, b);
        roomInfo.style.display = 'block';
        roomInfo.className = warn ? 'warn' : '';
    }

    /**
     * 开局跟服务器对一次房间：
     *   ① 问 <c>/api/room</c>：现在这个房间是几号、要不要密码；
     *   ② 地址栏里的号对不上 → 地址过期了，悄悄改成当前号（页面不用重载）；
     *   ③ cookie 里的号对不上 → 身上那份身份属于**另一个房间**：
     *      先拿旧 token 向旧房间告辞，再清掉 token + 房间号，以新玩家重新加入。
     */
    function initRoom() {
        roomInUrl = readRoomFromPath();
        loadStoredPasswd();

        return fetch('/api/room', { cache: 'no-store' })
            .then((res) => res.json())
            .then((data) => {
                serverRoom = String((data && data.room) || '');
                needPassword = !!(data && data.needPassword);
                updatePasswordField();

                // `/join` 页没有房间号可对（号码是玩家马上要手填的），所以这里**不**改地址、
                // 也**不**急着向旧房间告辞 —— 万一他只是打开看一眼就走了呢。
                // 告辞推迟到 submitJoin 真正要加入新房间的那一刻。
                if (JOIN_MODE) {
                    if (loginInfo && !loginInfo.textContent) {
                        setInfo('login.fill', needPassword ? T('login.fill.passpart') : '');
                    }
                    return null;
                }

                const cookieRoom = getCookie(COOKIE_ROOM) || '';

                if (serverRoom && roomInUrl && roomInUrl !== serverRoom) {
                    showRoomInfo('room.updated', true, roomInUrl, serverRoom);
                    roomInUrl = serverRoom;
                    try {
                        history.replaceState(null, '',
                            '/' + serverRoom + '/player' + location.search + location.hash);
                    } catch (e) { /* 有些环境不让改写地址，不影响进入 */ }
                }

                if (serverRoom && cookieRoom && cookieRoom !== serverRoom) {
                    return leaveOldRoom(cookieRoom, serverRoom);
                }
                if (serverRoom && !needPassword) {
                    showRoomInfo('room.number', false, serverRoom);
                }
                return null;
            })
            .catch(() => null)   // 拿不到房间信息就按"不知道"继续（老服务端也不会回这个接口）
            .then(() => {
                roomReady = true;
                if (refreshDebugPanel) refreshDebugPanel();   // 让 `?debug=1` 那行立刻显示真实房间
            });
    }

    /**
     * 向**旧房间**告辞（尽力而为）。
     *
     * 旧房间多半已经不在了（DM 重启就换号），这次请求会拿到 ROOM_GONE，
     * 或者干脆连不上（连服务器都换地址了）。两种都算"已经退出" ——
     * 我们只是尽到告知义务，然后把本地身份清干净、以新玩家重新加入。
     */
    function leaveOldRoom(oldRoom, newRoom) {
        const name = getCookie(COOKIE_NAME) || '';
        const token = getCookie(COOKIE_TOKEN) || '';

        if (!name) {
            clearRoomIdentity();
            return null;
        }

        showRoomInfo('room.switching', true);
        return postWithTimeout('/api/leave', { room: oldRoom, name: name, token: token }, 4000)
            .catch(() => null)
            .then(() => {
                clearRoomIdentity();
                showRoomInfo('room.left.old', false, oldRoom, newRoom);
            });
    }

    // ---------- 登录 ----------
    function attemptLogin(name) {
        // 只有名称与 cookie 记录一致时才带 token，避免换个名字复用别人的令牌
        const savedName = getCookie(COOKIE_NAME);
        const savedToken = getCookie(COOKIE_TOKEN);
        const token = (savedName && savedToken && savedName === name) ? savedToken : '';
        loginBusy = true;
        socket.emit('player_login', {
            name: name,
            token: token,
            // 房间号：优先用服务器刚告诉我们的那个（地址栏里的可能已经过期）
            room: serverRoom || roomInUrl,
            passwd: roomPassword || '',
        });
    }

    function setBusy(busy, key, a) {
        loginBtn.disabled = busy;
        if (key) setInfo(key, a);
        else { infoState = null; loginInfo.textContent = ''; }
    }

    /**
     * `/join` 页的"进入"：先让服务器**认房间号和密码**，认了再走和玩家页一样的登录。
     *
     * 两种错要分开报（"房间号不对"和"密码不对"是两回事），所以先问
     * `/api/room?room=&passwd=` —— 它只回答"在不在、对不对"，**不回密码本身**。
     */
    function submitJoin(name) {
        const room = (roomNumInput ? roomNumInput.value : '').trim();
        if (!/^\d{6,12}$/.test(room)) {
            setError('login.room.invalid');
            try { roomNumInput.focus(); } catch (e) { /* 忽略 */ }
            return;
        }

        const typedPasswd = (passInput.value || '').trim();
        const passwd = typedPasswd || roomPassword || '';
        let accepted = false;

        setError('');
        setBusy(true, 'login.busy.room');

        fetch('/api/room?room=' + encodeURIComponent(room) + '&passwd=' + encodeURIComponent(passwd),
              { cache: 'no-store' })
            .then((res) => res.json())
            .then((data) => {
                if (!data || data.exists !== true) {
                    setBusy(false, '');
                    setError('login.room.notfound');
                    showRoomInfo('login.room.check', true);
                    return null;
                }
                if (data.passwordOk !== true) {
                    setBusy(false, '');
                    setError('login.pass.wrong');
                    showRoomInfo('login.room.needpass', true, room);
                    try { passInput.focus(); } catch (e) { /* 忽略 */ }
                    return null;
                }

                // 房间认了：把这一套记成"本页当前的房间"，之后的登录 / 退出 / 重连
                // 与扫码进来的玩家**完全一样**。
                accepted = true;
                serverRoom = String(data.room || room);
                roomInUrl = serverRoom;
                needPassword = !!data.needPassword;
                roomPassword = passwd;
                if (typedPasswd) {
                    try { sessionStorage.setItem(PASSWD_KEY, passwd); } catch (e) { /* 忽略 */ }
                }
                updatePasswordField();
                showRoomInfo('room.number', false, serverRoom);
                roomReady = true;

                // 身上那份身份属于别的房间 → 先向旧房间告辞（与玩家页同一套规矩）。
                const cookieRoom = getCookie(COOKIE_ROOM) || '';
                if (cookieRoom && cookieRoom !== serverRoom) {
                    return leaveOldRoom(cookieRoom, serverRoom);
                }
                return null;
            })
            .then(() => {
                if (!accepted || exited) return;
                setBusy(true, 'login.busy.enter');
                attemptLogin(name);
            })
            .catch(() => {
                setBusy(false, '');
                setError('login.netfail');
            });
    }

    loginBtn.addEventListener('click', () => {
        // 已经退过房了：这条 SSE 连接已经关掉，得整页重来一次才能重新加入
        if (exited) {
            location.reload();
            return;
        }

        const name = nameInput.value.trim();
        if (!name) {
            setError('login.needname');
            return;
        }

        // `/join` 页：房间号是手填的，得先让服务器认这个房间和密码（见 submitJoin）。
        if (JOIN_MODE) {
            submitJoin(name);
            return;
        }

        if (needPassword && passInput.style.display !== 'none' && !passInput.value.trim()) {
            setError('login.needpass');
            try { passInput.focus(); } catch (e) { /* 忽略 */ }
            return;
        }
        // 玩家在密码框里打了字就以它为准（覆盖地址里带的那份）
        if (passInput.style.display !== 'none' && passInput.value.trim()) {
            roomPassword = passInput.value.trim();
            try { sessionStorage.setItem(PASSWD_KEY, roomPassword); } catch (e) { /* 忽略 */ }
        }

        setError('');
        setBusy(true, 'login.busy.connect');
        attemptLogin(name);
    });

    if (roomNumInput) {
        roomNumInput.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') loginBtn.click();
        });
    }

    nameInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') loginBtn.click();
    });

    passInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') loginBtn.click();
    });

    socket.on('login_success', (data) => {
        // 服务器可能颁发新 token（首次加入），一定写回 cookie
        if (data && data.token) setCookie(COOKIE_TOKEN, data.token, COOKIE_DAYS);
        if (data && data.name) setCookie(COOKIE_NAME, data.name, COOKIE_DAYS);
        // 房间号也存进 cookie：下次打开页面就能立刻看出"身上这份身份是不是
        // 属于当前房间"，对不上就先向旧房间告辞（见 initRoom）。
        if (data && data.room) setCookie(COOKIE_ROOM, data.room, COOKIE_DAYS);
        else if (serverRoom) setCookie(COOKIE_ROOM, serverRoom, COOKIE_DAYS);

        // `/join` 页进来的人：地址栏还是 `/join`，把它换成正经的 `/<房间号>/player`。
        // 这样刷新、退出、被踢之后的流程与**扫码进来的玩家完全一致**；
        // 密码跟着带上，刷新时连密码框都不用再填。
        if (JOIN_MODE) {
            const room = (data && data.room) || serverRoom;
            if (room) {
                try {
                    history.replaceState(null, '', '/' + room + '/player' +
                        (roomPassword ? '?passwd=' + encodeURIComponent(roomPassword) : ''));
                } catch (e) { /* 有些环境不让改写地址，不影响进入 */ }
            }
        }

        playerName = (data && data.name) || nameInput.value.trim();
        displayName.textContent = playerName;
        loginOverlay.style.display = 'none';
        toolbar.style.display = 'flex';
        setFabVisible(true);
        setError('');
        setBusy(false, '');
        loggedIn = true;
        loginBusy = false;
        passwordRejected = false;
        updateFabForLogin();
        applyRoomLock(data ? data.roomLocked : false);
        socket.emit('request_map');
    });

    socket.on('login_failed', (data) => {
        const code = data && data.code;
        loginBusy = false;

        // 密码不对：把密码框亮出来（多半是地址里的密码过期了，或玩家手输了错）
        if (code === 'BAD_PASSWORD') {
            passwordRejected = true;
            updatePasswordField();
            setBusy(false, '');
            setError('login.pass.wrong');
            showRoomInfo('login.room.needpass2', true, serverRoom || roomInUrl);
            try { passInput.focus(); } catch (e) { /* 忽略 */ }
            return;
        }

        // 后端给的 msg 是中文，切了语言也没法变 —— 已知 code 一律用本地文案
        setErrorRaw(loginFailureText(code, data && data.msg));
        setBusy(false, '');
        applyRoomLock(code === 'ROOM_LOCKED');

        // 房间号在"刚问完 /api/room"之后又变了（DM 恰好在这一瞬间重启）。
        // 重问一次房间信息再试，只自动试一次。
        if (code === 'ROOM_GONE' && !roomRetried) {
            roomRetried = true;
            clearRoomIdentity();
            roomReady = false;
            initRoom().then(() => { if (roomReady && !loggedIn && !exited) autoLogin(); });
            return;
        }

        // 名称被占用或令牌失效：清掉本地身份，避免一直重试同一个错误
        if (code === 'TOKEN_MISMATCH' || code === 'NAME_TAKEN' || code === 'COOKIE_CONFLICT') {
            clearIdentity();
        }
    });

    socket.on('room_state', (data) => {
        applyRoomState(data || {});
    });

    /** 房间状态的两种开关都要跟着走：锁定 + 要不要密码。 */
    function applyRoomState(state) {
        applyRoomLock(!!state.locked);
        if (typeof state.needPassword === 'boolean') {
            needPassword = state.needPassword;
            updatePasswordField();
        }
    }

    function applyRoomLock(locked) {
        if (lockNotice) lockNotice.style.display = locked ? 'block' : 'none';
        if (!loggedIn && locked && !loginError.textContent) {
            setInfo('login.locked.info');
        }
    }

    // ---------- 自动重连 ----------
    function autoLogin() {
        if (loggedIn || exited || loginBusy) return;

        // 房间信息还没拿到就先别登：地址栏里的房间号可能是过期的，
        // 拿它去登录会被判 ROOM_GONE，白白多一次往返（initRoom 完了会再叫一次）。
        if (!roomReady) return;

        const savedName = getCookie(COOKIE_NAME);
        const savedToken = getCookie(COOKIE_TOKEN);

        if (savedName && savedToken) {
            nameInput.value = savedName;
            setBusy(true, 'login.busy.auto');
            attemptLogin(savedName);
        } else {
            if (savedName) nameInput.value = savedName;
            if (!loginInfo.textContent) {
                setInfo(savedName ? 'login.hint.saved' : 'login.hint');
            }
        }
    }

    /**
     * 已经登录过的玩家重连之后**再登录一次**（静默，不弹遮罩）。
     *
     * 服务器把"这名玩家在线"绑在当前的 SSE 连接上：连接一断就被标成离线，
     * 重连换的是一条全新的连接，光把右上角改成"在线"而不重新登录的话，
     * DM 那边的绿点会一直是灰的 —— 以前必须玩家手动刷新整个页面才会恢复。
     */
    function relogin() {
        const name = playerName || getCookie(COOKIE_NAME);
        if (!name) return;
        attemptLogin(name);
    }

    // ---------- 地图 ----------
    socket.on('map_change', (data) => {
        if (!data) return;

        // 电脑端在底图上设定的顺时针角度（0/90/180/270）
        if (typeof data.rotation === 'number' && isFinite(data.rotation)) {
            mapRotation = (((data.rotation % 360) + 360) % 360);
        }

        // 网格线颜色：电脑端已经按底图明暗判定好了，这边照抄就行。
        // 为什么不在手机上自己判断：手机要把 3MB 的底图解一遍才能算平均亮度，
        // 又慢又费电；而且万一手电脑判得不一样，玩家看到的网格就和 DM 的对不上了。
        if (data.gridColor === 'white' || data.gridColor === 'black') {
            gridColor = data.gridColor;
        }

        // 显示模式：电脑端的 "fill" 是"放大到盖满再裁掉多余"（保持比例），
        // 不是 CSS 那个会把图拉变形的 fill —— 映射见 applyRotation()。
        if (data.mode) {
            mapFitMode = data.mode === 'contain' ? 'contain' : 'fill';
        }

        // 绘图版本号变了才去拉内容。电脑端一笔画几十格，
        // 把整份绘图塞进事件流太重，这里只传版本号。
        if (typeof data.drawingVersion === 'number' && data.drawingVersion !== drawingVersion) {
            fetchDrawing();
        }

        // 网格与底图的绑定：电脑端把"底图能放几格 / 画布半宽是几格"算好发下来，
        // 这边照着重放（见 computeGridFrame）。**没有底图时这几个数照样发**
        // —— 那时它们描述的是画布本身 —— 所以不再跟 data.url 绑在一起。
        // 只有真正少了这几个数（老版本电脑端）才退回老网格。
        if (typeof data.gridCellsX === 'number' && data.gridCellsX > 0 &&
            typeof data.gridCellsY === 'number' && data.gridCellsY > 0 &&
            typeof data.gridHalfW === 'number' && data.gridHalfW > 0 &&
            typeof data.gridHalfH === 'number' && data.gridHalfH > 0) {
            gridAnchor = {
                cellsX: data.gridCellsX,
                cellsY: data.gridCellsY,
                halfW: data.gridHalfW,
                halfH: data.gridHalfH,
            };
        } else {
            gridAnchor = null;
        }

        if (data.url) {
            mapImg.src = data.url;
        } else {
            // 电脑端清掉了底图：手机上也别继续挂着上一张。
            // 去掉 src 之后 naturalWidth 归零，computeGridFrame 于是按"画布"画网格。
            mapImg.removeAttribute('src');
        }

        applyRotation();
    });

    mapImg.addEventListener('load', drawPlayerGrid);

    /**
     * 底图在 wrapper 里**实际占的矩形**（object-fit 之后的尺寸和位置）。
     *
     * 必须和电脑端的摆放规则逐条对应，否则锚点就对不上：
     *   电脑端 fill    = 放大到盖满整个视口（保持比例，超出的裁掉）→ CSS 的 cover
     *   电脑端 contain = 缩到全部放进视口（保持比例）              → CSS 的 contain
     * 原来这里把电脑端的 fill 原样交给了 CSS，而 CSS 的 fill 是**不等比拉伸**，
     * 底图会被拉变形 —— 这是手机上位置对不上的原因之一。
     */
    function imageFitRect(boxW, boxH) {
        const nw = mapImg.naturalWidth;
        const nh = mapImg.naturalHeight;
        if (!nw || !nh) return null;

        const contain = mapImg.style.objectFit === 'contain';
        const s = contain ? Math.min(boxW / nw, boxH / nh) : Math.max(boxW / nw, boxH / nh);

        const w = nw * s;
        const h = nh * s;
        return { x: (boxW - w) / 2, y: (boxH - h) / 2, w: w, h: h };
    }

    /**
     * 算出这一帧的网格坐标系。
     *
     * 背景：电脑端的网格是**钉在画布左上角**的，底图却是**居中**摆放的，
     * 两端画布尺寸又差一个数量级，各画各的必然错位。所以电脑端把这份几何
     * 抽成 4 个与分辨率无关的数（gridAnchor）发下来，这里把它还原成一套
     * 屏幕坐标，网格和绘图都用它。
     *
     * 坐标系 F（返回值的 cx/cy/rotate + ox/oy/cellW/cellH 定义）：
     *   原点 = **底图中心**（== wrapper 中心，底图在两端都是居中的）；
     *   朝向 = 把电脑端网格的朝向原样搬过来 —— 电脑端网格是屏幕轴对齐的，
     *          在底图里看就是转了 -mapRotation，所以这里 ctx.rotate(-mapRotation)；
     *   ox/oy = 电脑屏幕上 (0,0) 那一点在 F 里的位置（= -画布半宽×cellW）。
     *   第 k 条竖线在 x = ox + k*cellW；(x,y) 格占 [ox+x*cellW .. ox+(x+1)*cellW]。
     *
     * 没有底图（或电脑端没下发锚点）时返回老行为的等距网格：贴 wrapper 左上角、
     * 一格固定 37.8 CSS 像素。
     */
    function computeGridFrame() {
        const w = wrapper.offsetWidth;
        const h = wrapper.offsetHeight;
        if (!w || !h) return null;

        // 有锚点时：底图在盒子里**实际占的矩形**。
        // ⚠️ 没有底图（电脑端还没设图 / 刚把图撤掉）时锚点描述的是**画布本身**，
        //    这时就把整个盒子当成那张"图" —— 电脑端没底图时也是整块画布铺网格，
        //    这样两端的格子疏密才是同一个（以前这里直接退回下面那个贴左上角、
        //    一格 37.8px 的等距网格，在平板上密得像一层细碎的小格）。
        let fit = null;
        if (gridAnchor) {
            fit = (mapImg.naturalWidth > 0 && mapImg.naturalHeight > 0)
                ? imageFitRect(w, h)
                : { x: 0, y: 0, w: w, h: h };
        }

        if (!fit) {
            const px = Math.max(5, 2.0 * (37.8 / 2));
            return {
                anchored: false, cx: 0, cy: 0, rotate: 0,
                cellW: px, cellH: px, ox: 0, oy: 0,
                w: w, h: h, fit: null,
            };
        }

        // 一格多少像素。电脑端的格子**永远是正方形**（cm/格 × 37.8），所以这里
        // 也只算一个 cell：拿"底图显示宽 ÷ cellsX"。
        // 分成 fit.w/cellsX 与 fit.h/cellsY 两路，在"有底图"时结果一样
        // （cellsX/cellsY 正好等于底图的比例），但在"没底图"时（那套锚点描述的是
        // 画布，比例和盒子不一定相同）会算出长方形格子。
        const cellPx = fit.w / gridAnchor.cellsX;
        return {
            anchored: true,
            cx: fit.x + fit.w / 2,     // 底图中心 = 锚点原点
            cy: fit.y + fit.h / 2,
            rotate: -mapRotation * Math.PI / 180,
            cellW: cellPx,
            cellH: cellPx,
            ox: -gridAnchor.halfW * cellPx,   // 电脑屏幕 (0,0) 在 F 里的位置
            oy: -gridAnchor.halfH * cellPx,
            w: w,
            h: h,
            fit: fit,
        };
    }

    function drawPlayerGrid() {
        // 用 offsetWidth/offsetHeight（**布局**尺寸），不能用 getBoundingClientRect：
        // 后者返回的是"旋转并缩放之后"的包围盒，转了 90° 之后宽高会互换，
        // 网格就会按错误的宽高比画出来（看起来是横竖被拉长过的格子）。
        const w = wrapper.offsetWidth;
        const h = wrapper.offsetHeight;
        if (!w || !h) return;

        gridFrame = computeGridFrame();

        canvas.width = w;
        canvas.height = h;
        const ctx = canvas.getContext('2d');
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        if (gridFrame) {
            // 纯白 / 纯黑两种，跟电脑端保持一致（见 map_change 里的 gridColor）。
            // 不透明度 0.78 —— 和 DM 端画布上的取值一样，两边看到的网格深浅一致。
            ctx.strokeStyle = gridColor === 'black' ? 'rgba(0,0,0,0.78)' : 'rgba(255,255,255,0.78)';
            ctx.lineWidth = 1;

            ctx.save();
            ctx.translate(gridFrame.cx, gridFrame.cy);
            if (gridFrame.rotate) ctx.rotate(gridFrame.rotate);

            // F 的可见范围是个以原点为中心、对角线长度的圆，留一格余量就够
            const reach = Math.hypot(w, h) / 2 + Math.max(gridFrame.cellW, gridFrame.cellH);

            ctx.beginPath();
            const k0 = Math.floor((-reach - gridFrame.ox) / gridFrame.cellW);
            const k1 = Math.ceil((reach - gridFrame.ox) / gridFrame.cellW);
            for (let k = k0; k <= k1; k++) {
                const x = gridFrame.ox + k * gridFrame.cellW;
                ctx.moveTo(x, -reach);
                ctx.lineTo(x, reach);
            }
            const m0 = Math.floor((-reach - gridFrame.oy) / gridFrame.cellH);
            const m1 = Math.ceil((reach - gridFrame.oy) / gridFrame.cellH);
            for (let m = m0; m <= m1; m++) {
                const y = gridFrame.oy + m * gridFrame.cellH;
                ctx.moveTo(-reach, y);
                ctx.lineTo(reach, y);
            }
            ctx.stroke();
            ctx.restore();
        }

        // 网格换算成像素之后，绘图层才能按同一个格子尺寸铺图
        renderDrawing();
    }

    // ---------- 绘图层（地形 / 实体 / 物品） ----------

    // 电脑端画了哪些格。结构：[{ k: 'terrain'|'entity'|'item', x, y, f: '树.png' }]，
    // 顺序就是渲染顺序（地形在前，实体/物品按落笔先后在后）。
    let drawingCells = [];
    let drawingVersion = -1;

    // 一格在手机上要多少像素的贴图。地形原图是 1254×1254、单张 1.3–3.3MB，
    // 一格在手机上只有三四十 CSS 像素，64px 的小图放大一点点也看不出糊，
    // 却能把流量从几十兆降到几十 KB。
    const TILE_PX = 64;

    // 手机端不自己判断"谁盖住谁"：电脑端已经把顺序排好了，这里照着铺就行。
    const tileCache = new Map();

    function fetchDrawing() {
        fetch('/api/drawing', { cache: 'no-store' })
            .then((res) => res.json())
            .then((data) => {
                if (!data || !Array.isArray(data.cells)) return;
                drawingCells = data.cells;
                drawingVersion = typeof data.version === 'number' ? data.version : 0;
                renderDrawing();
            })
            .catch(() => { /* 网络抖动就等下一次事件再拉 */ });
    }

    function tileFor(kind, file) {
        const key = kind + '/' + file;
        const hit = tileCache.get(key);
        if (hit !== undefined) return hit;          // false = 拉失败过，别再拉

        tileCache.set(key, null);                   // 占位，避免同一张图重复请求
        const img = new Image();
        img.onload = () => {
            tileCache.set(key, img);
            renderDrawing();
        };
        img.onerror = () => { tileCache.set(key, false); };
        img.src = '/api/terrain/tile/' + encodeURIComponent(kind) + '/' +
            encodeURIComponent(file) + '?px=' + TILE_PX;
        return null;
    }

    function renderDrawing() {
        if (!drawCanvas) return;

        const w = wrapper.offsetWidth;
        const h = wrapper.offsetHeight;
        const frame = gridFrame;

        if (!frame || !drawingCells.length || !w || !h || frame.cellW <= 0.01 || frame.cellH <= 0.01) {
            drawCanvas.width = 1;
            drawCanvas.height = 1;
            drawCanvas.style.width = '0px';
            drawCanvas.style.height = '0px';
            return;
        }

        // 画布铺满整个 wrapper、固定在 (0,0)。
        // 不能再像以前那样"只覆盖画到过的那一小块、用 left/top 摆过去"了 ——
        // 绘图必须和网格用**同一套坐标系**，转 90/270 时它俩得一起转。
        drawCanvas.width = w;
        drawCanvas.height = h;
        drawCanvas.style.width = w + 'px';
        drawCanvas.style.height = h + 'px';
        drawCanvas.style.left = '0px';
        drawCanvas.style.top = '0px';

        const ctx = drawCanvas.getContext('2d');
        ctx.clearRect(0, 0, w, h);

        ctx.save();
        ctx.translate(frame.cx, frame.cy);
        if (frame.rotate) ctx.rotate(frame.rotate);

        for (const cell of drawingCells) {
            const img = tileFor(cell.k, cell.f);
            if (!img) continue;
            ctx.drawImage(img,
                frame.ox + cell.x * frame.cellW,
                frame.oy + cell.y * frame.cellH,
                frame.cellW, frame.cellH);
        }

        ctx.restore();
    }

    // 电脑端改完绘图只推一个版本号，内容自己来拉
    socket.on('drawing_change', (data) => {
        if (data && typeof data.version === 'number' && data.version === drawingVersion) return;
        fetchDrawing();
    });


    // ---------- 旋转 + 缩放 + 拖动 ----------
    //
    // ==========================================================================
    // 【第二十三轮定稿】手机端额外旋转（extra）：**长边贴长边，只有两个值**
    // ==========================================================================
    //
    //      视口竖（设备正立 / 倒立）        → extra = 90
    //      视口横（向左横排 / 向右横排）    → extra = 0    ← 与电脑画布那一张完全一样
    //
    // 这是用户**逐姿态确认**过的唯一结论（原话）：
    //   「倒立和向左横排（设备逆时针转 90 度）的地图朝向是对的，
    //     正立和向右横排的方向是反的」
    // 两组各错 180°，而它们恰好就是"竖屏组"和"横屏组" —— 于是规则只剩两个值。
    //
    // ---- 下面两条是被实机推翻的旧推导，留着是为了别再犯 ----
    // · 第二十一轮："extra 恒 0"。那等于把画面**焊死在屏幕上**，屏幕在世界里跟着设备转，
    //   于是地图也跟着转（用户：「地图会跟随设备一起旋转」）。
    // · 第二十二轮："地图固定在世界里"，即 `extra = 设备转角 - 90`，想让四种姿态在
    //   世界里朝同一边。实机证明这个前提**不成立**：用户看到的是"两种姿态对、
    //   另两种反 180°"。⇒ **不要再假设"世界角恒定"，以实机结论为准。**
    //
    // 之所以能只看视口方向：手机上"竖视口 ⇔ 正立/倒立"、"横视口 ⇔ 左右横排"，
    // 两者一一对应；而视口尺寸是**一定拿得到**的（设备转角 API 反倒可能没有）。
    // 桌面浏览器视口是横的 → extra = 0，正好就是"当电脑看"。
    //
    // `?extra=<deg>` 是**叠加微调**（默认 0）：`?extra=180` 把这两个值整体翻面、
    // `?extra=90` 再拧 90°，用来现场反悔，不用改代码不用重编。
    const PORTRAIT_EXTRA = 90;

    // 由 applyRotation() 按视口方向刷新。缓存成变量是为了让 toLocalDelta()
    // （每次 touchmove 都会调）不必去读布局尺寸。
    let extraRot = 0;

    // 是否"填满屏幕"（`?box=fill`）。layoutBox() 会读它。
    let boxFill = false;

    function normDeg(v) {
        return ((v % 360) + 360) % 360;
    }

    /**
     * 设备相对"正放"转了多少度（0/90/180/270）；两个 API 都拿不到就给 -1。
     *
     * ⚠️ 第二十三轮起它**不再参与决策**（只喂 `?debug=1` 读数），但符号仍然要对，
     * 否则真机取证时会被读数带偏：
     *   1) `window.orientation` 的正值是**逆时针** —— 所以"向右横排"（把设备
     *      顺时针拧 90°）报的是 **-90**（MDN：positive values are counterclockwise）。
     *   2) `screen.orientation.angle` 与它**相反**（兼容规范里 wo = -angle）。
     *      直接把 angle 当 wo 用会把 90 与 270 对调，所以这里显式取反。
     * 顺序也重要：**先问 `window.orientation`**（报的就是 wo 本身），
     * 拿不到才退回 `screen.orientation.angle` 再取反。
     */
    function deviceRotationDeg() {
        if (typeof window.orientation === 'number' && isFinite(window.orientation)) {
            return normDeg(window.orientation);
        }
        const so = window.screen ? window.screen.orientation : null;
        if (so && typeof so.angle === 'number' && isFinite(so.angle)) {
            return (360 - normDeg(so.angle)) % 360;
        }
        return -1;
    }

    /**
     * `?extra=<deg>` —— 在默认值上**叠加**一个角度（默认 0）。
     *
     * 平板上装不了开发者工具，靠它当场反悔：默认是"地图固定在世界里"，
     * `?extra=180` 把脸翻过来、`?extra=90`/`270` 再拧 90°，不用改代码、不用重编。
     */
    function forcedExtraTrim() {
        const m = /[?&]extra=(-?\d+)/.exec(location.search);
        if (!m) return 0;
        const v = parseInt(m[1], 10);
        return isFinite(v) ? v : 0;
    }

    /** extra 是不是"转 90° 的那种"（90/270）—— 这时屏幕宽高在显示时是互换的。 */
    function isQuarterTurn(deg) {
        const d = normDeg(deg);
        return d === 90 || d === 270;
    }

    /**
     * `?box=fill` —— 让内容**填满屏幕**（会裁掉一部分底图），默认不填（整块画布都看得见、留黑边）。
     *
     * 这是个"取舍开关"，不是 bug 开关：屏幕比例和画布比例不同时，两者只能选一个 ——
     *   · 默认（不填）：**看得见的格数与电脑完全一致**（和电脑同一块区域），代价是四周留黑边；
     *   · `?box=fill`：铺满屏幕、没有黑边，代价是**一屏看到的格数比电脑少**（裁掉了边缘）。
     */
    function boxFillRequested() {
        return /[?&]box=fill\b/.test(location.search);
    }

    function computeExtraRotation() {
        const trim = forcedExtraTrim();
        const view = viewportSize();
        const landscapeView = view.w > view.h;
        // 长边贴长边：视口竖 → 90、视口横 → 0。
        // （桌面浏览器的视口是横的 → 0，正好就是"当电脑看、一点都不转"。）
        return normDeg((landscapeView ? 0 : PORTRAIT_EXTRA) + trim);
    }

    function totalRotation() {
        return ((mapRotation + extraRot) % 360 + 360) % 360;
    }

    // 视口尺寸。用容器而不是 100vh/100vw：移动端地址栏收起/展开时 100vh 会跳一下。
    function viewportSize() {
        const host = document.getElementById('player-map-container');
        return {
            w: host ? host.clientWidth : window.innerWidth,
            h: host ? host.clientHeight : window.innerHeight,
        };
    }

    /**
     * 内容盒子的尺寸（**旋转前**那一对）。
     *
     * 【第二十一/二十二/二十三轮定稿】目标：**手机显示的就是电脑屏幕那一块画面**，
     * 只是整体等比缩放、并按 extra 转了一下。
     *
     * ⚠️ 这里有两个"换向"最容易写错，必须分清：
     *   ① **盒子的比例 = 电脑"逻辑视口"的比例**。
     *      电脑端把底图转 90/270 时，它是把整块画布绕着中心转过去画的
     *      （见 MapCanvas.ApplyRotationTransform），所以"逻辑视口"的宽高是**互换**的：
     *      `逻辑宽 = 转90/270 ? 屏幕高 : 屏幕宽`。锚点里的 halfW/halfH 是**屏幕**视口的
     *      （W/(2cell) / H/(2cell)），所以盒子要用 `转90/270 ? halfH*2 : halfW*2` 当宽。
     *      ⇒ **以前这里一直用 halfW:halfH，底图只要转过 90/270，盒子比例就是错的**
     *        （底图是按逻辑视口 cover/contain 进去的，盒子比例不对 ⇒ 底图与网格对不上、
     *         看到的格数和位置全不对）。
     *   ② **盒子在屏幕上转的是 `total = mapRotation + extra`**，所以"量视口"时
     *      要在 `total` 是 90/270 时把视口宽高互换。
     *      ⇒ 以前这里看的是 `extra`：底图转着的时候 total 会变成 180 或 0，
     *        盒子就不该换向/该换向，于是**整块溢出屏幕被裁掉**（实测 667×400 塞进 400×800）。
     *
     * 拿不到锚点（老版本电脑端）时才退回"长边 × 短边"。
     */
    function layoutBox() {
        const view = viewportSize();

        if (gridAnchor && gridAnchor.halfW > 0 && gridAnchor.halfH > 0) {
            // ① 盒子（逻辑视口）的宽高，单位 = 格
            const mapQuarter = isQuarterTurn(mapRotation);
            const cellsW = mapQuarter ? gridAnchor.halfH * 2 : gridAnchor.halfW * 2;
            const cellsH = mapQuarter ? gridAnchor.halfW * 2 : gridAnchor.halfH * 2;

            // ② 盒子要整体转 total 才贴到屏幕上 ⇒ 量的时候视口也跟着换向
            const eff = isQuarterTurn(totalRotation())
                ? { w: view.h, h: view.w }
                : { w: view.w, h: view.h };

            const k = boxFill
                ? Math.max(eff.w / cellsW, eff.h / cellsH)     // 填满屏幕（会裁掉一部分）
                : Math.min(eff.w / cellsW, eff.h / cellsH);    // 整块都看得见（留黑边）
            if (k > 0 && isFinite(k)) {
                const w = Math.max(1, Math.round(cellsW * k));
                // 高度由宽度推出来，保证宽高比不被两次取整拧歪
                return { w: w, h: Math.max(1, Math.round(w * cellsH / cellsW)) };
            }
        }

        // 兜底：没有锚点 → 老电脑端 + 老网格，只能按屏幕来
        const sc = window.screen;
        let longSide = Math.max(view.w, view.h);
        let shortSide = Math.min(view.w, view.h);
        if (sc && sc.width > 0 && sc.height > 0) {
            const scLong = Math.max(sc.width, sc.height);
            const scShort = Math.min(sc.width, sc.height);
            // 差太多说明是"窗口小于显示器"的桌面环境，用视口更贴近实际可见区域
            if (longSide > 0 && scLong <= longSide * 1.25) {
                longSide = scLong;
                shortSide = scShort;
            }
        }
        // 兜底那条本来就是"贴着屏幕来"的，转过去就得把长边/短边换回来
        if (isQuarterTurn(totalRotation())) {
            const t = longSide; longSide = shortSide; shortSide = t;
        }
        return { w: Math.max(1, Math.round(longSide)), h: Math.max(1, Math.round(shortSide)) };
    }

    /**
     * 摆好"内容盒子"并应用变换。
     *
     * 盒子按**旋转前**的尺寸给（转 90/270 时它的宽高正好是屏幕上看到的那一对的
     * 互换），再整块 `rotate(total)` 转过去 —— 转完刚好铺满视口。
     */
    function applyRotation() {
        // 每一次重摆都重新问一遍（跟着设备转角走，见 computeExtraRotation()）
        extraRot = computeExtraRotation();
        boxFill = boxFillRequested();

        const box = layoutBox();
        const boxW = box.w;
        const boxH = box.h;

        mapImg.style.width = boxW + 'px';
        mapImg.style.height = boxH + 'px';
        // 电脑端 "fill" = 放大到盖满再裁（保持比例）→ 对应 CSS 的 cover。
        // 千万不能直接写 fill：CSS 的 fill 是**不等比拉伸**，底图会变形，
        // 网格与底图的绑定也就跟着失效了。
        mapImg.style.objectFit = mapFitMode === 'contain' ? 'contain' : 'cover';
        wrapper.style.width = boxW + 'px';
        wrapper.style.height = boxH + 'px';

        updateTransform();
        drawPlayerGrid();
    }

    function updateTransform() {
        wrapper.style.transform =
            `translate(-50%, -50%) rotate(${totalRotation()}deg) ` +
            `scale(${scale}) translate(${translateX}px, ${translateY}px)`;
    }

    /**
     * 把屏幕上的位移换算进"已经旋转过、缩放过"的容器坐标系。
     * 不做这一步的话，画面转了 90° 之后手指往右拖、地图会往上下跑。
     */
    function toLocalDelta(dx, dy) {
        const r = totalRotation() * Math.PI / 180;
        const cos = Math.cos(r);
        const sin = Math.sin(r);
        const s = scale || 1;
        return {
            x: (dx * cos + dy * sin) / s,
            y: (-dx * sin + dy * cos) / s,
        };
    }

    function distance(t1, t2) {
        return Math.hypot(t1.clientX - t2.clientX, t1.clientY - t2.clientY);
    }

    function midpoint(t1, t2) {
        return { x: (t1.clientX + t2.clientX) / 2, y: (t1.clientY + t2.clientY) / 2 };
    }

    function clampScale(value) {
        return Math.min(Math.max(value, 0.3), 8);
    }

    wrapper.addEventListener('touchstart', (e) => {
        if (e.touches.length === 2) {
            lastTouchDist = distance(e.touches[0], e.touches[1]);
            const mid = midpoint(e.touches[0], e.touches[1]);
            lastTouchX = mid.x;
            lastTouchY = mid.y;
            isDragging = false;
        } else if (e.touches.length === 1) {
            lastTouchX = e.touches[0].clientX;
            lastTouchY = e.touches[0].clientY;
            isDragging = true;
        }
    }, { passive: true });

    wrapper.addEventListener('touchmove', (e) => {
        e.preventDefault();

        if (e.touches.length === 2) {
            const dist = distance(e.touches[0], e.touches[1]);
            const mid = midpoint(e.touches[0], e.touches[1]);
            if (lastTouchDist > 0) {
                scale = clampScale(scale * (dist / lastTouchDist));
            }
            const d = toLocalDelta(mid.x - lastTouchX, mid.y - lastTouchY);
            translateX += d.x;
            translateY += d.y;
            lastTouchDist = dist;
            lastTouchX = mid.x;
            lastTouchY = mid.y;
            updateTransform();
            return;
        }

        if (e.touches.length === 1 && isDragging) {
            const t = e.touches[0];
            const d = toLocalDelta(t.clientX - lastTouchX, t.clientY - lastTouchY);
            translateX += d.x;
            translateY += d.y;
            lastTouchX = t.clientX;
            lastTouchY = t.clientY;
            updateTransform();
        }
    }, { passive: false });

    wrapper.addEventListener('touchend', (e) => {
        if (e.touches.length === 0) {
            isDragging = false;
            lastTouchDist = 0;
        } else if (e.touches.length === 1) {
            lastTouchDist = 0;
            lastTouchX = e.touches[0].clientX;
            lastTouchY = e.touches[0].clientY;
            isDragging = true;
        }
    }, { passive: true });

    wrapper.addEventListener('wheel', (e) => {
        e.preventDefault();
        const factor = e.deltaY < 0 ? 1.1 : 1 / 1.1;
        scale = clampScale(scale * factor);
        updateTransform();
    }, { passive: false });

    wrapper.addEventListener('dblclick', () => {
        scale = 1;
        translateX = 0;
        translateY = 0;
        updateTransform();
    });

    /**
     * 转屏之后重新摆一遍。
     *
     * 容器的布局尺寸不是立刻就变的（iOS 的地址栏、转屏动画期间都会晚一拍），
     * 所以除了马上量一次，再补两个稍后的点 —— 少了这两拍，偶尔会按"转屏前的
     * 尺寸"摆一次歪的，看起来就是"横过来了地图还是竖的"。
     */
    function onViewportChanged() {
        applyRotation();
        resetFabPosition();     // 转屏后悬浮按钮归位到右下角（见 resetFabPosition）
        setTimeout(applyRotation, 120);
        setTimeout(applyRotation, 320);
    }

    window.addEventListener('orientationchange', onViewportChanged);
    // resize 也要带上归位：iOS 转屏主要靠 resize 通知（orientationchange 有时根本不报），
    // 而"转完自动回到右下角"是对这个按钮的硬要求。
    window.addEventListener('resize', () => {
        applyRotation();
        resetFabPosition();
    });

    // screen.orientation 是更靠谱的那条通知渠道（有些浏览器只在这里报变化）
    const screenOrientation = window.screen ? window.screen.orientation : null;
    if (screenOrientation && typeof screenOrientation.addEventListener === 'function') {
        screenOrientation.addEventListener('change', onViewportChanged);
    }

    /**
     * 现场排查用：地址后面加 `?debug=1`，左下角会挂一行朝向读数。
     *
     * 平板上装不了开发者工具，这一行字就是唯一的证据 —— 转一圈屏幕截个图，
     * 就能看出这台机器到底报的是 portrait/landscape、有没有 secondary。
     * `pointer-events:none`，不挡拖动。
     */
    function setupOrientationDebug() {
        if (!/[?&]debug=1/.test(location.search)) return;

        const box = document.createElement('div');
        box.style.cssText = 'position:fixed;left:0;bottom:0;z-index:4000;pointer-events:none;' +
            'background:rgba(0,0,0,.75);color:#5f5;font:12px/1.5 Consolas,monospace;' +
            'padding:4px 6px;white-space:pre';
        document.body.appendChild(box);

        const tick = () => {
            const view = viewportSize();
            const layout = layoutBox();
            const type = screenOrientation && screenOrientation.type ? screenOrientation.type : '(无)';
            const angle = screenOrientation && typeof screenOrientation.angle === 'number'
                ? screenOrientation.angle : '(无)';
            const legacy = typeof window.orientation === 'number' ? window.orientation : '(无)';
            const deg = deviceRotationDeg();
            const trim = forcedExtraTrim();
            const pose = deg < 0 ? '(读不到)'
                : (deg === 180 ? '倒立'
                    : deg === 90 ? '向左横排'
                        : deg === 270 ? '向右横排' : '正立');
            const mapQ = isQuarterTurn(mapRotation);
            const anchorLine = gridAnchor
                ? '锚点 逻辑视口 ' +
                  (mapQ ? gridAnchor.halfH * 2 : gridAnchor.halfW * 2).toFixed(2) + 'x' +
                  (mapQ ? gridAnchor.halfW * 2 : gridAnchor.halfH * 2).toFixed(2) + ' 格' +
                  (mapQ ? '（底图转 90/270 ⇒ 与屏幕换向）' : '') + '\n' +
                  '     halfW ' + gridAnchor.halfW.toFixed(3) + ' halfH ' + gridAnchor.halfH.toFixed(3) +
                  ' cellsX ' + gridAnchor.cellsX.toFixed(3) + ' cellsY ' + gridAnchor.cellsY.toFixed(3) + '\n'
                : '锚点 (无·退回老网格，格数与电脑无对应关系)\n';
            const gf = gridFrame;
            const gridLine = gf && gf.anchored
                ? '网格 cellPx ' + gf.cellW.toFixed(2) +
                  '  底图矩形 ' + gf.fit.x.toFixed(1) + ',' + gf.fit.y.toFixed(1) + ' ' +
                  gf.fit.w.toFixed(1) + 'x' + gf.fit.h.toFixed(1) + '\n'
                : '网格 (老网格·贴左上角)\n';
            const landscapeView = view.w > view.h;
            box.textContent =
                'player.js ' + PLAYER_JS_REV + '\n' +
                // 房间这几行是给"平板/手机上房间号或密码不对"这类问题取证用的：
                // 一眼能看出地址里的号、服务器当前的号、以及密码要不要、判没判错。
                '房间 URL ' + (JOIN_MODE ? '/join(手填)' : (roomInUrl || '(老地址)')) +
                '  服务器 ' + (serverRoom || '(未取到)') +
                '  密码 ' + (needPassword ? (passwordRejected ? '需要·已判错' : '需要') : '不需要') +
                (roomPassword ? '  已带密码' : '  未带密码') + '\n' +
                'viewport ' + view.w + 'x' + view.h + (landscapeView ? ' 横' : ' 竖') + '\n' +
                '盒子(旋转前) ' + layout.w + 'x' + layout.h +
                (boxFill ? '  [box=fill 铺满]' : '  [留边]') + '\n' +
                anchorLine +
                '底图 ' + (mapImg.naturalWidth || 0) + 'x' + (mapImg.naturalHeight || 0) +
                '  ' + mapFitMode + '\n' +
                gridLine +
                'orientation.type  ' + type + '\n' +
                'orientation.angle ' + angle + '\n' +
                'window.orientation ' + legacy + '\n' +
                '设备转角 ' + (deg < 0 ? '(无)' : deg + '°') + '  → ' + pose + '（仅供参考）\n' +
                'extra ' + extraRot + '  ← 长边贴长边：视口' +
                (landscapeView ? '横→0' : '竖→90') +
                (trim !== 0 ? '  ?extra 微调 ' + trim : '') + '\n' +
                'map ' + mapRotation + '  total ' + totalRotation();
        };

        tick();
        refreshDebugPanel = tick;      // 房间信息到手后 initRoom 会叫它刷一次
        window.addEventListener('resize', tick);
        setInterval(tick, 500);
    }

    // ---------- 右下角悬浮按钮：菜单 / 退出房间 ----------
    //
    // 位置要求和地图**不一样**：地图那块是"长边贴长边"跟着转的，而这个按钮要的是
    // **相对屏幕**永远待在右下角。所以它固定在 `fixed` 坐标系里（不在那个被 rotate 的
    // wrapper 里），转屏之后浏览器自己会把 right/bottom 重算一遍；
    // 玩家拖过之后由 JS 写下 left/top，转屏时清掉这两个内联样式就等于归位。

    const FAB_MARGIN = 12;      // 面板至少离屏幕边缘这么远
    const FAB_GAP = 8;          // 按钮与面板之间的缝
    const FAB_DRAG_SLOP = 6;    // 移动不超过这么多像素就算"点击"而不是"拖动"

    let fabDragging = false;
    let fabMoved = false;
    let fabGrabX = 0;
    let fabGrabY = 0;
    let fabStartX = 0;
    let fabStartY = 0;

    function setFabVisible(visible) {
        fab.classList.toggle('fab-hidden', !visible);
        if (!visible) closeFabMenu();
    }

    /**
     * 按"登没登录"调整悬浮菜单：
     *   没登录 → 按钮也在（登录页就能改语言，不然看不懂中文的人连菜单都进不去），
     *           但根菜单里只有「设置」，没有「退出房间」。
     *   已登录 → 两项都在。
     */
    function updateFabForLogin() {
        setFabVisible(true);
        if (exitBtn) exitBtn.style.display = loggedIn ? '' : 'none';
        if (menuOpen()) placeFabMenu();
    }

    function menuOpen() {
        return !fabMenu.classList.contains('fab-hidden');
    }

    function closeFabMenu() {
        fabMenu.classList.add('fab-hidden');
    }

    /**
     * 切菜单页：root（设置 / 退出房间）→ settings（语言）→ language（语言列表）。
     * 面板尺寸会跟着变，所以切完要重新摆一次（和"打开时先量尺寸"是同一个理由）。
     */
    let menuPage = 'root';

    function showMenuPage(name) {
        menuPage = name;
        const pages = fabMenu.querySelectorAll('[data-page]');
        for (let i = 0; i < pages.length; i++) {
            const mine = pages[i].getAttribute('data-page') === name;
            pages[i].classList.toggle('fab-hidden', !mine);
        }
        if (menuOpen()) placeFabMenu();
    }

    /**
     * 把展开的面板摆到"整个内容都看得见"的位置：
     *   横向：先按"右边缘和按钮对齐"，再夹进屏幕；
     *   纵向：按钮下面放得下就放下面，放不下就翻到按钮上方 —— 再夹一次。
     * 夹完还放不下（屏幕太扁）就交给 CSS 的 `max-height + overflow-y:auto`，
     * 面板自己滚，但**不显示滚动条**。
     */
    function placeFabMenu() {
        const b = fab.getBoundingClientRect();
        const w = fabMenu.offsetWidth;
        const h = fabMenu.offsetHeight;

        let left = b.right - w;
        left = Math.min(Math.max(FAB_MARGIN, left), Math.max(FAB_MARGIN, window.innerWidth - w - FAB_MARGIN));

        const below = window.innerHeight - b.bottom;
        let top = (below >= h + FAB_GAP + FAB_MARGIN) ? (b.bottom + FAB_GAP) : (b.top - h - FAB_GAP);
        top = Math.min(Math.max(FAB_MARGIN, top), Math.max(FAB_MARGIN, window.innerHeight - h - FAB_MARGIN));

        fabMenu.style.left = Math.round(left) + 'px';
        fabMenu.style.top = Math.round(top) + 'px';
    }

    function openFabMenu() {
        showMenuPage('root');           // 每次打开都从第一级开始
        applyI18n();                    // 顺手把菜单文字刷成当前语言（列表也要重排）
        fabMenu.classList.remove('fab-hidden');
        // 先摆到边上再量尺寸，量到的是真实宽高，最后再摆一次到正确位置
        fabMenu.style.left = FAB_MARGIN + 'px';
        fabMenu.style.top = FAB_MARGIN + 'px';
        placeFabMenu();
    }

    function toggleFabMenu() {
        if (menuOpen()) closeFabMenu();
        else openFabMenu();
    }

    /**
     * 归位：清掉拖动留下的内联 left/top，回到 CSS 里那组 `right/bottom`。
     * **每次转屏都会调一次** —— 这就是"旋转后自动回到右下角"。
     */
    function resetFabPosition() {
        fab.style.left = '';
        fab.style.top = '';
        if (menuOpen()) placeFabMenu();
    }

    function moveFab(clientX, clientY) {
        const size = fab.offsetWidth || 54;
        let left = clientX - fabGrabX;
        let top = clientY - fabGrabY;
        left = Math.min(Math.max(0, left), Math.max(0, window.innerWidth - size));
        top = Math.min(Math.max(0, top), Math.max(0, window.innerHeight - size));
        fab.style.left = Math.round(left) + 'px';
        fab.style.top = Math.round(top) + 'px';
    }

    function fabPointerDown(clientX, clientY) {
        const b = fab.getBoundingClientRect();
        fabGrabX = clientX - b.left;
        fabGrabY = clientY - b.top;
        fabStartX = clientX;
        fabStartY = clientY;
        fabDragging = true;
        fabMoved = false;
        if (menuOpen()) closeFabMenu();
    }

    function fabPointerMove(clientX, clientY) {
        if (!fabDragging) return;
        if (!fabMoved &&
            Math.hypot(clientX - fabStartX, clientY - fabStartY) < FAB_DRAG_SLOP) return;
        fabMoved = true;
        moveFab(clientX, clientY);
    }

    function fabPointerUp() {
        if (!fabDragging) return;
        fabDragging = false;
        if (!fabMoved) toggleFabMenu();     // 没挪动过 = 一次点击
    }

    fab.addEventListener('touchstart', (e) => {
        if (e.touches.length !== 1) return;
        e.preventDefault();
        const t = e.touches[0];
        fabPointerDown(t.clientX, t.clientY);
    }, { passive: false });

    fab.addEventListener('touchmove', (e) => {
        if (!fabDragging || e.touches.length !== 1) return;
        e.preventDefault();
        const t = e.touches[0];
        fabPointerMove(t.clientX, t.clientY);
    }, { passive: false });

    fab.addEventListener('touchend', (e) => {
        if (e.touches.length === 0) fabPointerUp();
    }, { passive: true });

    fab.addEventListener('touchcancel', () => { fabDragging = false; }, { passive: true });

    // 桌面上用鼠标也能拖（调试用；玩家页主要在手机 / 平板上）
    fab.addEventListener('mousedown', (e) => {
        e.preventDefault();
        fabPointerDown(e.clientX, e.clientY);
    });
    window.addEventListener('mousemove', (e) => fabPointerMove(e.clientX, e.clientY));
    window.addEventListener('mouseup', () => fabPointerUp());

    // 点到别处就把面板收起来
    document.addEventListener('touchstart', (e) => {
        if (!menuOpen()) return;
        if (fab.contains(e.target) || fabMenu.contains(e.target)) return;
        closeFabMenu();
    }, { passive: true });

    if (settingsBtn) settingsBtn.addEventListener('click', () => { showMenuPage('settings'); });
    if (languageBtn) languageBtn.addEventListener('click', () => { showMenuPage('language'); });

    // 两个「返回」按钮：data-back 指向要回的那一页
    Array.prototype.forEach.call(fabMenu.querySelectorAll('[data-back]'), (btn) => {
        btn.addEventListener('click', () => { showMenuPage(btn.getAttribute('data-back')); });
    });

    exitBtn.addEventListener('click', () => { exitRoom(); });

    /**
     * 退出房间。
     *
     * 服务器那边：房间在 + token 对 → 把这个人从名册里删掉（令牌一起作废），
     * 并且所有人（含 DM）都会看到 "xxx 离开了房间"。
     *
     * 本地这边：**不管服务器有没有回话，都算退出** —— 房间已经离线（DM 把程序关了）
     * 的时候，"退出"本来就该以本地为准，否则玩家会被困在一个死掉的房间里。
     */
    function exitRoom() {
        const name = playerName || getCookie(COOKIE_NAME) || '';
        const token = getCookie(COOKIE_TOKEN) || '';
        const room = serverRoom || roomInUrl || getCookie(COOKIE_ROOM) || '';

        exitBtn.disabled = true;
        exitBtn.textContent = T('exit.doing');

        postWithTimeout('/api/leave', { room: room, name: name, token: token }, 4000)
            .then((res) => {
                if (res && res.code === 'ROOM_GONE') return finishExit('exit.gone');
                // 服务器说成功、或者说"名册里没有你"——对玩家都是"已经退出"。
                // 服务器那句 message 是中文，这里不再直接用它。
                return finishExit('exit.done');
            })
            .catch(() => finishExit('exit.offline'));
    }

    function finishExit(key, a) {
        exitBtn.disabled = false;
        exitBtn.textContent = T('fab.exit');
        closeFabMenu();

        // 清掉 token 与房间号（**名字留着**，重新加入时少打一次）
        clearRoomIdentity();
        try { sessionStorage.removeItem(PASSWD_KEY); } catch (e) { /* 忽略 */ }
        roomPassword = '';
        passwordRejected = false;
        loggedIn = false;
        exited = true;
        playerName = '';
        updateFabForLogin();
        toolbar.style.display = 'none';
        displayName.textContent = T('common.unknown');

        try { socket.close(); } catch (e) { /* 已经关了 */ }

        loginOverlay.style.display = 'flex';
        setError('');
        setBusy(false, '');
        showRoomInfo('', false);
        if (!nameInput.value.trim()) {
            const savedName = getCookie(COOKIE_NAME);
            if (savedName) nameInput.value = savedName;
        }
        infoState = { exitKey: key, a: a };
        renderInfo();
    }

    // ---------- 顶部胶囊提示（"xxx 离开了房间"） ----------

    let toastTimer = null;

    /**
     * 从屏幕上方正中滑下来一条胶囊提示，`ms` 毫秒后缩回。
     */
    function showToast(text, ms) {
        if (!toastEl) return;
        toastEl.textContent = text;
        // 先摘掉再戴上，保证连点两次也能重新触发动画
        toastEl.classList.remove('show');
        void toastEl.offsetWidth;
        toastEl.classList.add('show');

        if (toastTimer) clearTimeout(toastTimer);
        toastTimer = setTimeout(() => {
            toastTimer = null;
            toastEl.classList.remove('show');
        }, ms || 2000);
    }

    socket.on('player_left', (data) => {
        const who = (data && data.name) ? data.name : T('toast.anon');
        showToast(T('toast.left', who), 2000);
    });

    // ---------- 连接状态 ----------
    socket.on('disconnect', () => {
        refreshConnectionStatus();
    });

    socket.on('connect', () => {
        refreshConnectionStatus();
        if (!loggedIn) autoLogin();
        else relogin();
    });

    // 从后台切回前台：服务器早就把这条连接判死了（我们这边可能还以为连着），
    // 所以直接换一条新连接 —— 打通后照常走 'connect' 里的重新登录。
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState !== 'visible' || !loggedIn) return;
        socket.reconnect();
    });

    // 从 bfcache 恢复（iOS Safari 的后退/切换很常见）时连接同样是废的
    window.addEventListener('pageshow', (e) => {
        if (!e.persisted || !loggedIn) return;
        socket.reconnect();
    });

    // 服务器换了 IP/端口：跟随新地址刷新
    socket.on('server_moved', (data) => {
        if (data && data.playerUrl) {
            setBusy(true, 'server.moved');
            setTimeout(() => { window.location.href = data.playerUrl; }, 800);
        }
    });

    // 被踢
    socket.on('kicked', () => {
        loggedIn = false;
        loginOverlay.style.display = 'flex';
        setError('kick.error');
        setBusy(false, 'kick.info');
        try { alert(T('kick.alert')); } catch { /* 某些浏览器会拦截 alert */ }
    });

    // 启动
    // 先把地址里的房间号与密码读出来，**再**装调试面板：不然 `?debug=1` 的第一屏
    // 会显示"房间 URL (老地址)"（那时还没读），看着像出了问题。
    roomInUrl = readRoomFromPath();
    loadStoredPasswd();

    // `/join` 页多给一个房间号输入框（密码框由 updatePasswordField 一直亮着，所以是三个框）。
    if (JOIN_MODE && roomNumInput) roomNumInput.style.display = 'block';

    // 先把界面文字按当前语言填一遍（含语言列表与状态条），再问房间
    applyI18n();
    updateFabForLogin();      // 登录页也能打开「设置 → 语言」

    setupOrientationDebug();
    applyRotation();

    // 房间信息问清楚之后再谈登录：房间号是不是当前这个、要不要密码、
    // 要不要先拿旧 token 向旧房间告辞（见 initRoom）。
    // ⚠️ `/join` 页**不**自动登录 —— 房间号得玩家自己填。
    initRoom().then(() => {
        if (!JOIN_MODE && socket.connected) autoLogin();
    });
})();
