/*
 * 玩家视图逻辑。
 *
 * 令牌流程：
 *  1) 第一次用某个名称进入时，不带 token；服务器颁发随机 token 并写入 cookie；
 *  2) 之后（刷新/掉线重连）带着 cookie 里的 name + token 再进来，服务器校验通过才放行；
 *  3) 别人用同一个名称进来（没有 token 或 token 不对）会被拒绝并提示名称重复。
 *
 * 房间锁定：
 *  服务器锁定房间后，只有名册里已有名称 + 正确 token 的老玩家能重连，新名称会被拒绝。
 */
(function () {
    'use strict';

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

    const COOKIE_NAME = 'dnd_player_name';
    const COOKIE_TOKEN = 'dnd_player_token';
    const COOKIE_DAYS = 1;

    // 状态条配色。"在线"用亮绿：这个指示就是给玩家"能不能收到更新"看的，
    // 以前那种柔和的 #6a6 在一眼扫过去时和灰色没差多少。
    const ONLINE_COLOR = '#00e676';
    const OFFLINE_COLOR = '#ff6666';

    let playerName = '';
    let loggedIn = false;

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

    function clearIdentity() {
        document.cookie = COOKIE_NAME + '=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/';
        document.cookie = COOKIE_TOKEN + '=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/';
    }

    // ---------- 登录 ----------
    function attemptLogin(name) {
        // 只有名称与 cookie 记录一致时才带 token，避免换个名字复用别人的令牌
        const savedName = getCookie(COOKIE_NAME);
        const savedToken = getCookie(COOKIE_TOKEN);
        const token = (savedName && savedToken && savedName === name) ? savedToken : '';
        socket.emit('player_login', { name: name, token: token });
    }

    function setBusy(busy, text) {
        loginBtn.disabled = busy;
        loginInfo.textContent = text || '';
    }

    loginBtn.addEventListener('click', () => {
        const name = nameInput.value.trim();
        if (!name) {
            loginError.textContent = '请输入名称';
            return;
        }
        loginError.textContent = '';
        setBusy(true, '正在连接...');
        attemptLogin(name);
    });

    nameInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') loginBtn.click();
    });

    socket.on('login_success', (data) => {
        // 服务器可能颁发新 token（首次加入），一定写回 cookie
        if (data && data.token) setCookie(COOKIE_TOKEN, data.token, COOKIE_DAYS);
        if (data && data.name) setCookie(COOKIE_NAME, data.name, COOKIE_DAYS);

        playerName = (data && data.name) || nameInput.value.trim();
        displayName.textContent = playerName;
        loginOverlay.style.display = 'none';
        toolbar.style.display = 'flex';
        loginError.textContent = '';
        setBusy(false, '');
        loggedIn = true;
        applyRoomLock(data ? data.roomLocked : false);
        socket.emit('request_map');
    });

    socket.on('login_failed', (data) => {
        const code = data && data.code;
        loginError.textContent = (data && data.msg) || '登录失败';
        setBusy(false, '');
        applyRoomLock(code === 'ROOM_LOCKED');

        // 名称被占用或令牌失效：清掉本地身份，避免一直重试同一个错误
        if (code === 'TOKEN_MISMATCH' || code === 'NAME_TAKEN' || code === 'COOKIE_CONFLICT') {
            clearIdentity();
        }
    });

    socket.on('room_state', (data) => {
        applyRoomLock(data && data.locked);
    });

    function applyRoomLock(locked) {
        if (lockNotice) lockNotice.style.display = locked ? 'block' : 'none';
        if (!loggedIn && locked && !loginError.textContent) {
            loginInfo.textContent = '房间已锁定，只有原有玩家可以用原名称重连';
        }
    }

    // ---------- 自动重连 ----------
    function autoLogin() {
        if (loggedIn) return;
        const savedName = getCookie(COOKIE_NAME);
        const savedToken = getCookie(COOKIE_TOKEN);

        if (savedName && savedToken) {
            nameInput.value = savedName;
            setBusy(true, '正在自动登录...');
            attemptLogin(savedName);
        } else {
            if (savedName) nameInput.value = savedName;
            if (!loginInfo.textContent) {
                loginInfo.textContent = savedName ? '请输入名称后进入' : '请输入你的名称';
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

        if (data.url) {
            mapImg.src = data.url;

            // 网格与底图的绑定：电脑端把"底图能放几格 / 画布半宽是几格"算好发下来，
            // 这边照着重放（见 computeGridFrame）。没有这几个数就退回老行为 ——
            // 贴左上角的等距网格，位置当然对不上，但至少不会画出畸形的东西。
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
        } else {
            // 电脑端清掉了底图：手机上也别继续挂着上一张
            mapImg.removeAttribute('src');
            gridAnchor = null;
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

        const fit = gridAnchor ? imageFitRect(w, h) : null;
        if (!fit) {
            const px = Math.max(5, 2.0 * (37.8 / 2));
            return {
                anchored: false, cx: 0, cy: 0, rotate: 0,
                cellW: px, cellH: px, ox: 0, oy: 0,
                w: w, h: h, fit: null,
            };
        }

        const cellW = fit.w / gridAnchor.cellsX;
        const cellH = fit.h / gridAnchor.cellsY;
        return {
            anchored: true,
            cx: fit.x + fit.w / 2,     // 底图中心 = 锚点原点
            cy: fit.y + fit.h / 2,
            rotate: -mapRotation * Math.PI / 180,
            cellW: cellW,
            cellH: cellH,
            ox: -gridAnchor.halfW * cellW,   // 电脑屏幕 (0,0) 在 F 里的位置
            oy: -gridAnchor.halfH * cellH,
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

    // 手机端在电脑的基础上**再**顺时针转 90°：
    // 约定手机屏幕的"上端"对应电脑屏幕的"左端"、"下端"对应电脑屏幕的"右端"，
    // 也就是网格的位置关系整体顺时针转 90°；底图则在这个基础上再多转 90°。
    // 合起来：手机看到的画面 = 电脑看到的画面顺时针转 90°。
    const PHONE_EXTRA_ROTATION = 90;

    function totalRotation() {
        return ((mapRotation + PHONE_EXTRA_ROTATION) % 360 + 360) % 360;
    }

    function isQuarterTurn(deg) {
        return deg === 90 || deg === 270;
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
     * 摆好"内容盒子"并应用变换。
     *
     * 盒子尺寸是**旋转前**的：转 90/270 时宽高互换，转过去之后才正好铺满屏幕。
     * 如果一直用 100vw×100vh，转 90° 之后内容会比屏幕"高"，上下被切掉一截。
     */
    function applyRotation() {
        const rot = totalRotation();
        const view = viewportSize();
        const quarter = isQuarterTurn(rot);
        const boxW = quarter ? view.h : view.w;
        const boxH = quarter ? view.w : view.h;

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

    window.addEventListener('orientationchange', () => {
        applyRotation();
        // 旋屏后视口尺寸要等浏览器算完才准，稍后再量一次
        setTimeout(applyRotation, 200);
    });

    window.addEventListener('resize', applyRotation);

    // ---------- 连接状态 ----------
    socket.on('disconnect', () => {
        statusEl.textContent = '● 离线';
        statusEl.style.color = OFFLINE_COLOR;
    });

    socket.on('connect', () => {
        statusEl.textContent = '● 在线';
        statusEl.style.color = ONLINE_COLOR;
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
            setBusy(true, '服务器地址已更新，正在重新连接...');
            setTimeout(() => { window.location.href = data.playerUrl; }, 800);
        }
    });

    // 被踢
    socket.on('kicked', () => {
        loggedIn = false;
        loginOverlay.style.display = 'flex';
        loginError.textContent = '你已被 DM 踢出游戏';
        setBusy(false, '可用原名称重新进入');
        try { alert('你已被DM踢出游戏'); } catch { /* 某些浏览器会拦截 alert */ }
    });

    // 启动
    applyRotation();
    if (socket.connected) autoLogin();
})();
