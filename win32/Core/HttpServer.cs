using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TrpgMaps
{
    /// <summary>解析后的一次 HTTP 请求。</summary>
    internal sealed class HttpRequest
    {
        public string Method = "GET";
        public string RawPath = "/";
        public string Path = "/";
        public string Query = string.Empty;
        public Dictionary<string, string> Headers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];
        public string RemoteIp = "unknown";

        public string GetQuery(string name)
        {
            if (string.IsNullOrEmpty(Query)) return null;
            foreach (var pair in Query.Split('&'))
            {
                if (pair.Length == 0) continue;
                var eq = pair.IndexOf('=');
                var key = eq < 0 ? pair : pair.Substring(0, eq);
                if (!string.Equals(Uri.UnescapeDataString(key), name, StringComparison.Ordinal)) continue;
                var value = eq < 0 ? string.Empty : pair.Substring(eq + 1);
                return Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            return null;
        }

        public string GetHeader(string name)
        {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
        }

        public string BodyText
        {
            get { return Body.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Body); }
        }

        /// <summary>把请求体当 JSON 对象解析（不是对象则返回 null）。</summary>
        public Dictionary<string, object> BodyJson()
        {
            return MiniJson.ParseObject(BodyText);
        }
    }

    /// <summary>一次连接的写出口（普通响应 / SSE 都从这里走）。</summary>
    internal sealed class RequestContext
    {
        public readonly HttpRequest Request;
        public readonly NetworkStream Stream;
        public readonly TcpClient Client;
        public readonly string ConnectionId;

        private readonly object _writeGate = new object();

        public RequestContext(HttpRequest request, NetworkStream stream, TcpClient client, string connectionId)
        {
            Request = request;
            Stream = stream;
            Client = client;
            ConnectionId = connectionId;
        }

        /// <summary>请求是否来自本机回环地址（DM 专用接口的判定）。</summary>
        public bool IsLocal
        {
            get
            {
                var ip = Request.RemoteIp;
                return ip == "127.0.0.1" || ip == "::1" || ip == "0.0.0.0" || ip == "unknown";
            }
        }

        /// <summary>客户端是否还连着（用于 SSE 循环退出）。</summary>
        public bool ClientAlive
        {
            get
            {
                try
                {
                    var socket = Client.Client;
                    if (socket == null || !socket.Connected) return false;
                    return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
                }
                catch
                {
                    return false;
                }
            }
        }

        public void WriteResponse(int status, string contentType, byte[] body, bool keepAlive)
        {
            WriteResponse(status, contentType, body, keepAlive, null);
        }

        public void WriteResponse(int status, string contentType, byte[] body, bool keepAlive,
            Dictionary<string, string> extraHeaders)
        {
            if (body == null) body = new byte[0];

            var header = new StringBuilder();
            header.Append("HTTP/1.1 ").Append(status).Append(' ').Append(StatusText(status)).Append("\r\n");
            header.Append("Content-Type: ").Append(contentType).Append("\r\n");
            header.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            header.Append("Cache-Control: no-cache\r\n");
            header.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
            if (extraHeaders != null)
            {
                foreach (var pair in extraHeaders)
                {
                    header.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
                }
            }
            header.Append("\r\n");

            lock (_writeGate)
            {
                var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
                Stream.Write(headerBytes, 0, headerBytes.Length);
                if (body.Length > 0) Stream.Write(body, 0, body.Length);
                Stream.Flush();
            }
        }

        public void WriteText(int status, string text)
        {
            WriteResponse(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(text), false);
        }

        public void WriteSseHeaders()
        {
            var header = new StringBuilder();
            header.Append("HTTP/1.1 200 OK\r\n");
            header.Append("Content-Type: text/event-stream; charset=utf-8\r\n");
            header.Append("Cache-Control: no-cache, no-store\r\n");
            header.Append("Connection: keep-alive\r\n");
            header.Append("X-Accel-Buffering: no\r\n");
            header.Append("\r\n");

            lock (_writeGate)
            {
                var bytes = Encoding.ASCII.GetBytes(header.ToString());
                Stream.Write(bytes, 0, bytes.Length);
                Stream.Flush();
            }
        }

        /// <summary>写一段原始的 SSE 文本（心跳注释等）。</summary>
        public bool WriteSseRaw(string text)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                lock (_writeGate)
                {
                    Stream.Write(bytes, 0, bytes.Length);
                    Stream.Flush();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool WriteSseEvent(string eventName, string json)
        {
            return WriteSseRaw("event: " + eventName + "\ndata: " + json + "\n\n");
        }

        private static string StatusText(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 409: return "Conflict";
                case 500: return "Internal Server Error";
                default: return "OK";
            }
        }
    }

    /// <summary>
    /// 内置 Web 服务器：基于 TcpListener 手写的极简 HTTP/1.1。
    ///
    /// 为什么不用 HttpListener：在 Windows 上绑定 "+:port" 这类非 localhost 前缀需要
    /// 管理员权限或 netsh urlacl 预留，而本工具希望普通用户双击即可用。
    /// TcpListener 监听端口不需要任何特权。
    ///
    /// 只支持本项目用到的能力：GET / POST、Content-Length 请求体、普通响应与 SSE 流。
    /// </summary>
    internal sealed class HttpServer : IDisposable
    {
        /// <summary>返回 true 表示保持长连接（SSE 自行接管流，返回值被忽略）。</summary>
        public delegate bool Handler(RequestContext ctx);

        private readonly Handler _handler;
        private readonly object _gate = new object();

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        public HttpServer(Handler handler)
        {
            _handler = handler;
        }

        public bool IsRunning
        {
            get { return _running; }
        }

        public void Start(int port)
        {
            lock (_gate)
            {
                if (_running) return;

                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                _listener = listener;
                _running = true;

                _acceptThread = new Thread(AcceptLoop);
                _acceptThread.IsBackground = true;
                _acceptThread.Name = "http-accept";
                _acceptThread.Start();
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _running = false;
                if (_listener != null)
                {
                    try { _listener.Stop(); }
                    catch { /* 忽略 */ }
                    _listener = null;
                }
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch
                {
                    break; // 监听器被关闭
                }

                var worker = new Thread(HandleClient);
                worker.IsBackground = true;
                worker.Name = "http-conn";
                try { worker.Start(client); }
                catch { try { client.Close(); } catch { } }
            }
        }

        private void HandleClient(object state)
        {
            var client = (TcpClient)state;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 30000;
                var stream = client.GetStream();
                var remoteIp = GetRemoteIp(client);

                while (_running)
                {
                    HttpRequest request;
                    if (!ReadRequest(stream, remoteIp, out request)) break;
                    if (request == null) break;

                    var ctx = new RequestContext(request, stream, client, Guid.NewGuid().ToString("N"));
                    var keepAlive = false;

                    try
                    {
                        keepAlive = _handler(ctx);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("处理请求出错 " + request.Method + " " + request.RawPath, ex);
                        try { ctx.WriteText(500, "{\"ok\":false,\"msg\":\"server error\"}"); }
                        catch { /* 忽略 */ }
                        keepAlive = false;
                    }

                    if (!keepAlive) break;
                }
            }
            catch
            {
                // 连接层异常直接丢弃这条连接
            }
            finally
            {
                try { client.Close(); }
                catch { /* 忽略 */ }
            }
        }

        private static string GetRemoteIp(TcpClient client)
        {
            try
            {
                var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                if (endpoint == null) return "unknown";
                // 监听的是 IPAddress.Any（IPv4），因此这里拿到的基本都是 IPv4 地址
                return endpoint.Address.ToString();
            }
            catch
            {
                return "unknown";
            }
        }

        private static bool ReadRequest(NetworkStream stream, string remoteIp, out HttpRequest request)
        {
            request = null;

            var requestLine = ReadLine(stream);
            if (requestLine == null) return false;

            // 客户端有时会先发一个空行
            while (requestLine.Length == 0)
            {
                requestLine = ReadLine(stream);
                if (requestLine == null) return false;
            }

            var parts = requestLine.Split(' ');
            if (parts.Length < 3) return false;

            var result = new HttpRequest
            {
                Method = parts[0].ToUpperInvariant(),
                RawPath = parts[1],
                RemoteIp = remoteIp
            };

            var queryIndex = result.RawPath.IndexOf('?');
            if (queryIndex >= 0)
            {
                result.Query = result.RawPath.Substring(queryIndex + 1);
                result.Path = result.RawPath.Substring(0, queryIndex);
            }
            else
            {
                result.Path = result.RawPath;
            }
            result.Path = Uri.UnescapeDataString(result.Path);

            // 头部
            while (true)
            {
                var line = ReadLine(stream);
                if (line == null) return false;
                if (line.Length == 0) break;

                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                result.Headers[name] = value;
            }

            // 请求体
            var contentLength = 0;
            var lengthHeader = result.GetHeader("Content-Length");
            if (!string.IsNullOrEmpty(lengthHeader))
            {
                int.TryParse(lengthHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
            }

            if (contentLength > 0)
            {
                // 防止异常大的请求把内存吃光
                if (contentLength > 64 * 1024 * 1024) return false;

                var body = new byte[contentLength];
                var read = 0;
                while (read < contentLength)
                {
                    var n = stream.Read(body, read, contentLength - read);
                    if (n <= 0) return false;
                    read += n;
                }
                result.Body = body;
            }

            request = result;
            return true;
        }

        /// <summary>按 CRLF 读一行（只用于请求行与头部，都是 ASCII）。</summary>
        private static string ReadLine(NetworkStream stream)
        {
            var buffer = new List<byte>(128);
            while (true)
            {
                var b = stream.ReadByte();
                if (b < 0)
                {
                    return buffer.Count == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());
                }
                if (b == '\n')
                {
                    if (buffer.Count > 0 && buffer[buffer.Count - 1] == (byte)'\r')
                        buffer.RemoveAt(buffer.Count - 1);
                    return Encoding.ASCII.GetString(buffer.ToArray());
                }
                if (buffer.Count > 8192) return null; // 头部异常长，直接放弃
                buffer.Add((byte)b);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
