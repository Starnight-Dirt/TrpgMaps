/*
 * 实时通道客户端，替代原项目的 socket.io。
 *
 * 服务器 -> 浏览器：SSE（GET /api/events），浏览器原生 EventSource。
 * 浏览器 -> 服务器：普通 POST（/api/login、/api/request_map）。
 *
 * 对外暴露的接口刻意做成 socket.io 的样子（on/emit/connected），
 * 这样 dm.js 与 player.html 里的业务代码几乎不用改。
 */
(function (global) {
    'use strict';

    const ROUTES = {
        player_login: '/api/login',
        request_map: '/api/request_map',
    };

    function createSocket() {
        let subscriptionId = null;
        let source = null;
        let connected = false;
        let closedByUser = false;
        let retryDelay = 1000;
        // 一条连接只报一次 connect：open 与 connected 是两条消息，会先后到达
        let announced = false;
        const handlers = Object.create(null);

        function dispatch(eventName, payload) {
            const list = handlers[eventName];
            if (!list) return;
            for (const fn of list.slice()) {
                try {
                    fn(payload);
                } catch (err) {
                    console.error('事件处理出错 ' + eventName, err);
                }
            }
        }

        function emitLocal(eventName, payload) {
            // 本地事件（connect/disconnect）走同一条分发路径
            dispatch(eventName, payload);
        }

        function newId() {
            return 's_' + Date.now().toString(36) + '_' + Math.random().toString(36).slice(2, 10);
        }

        function connect() {
            closedByUser = false;

            // 每次重连都换新 ID：服务器把 ID 当作"当前连接"，
            // 这样断线重连后旧连接会被自然清理。
            subscriptionId = newId();
            announced = false;
            const es = new EventSource('/api/events?id=' + encodeURIComponent(subscriptionId));
            source = es;

            es.addEventListener('open', () => markOpen(es));
            es.addEventListener('connected', () => markOpen(es));

            // 业务事件：与服务器端 Hub 里的事件名一一对应。
            // 这里是一张**白名单** —— 服务器发过来但没列在这儿的事件会被直接丢掉，
            // 所以新增事件时两边都要改。
            // （room_state / server_moved 以前漏掉了：锁房间和服务器换 IP 都不通知玩家，
            //   player.js 里那两个处理器形同虚设。）
            const names = ['connected', 'login_success', 'login_failed', 'map_change',
                'player_list', 'kicked', 'pong', 'room_state', 'server_moved', 'drawing_change'];
            for (const name of names) {
                es.addEventListener(name, (e) => {
                    let payload = null;
                    try {
                        payload = e.data ? JSON.parse(e.data) : null;
                    } catch (err) {
                        console.error('解析事件失败 ' + name, err);
                        return;
                    }
                    dispatch(name, payload);
                });
            }

            es.addEventListener('error', () => {
                // 这条连接已经被换掉了（reconnect() 或后一次 connect()），
                // 它的迟到事件必须丢掉 —— 否则收尾会掐掉正在用的新连接。
                if (source !== es) return;

                source = null;

                if (connected) {
                    connected = false;
                    emitLocal('disconnect');
                }

                // **不把重连交给 EventSource 的自动重连**：它复用同一个 ?id=，
                // 服务器会认为那是同一条连接，于是旧连接超时退出时的收尾
                // （注销这个 ID 的订阅 + 把这名玩家标记离线）会把刚建好的新连接
                // 一起带走 —— 表现是绿点闪一下就灰，或者干脆一直灰。
                // 我们自己重连（新 ID），旧连接的收尾就落在旧 ID 上，互不相干。
                try {
                    es.close();
                } catch (err) {
                    /* 已经关掉了 */
                }

                if (closedByUser) return;

                const delay = retryDelay;
                retryDelay = Math.min(retryDelay * 2, 10000);
                setTimeout(() => {
                    if (!closedByUser) connect();
                }, delay);
            });
        }

        /// 一条连接打通了（open 与 connected 只报一次）。
        function markOpen(es) {
            if (source !== es) return;        // 已被换掉的连接，忽略
            if (announced) return;
            announced = true;

            retryDelay = 1000;
            connected = true;
            // 每次都发：重连之后订阅 ID 变了，玩家端要靠这个事件重新登录，
            // 让服务器把"在线"挂到新的连接上。处理器是幂等的。
            emitLocal('connect');
        }

        function post(url, body) {
            return fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body || {}),
            }).then((res) => res.json().catch(() => ({})));
        }

        connect();

        return {
            get connected() {
                return connected;
            },
            get id() {
                return subscriptionId;
            },
            on(eventName, fn) {
                (handlers[eventName] || (handlers[eventName] = [])).push(fn);
                return this;
            },
            off(eventName, fn) {
                const list = handlers[eventName];
                if (!list) return this;
                const index = list.indexOf(fn);
                if (index >= 0) list.splice(index, 1);
                return this;
            },
            emit(eventName, payload) {
                const route = ROUTES[eventName];
                if (!route) {
                    console.warn('未定义的客户端事件：' + eventName);
                    return Promise.resolve({});
                }
                const body = Object.assign({}, payload || {});
                // 服务器用 id 定位这条事件流
                if (subscriptionId) body.id = subscriptionId;
                if (body.cookie_id !== undefined && body.cookieId === undefined) {
                    body.cookieId = body.cookie_id;
                }
                return post(route, body);
            },
            close() {
                closedByUser = true;
                connected = false;
                if (source) {
                    source.close();
                    source = null;
                }
            },
            /**
             * 换一条全新的连接（新 ID）重连。
             *
             * 页面从后台回到前台时用：浏览器把连接冻住过之后，服务器那边
             * 早就把我们标成离线了，而本页面看着还是"在线"。这时候**必须**
             * 换新 ID（理由见上面 error 处理里的注释），然后重新登录。
             *
             * 刻意不派发 disconnect：状态条不该因为一次无感的换线而闪一下。
             * 打通之后照常派发 connect，玩家端会在那里重新登录。
             */
            reconnect() {
                if (closedByUser) return;
                retryDelay = 1000;
                const old = source;
                source = null;
                try {
                    if (old) old.close();
                } catch (err) {
                    /* 已经关掉了 */
                }
                connect();
            },
        };
    }

    global.SSEIO = { create: createSocket };
    // 兼容原代码里的 io() 调用方式
    global.io = createSocket;
})(window);
