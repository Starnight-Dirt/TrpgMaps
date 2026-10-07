using System;
using System.Collections.Generic;
using System.Threading;

namespace TrpgMaps
{
    /// <summary>要推给某个浏览器连接的一条 SSE 事件。</summary>
    internal sealed class SseMessage
    {
        public string Event;
        public string Data;
    }

    /// <summary>一个在线事件流（对应一个浏览器标签页）。</summary>
    internal sealed class RealtimeSubscription
    {
        private readonly Queue<SseMessage> _queue = new Queue<SseMessage>();
        private readonly object _gate = new object();
        private readonly ManualResetEvent _signal = new ManualResetEvent(false);

        public readonly string Id;
        public volatile bool Closed;

        public RealtimeSubscription(string id)
        {
            Id = id;
        }

        public void Push(string eventName, string json)
        {
            if (Closed) return;
            lock (_gate)
            {
                // 队列积压说明这个客户端已经跟不上了，丢掉最旧的
                while (_queue.Count >= 256) _queue.Dequeue();
                _queue.Enqueue(new SseMessage { Event = eventName, Data = json });
            }
            try { _signal.Set(); }
            catch { /* 已释放 */ }
        }

        /// <summary>等待一条事件，超时返回 null。</summary>
        public SseMessage Wait(int timeoutMs)
        {
            if (Closed) return null;

            var msg = TryTake();
            if (msg != null) return msg;

            try { _signal.WaitOne(timeoutMs, false); }
            catch { return null; }

            return TryTake();
        }

        private SseMessage TryTake()
        {
            lock (_gate)
            {
                if (_queue.Count == 0) return null;
                var msg = _queue.Dequeue();
                if (_queue.Count == 0)
                {
                    try { _signal.Reset(); }
                    catch { /* 已释放 */ }
                }
                return msg;
            }
        }

        public void Close()
        {
            Closed = true;
            try { _signal.Set(); }
            catch { /* 已释放 */ }
        }
    }

    /// <summary>
    /// 实时推送中心。替代原 Python 版的 Flask-SocketIO：
    /// 每个浏览器标签页订阅一条 SSE 流，服务器按需广播或定向推送。
    /// </summary>
    internal sealed class RealtimeHub
    {
        private readonly Dictionary<string, RealtimeSubscription> _subs =
            new Dictionary<string, RealtimeSubscription>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public RealtimeSubscription Subscribe(string id)
        {
            var sub = new RealtimeSubscription(id);
            RealtimeSubscription old;
            lock (_gate)
            {
                if (_subs.TryGetValue(id, out old)) old.Close();
                _subs[id] = sub;
            }
            return sub;
        }

        public void Unsubscribe(string id)
        {
            RealtimeSubscription sub;
            lock (_gate)
            {
                if (_subs.TryGetValue(id, out sub)) _subs.Remove(id);
                else sub = null;
            }
            if (sub != null) sub.Close();
        }

        public void Broadcast(string eventName, string json)
        {
            List<RealtimeSubscription> targets;
            lock (_gate)
            {
                targets = new List<RealtimeSubscription>(_subs.Values);
            }
            foreach (var sub in targets) sub.Push(eventName, json);
        }

        public void SendTo(string id, string eventName, string json)
        {
            if (string.IsNullOrEmpty(id)) return;
            RealtimeSubscription target;
            lock (_gate)
            {
                _subs.TryGetValue(id, out target);
            }
            if (target != null) target.Push(eventName, json);
        }

        public int Count
        {
            get { lock (_gate) { return _subs.Count; } }
        }

        /// <summary>关闭所有连接（换 IP/端口重启服务时用）。</summary>
        public void CloseAll()
        {
            List<RealtimeSubscription> all;
            lock (_gate)
            {
                all = new List<RealtimeSubscription>(_subs.Values);
                _subs.Clear();
            }
            foreach (var sub in all) sub.Close();
        }
    }
}
