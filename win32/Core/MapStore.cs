using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TrpgMaps
{
    /// <summary>上传结果。</summary>
    internal sealed class UploadResult
    {
        public bool Success;
        public string Message;
        public MapInfo Data;
    }

    /// <summary>
    /// 地图仓库：扫描、上传（含 MD5 去重）、当前底图状态。
    /// 与原 Python 版 app.py 的 get_maps / upload_map / set_current_map 对应。
    /// </summary>
    internal sealed class MapStore
    {
        private readonly object _gate = new object();
        private string _currentMap;
        private string _fitMode = "fill";
        private int _rotation;
        private string _gridMode = "auto";

        public MapStore()
        {
            if (!Directory.Exists(AppEnv.MapsFolder)) Directory.CreateDirectory(AppEnv.MapsFolder);
        }

        public string CurrentMap
        {
            get { lock (_gate) { return _currentMap; } }
        }

        public string FitMode
        {
            get { lock (_gate) { return _fitMode; } }
        }

        /// <summary>
        /// 底图顺时针旋转角（0 / 90 / 180 / 270）。
        ///
        /// 这是**全局显示状态**，不跟着单张底图走：桌子摆好之后方向就定了，
        /// 换底图时不该再转回来。网格不参与旋转 —— 转的只是底图图片本身。
        /// </summary>
        public int Rotation
        {
            get { lock (_gate) { return _rotation; } }
        }

        /// <summary>顺时针再转 delta 度（只认 90 的整数倍），返回新角度。</summary>
        public int Rotate(int delta)
        {
            lock (_gate)
            {
                _rotation = NormalizeRotation(_rotation + delta);
                return _rotation;
            }
        }

        /// <summary>把任意角度规整到 0 / 90 / 180 / 270；非 90 的整数倍一律当 0。</summary>
        public static int NormalizeRotation(int degrees)
        {
            var value = degrees % 360;
            if (value < 0) value += 360;
            return value % 90 == 0 ? value : 0;
        }

        /// <summary>
        /// 网格线颜色模式：<c>auto</c>（跟随底图自动切换）/ <c>white</c> / <c>black</c>。
        ///
        /// 和旋转角一样，这是**全局显示状态**而不是单张底图的属性：DM 用的是一个
        /// 投屏画面，网格线颜色定了就是定了，换底图不该莫名其妙变回去
        /// （auto 模式下当然会随底图重新判定，那正是它该做的事）。
        /// </summary>
        public string GridMode
        {
            get { lock (_gate) { return _gridMode; } }
        }

        /// <summary>设置网格颜色模式，返回归一化后的值（非法值一律当 auto）。</summary>
        public string SetGridMode(string mode)
        {
            var normalized = NormalizeGridMode(mode);
            lock (_gate) { _gridMode = normalized; }
            return normalized;
        }

        /// <summary>把任意输入规整成 auto / white / black。</summary>
        public static string NormalizeGridMode(string mode)
        {
            if (string.Equals(mode, "white", StringComparison.OrdinalIgnoreCase)) return "white";
            if (string.Equals(mode, "black", StringComparison.OrdinalIgnoreCase)) return "black";
            return "auto";
        }

        /// <summary>当前底图在本机的完整路径；没有底图时返回 null。</summary>
        public string CurrentMapPath
        {
            get
            {
                lock (_gate)
                {
                    return string.IsNullOrEmpty(_currentMap)
                        ? null
                        : Path.Combine(AppEnv.MapsFolder, _currentMap);
                }
            }
        }

        /// <summary>
        /// 当前底图的色调采样。没有底图 / 分析失败时 <c>Ok = false</c>。
        /// 界面用它把"自动判定成了什么、依据是什么"显示出来。
        /// </summary>
        public ToneSample CurrentTone()
        {
            var path = CurrentMapPath;
            return string.IsNullOrEmpty(path) ? default(ToneSample) : MapTone.Analyze(path);
        }

        /// <summary>
        /// 解析出**实际要画**的网格线颜色：<c>white</c> 或 <c>black</c>。
        ///
        /// auto 时的规则见 <see cref="MapTone"/>：底图平均亮度 ≥ 0.5 用黑线，
        /// 否则用白线；**没有底图时用白线**（画布底色是深灰 #1A1A1A）。
        /// </summary>
        public string ResolveGridColor()
        {
            string mode;
            string name;
            lock (_gate)
            {
                mode = _gridMode;
                name = _currentMap;
            }

            if (mode == "white" || mode == "black") return mode;
            if (string.IsNullOrEmpty(name)) return "white";

            var sample = MapTone.Analyze(Path.Combine(AppEnv.MapsFolder, name));
            if (!sample.Ok) return "white";
            return MapTone.PrefersWhite(sample) ? "white" : "black";
        }

        public bool SetCurrent(string name, string mode)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var safe = Path.GetFileName(name);
            if (!AppEnv.IsAllowedImage(safe)) return false;
            var full = Path.Combine(AppEnv.MapsFolder, safe);
            if (!File.Exists(full)) return false;

            lock (_gate)
            {
                _currentMap = safe;
                if (!string.IsNullOrEmpty(mode)) _fitMode = mode;
                if (_fitMode != "contain") _fitMode = "fill";
            }
            return true;
        }

        /// <summary>
        /// 撤掉当前底图：回到"刚打开程序、只有网格没有底图"的状态。
        ///
        /// 只清"当前选中"，**不动 maps\ 里的文件**（列表还在，随时能再选回来）。
        /// 显示模式、旋转角、网格颜色都保留 —— 和它们一样，这三者是全局显示状态，
        /// 撤掉一张图不该把它们一起重置。
        /// </summary>
        public void ClearCurrent()
        {
            lock (_gate)
            {
                _currentMap = null;
            }
        }

        /// <summary>列出全部底图（附带 MD5）。</summary>
        public List<MapInfo> List()
        {
            var result = new List<MapInfo>();
            if (!Directory.Exists(AppEnv.MapsFolder)) return result;

            foreach (var file in Directory.GetFiles(AppEnv.MapsFolder))
            {
                var name = Path.GetFileName(file);
                if (!AppEnv.IsAllowedImage(name)) continue;
                result.Add(new MapInfo(name, file, GetMd5(file), BuildUrl(name)));
            }
            return result;
        }

        /// <summary>取某张底图的完整路径（不存在或非法返回 null）。</summary>
        public static string ResolveImagePath(string fileName)
        {
            var safe = Path.GetFileName(fileName);
            if (!AppEnv.IsAllowedImage(safe)) return null;
            var full = Path.Combine(AppEnv.MapsFolder, safe);
            return File.Exists(full) ? full : null;
        }

        public static string BuildUrl(string name)
        {
            return "/api/maps/image/" + Uri.EscapeDataString(name);
        }

        /// <summary>上传底图：校验扩展名 → 文件名冲突 → MD5 去重 → 落盘。</summary>
        public UploadResult Upload(string fileName, byte[] content)
        {
            if (string.IsNullOrEmpty(fileName))
                return Fail("文件名为空");
            if (!AppEnv.IsAllowedImage(fileName))
                return Fail("不支持的格式");

            var safe = Path.GetFileName(fileName);
            var savePath = Path.Combine(AppEnv.MapsFolder, safe);
            if (File.Exists(savePath))
                return Fail("文件名已存在");

            var tempPath = savePath + ".tmp";
            try
            {
                File.WriteAllBytes(tempPath, content);
                var md5 = GetMd5(tempPath);

                foreach (var existing in Directory.GetFiles(AppEnv.MapsFolder))
                {
                    var existingName = Path.GetFileName(existing);
                    if (!AppEnv.IsAllowedImage(existingName)) continue;
                    if (existingName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    if (GetMd5(existing) == md5)
                    {
                        File.Delete(tempPath);
                        return Fail("图片内容重复（MD5相同）");
                    }
                }

                if (File.Exists(savePath)) File.Delete(savePath);
                File.Move(tempPath, savePath);

                var result = new UploadResult { Success = true, Message = "上传成功" };
                result.Data = new MapInfo(safe, savePath, md5, BuildUrl(safe));
                return result;
            }
            catch (Exception ex)
            {
                AppLog.Write("上传底图失败：" + safe, ex);
                TryDelete(tempPath);
                return Fail("上传失败：" + ex.Message);
            }
        }

        private static UploadResult Fail(string message)
        {
            return new UploadResult { Success = false, Message = message, Data = null };
        }

        /// <summary>计算文件 MD5（十六进制小写）。</summary>
        public static string GetMd5(string filePath)
        {
            using (var md5 = MD5.Create())
            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var hash = md5.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* 忽略 */ }
        }
    }
}
