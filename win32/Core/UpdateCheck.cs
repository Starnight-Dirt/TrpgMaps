using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;

namespace TrpgMaps
{
    /// <summary>仓库根目录下 <c>version.json</c> 的内容。</summary>
    internal sealed class UpdateManifest
    {
        /// <summary>仓库里最新的版本号，不带 v。</summary>
        public string Version = string.Empty;

        /// <summary>更新说明，弹窗里显示一行。</summary>
        public string Notes = string.Empty;

        /// <summary>升级包文件名（形如 TrpgMaps_v1.0.0_win32_update.zip）。</summary>
        public string UpdateFile = string.Empty;

        /// <summary>可选：升级包的完整地址。填了就不再按仓库规则拼 URL。</summary>
        public string UpdateUrl = string.Empty;

        /// <summary>强制更新（用户点了"下次一定"也还会提示）。</summary>
        public bool Mandatory;

        public static UpdateManifest Parse(string json)
        {
            var obj = MiniJson.ParseObject(json);
            if (obj == null) return null;

            var m = new UpdateManifest();
            m.Version = MiniJson.GetString(obj, "version");
            m.Notes = MiniJson.GetString(obj, "notes");
            m.UpdateFile = MiniJson.GetString(obj, "update");
            m.UpdateUrl = MiniJson.GetString(obj, "updateUrl");
            m.Mandatory = MiniJson.GetBool(obj, "mandatory", false);

            if (string.IsNullOrEmpty(m.Version)) return null;
            return m;
        }
    }

    /// <summary>一次检查更新的结果。</summary>
    internal sealed class UpdateResult
    {
        public bool Ok;
        public string Error = string.Empty;

        /// <summary>清单拉取成功的源："gitee" 或 "github"。</summary>
        public string Source = string.Empty;

        /// <summary>清单里的版本比本机高时为 true（且不是"不再提示"过的版本）。</summary>
        public bool HasUpdate;
        public UpdateManifest Manifest;
    }

    /// <summary>
    /// 更新检测与下载。
    ///
    /// 走的是"仓库里放一个小 json"的路子（<see cref="AppInfo.ManifestFile"/>），
    /// **不解析 Gitee/GitHub 的 release 接口**：两家的接口字段不一样、还动不动
    /// 要 token 或限流，而一个小文件两家都能放、都是纯 HTTPS GET，实现能共用一份。
    ///
    /// 源的选择：**先 Gitee，全挂了再 GitHub**（用户在国内，Gitee 基本一拉就通；
    /// GitHub raw 在部分网络下会超时，所以放兜底）。
    ///
    /// ⚠️ 关于 HTTPS：这里是**开着证书校验**的（没有 `ServerCertificateValidationCallback`
    /// 放行）。因为最终要下载并**执行**升级包里的程序，关掉校验等于给中间人开了后门。
    /// 代价是老 Win7 如果没打过根证书更新，可能连不上 —— 那个用 Windows Update 装一次
    /// 根证书就能好，README 里写了。
    /// </summary>
    internal static class UpdateCheck
    {
        private const int TimeoutMs = 8000;

        private static bool _tlsConfigured;

        /// <summary>
        /// 清单在仓库里的相对路径：<c>win32/version.json</c>。
        /// 工程目录在发布出去的源码包里叫 win32（见 <see cref="AppInfo.RepoDir"/>）。
        /// </summary>
        private static string ManifestPath
        {
            get { return AppInfo.RepoDir + "/" + AppInfo.ManifestFile; }
        }

        /// <summary>Gitee 上的候选地址，按顺序试。</summary>
        private static string[] GiteeCandidates()
        {
            return new[]
            {
                AppInfo.GiteeRepo + "/raw/master/" + ManifestPath,
                AppInfo.GiteeRepo + "/raw/main/" + ManifestPath,
                // 旧布局兜底：清单曾经直接摆在仓库根目录里，留一条不吃亏。
                AppInfo.GiteeRepo + "/raw/master/" + AppInfo.ManifestFile
            };
        }

        /// <summary>GitHub 上的候选地址（兜底）。</summary>
        private static string[] GitHubCandidates()
        {
            const string raw = "https://raw.githubusercontent.com/starnight-dirt/TrpgMaps/";
            return new[]
            {
                raw + "master/" + ManifestPath,
                raw + "main/" + ManifestPath,
                raw + "master/" + AppInfo.ManifestFile
            };
        }

        // ---------------------------------------------------------------- 检查

        /// <summary>
        /// 拉清单并比对版本。**会阻塞**（最长 8 秒 × 6 个候选），
        /// 必须放到后台线程里调，不要在 UI 线程上调。
        /// </summary>
        public static UpdateResult Query()
        {
            var result = new UpdateResult();
            var errors = new List<string>();

            var sources = new[]
            {
                new KeyValuePair<string, string[]>("gitee", GiteeCandidates()),
                new KeyValuePair<string, string[]>("github", GitHubCandidates())
            };

            foreach (var source in sources)
            {
                foreach (var url in source.Value)
                {
                    string text;
                    try
                    {
                        text = Get(url);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(source.Key + ": " + ex.Message);
                        continue;
                    }

                    var manifest = UpdateManifest.Parse(text);
                    if (manifest == null)
                    {
                        errors.Add(source.Key + ": 清单格式不对");
                        continue;
                    }

                    result.Ok = true;
                    result.Source = source.Key;
                    result.Manifest = manifest;
                    result.HasUpdate = IsNewer(manifest.Version, AppInfo.Version) &&
                                       !string.Equals(manifest.Version, AppRegistry.GetSkippedVersion(),
                                           StringComparison.OrdinalIgnoreCase);
                    return result;
                }
            }

            result.Error = errors.Count == 0 ? "没有可用的更新源。" : string.Join("；", errors.ToArray());
            return result;
        }

        /// <summary>远端版本是否比本机新。只比"数字.数字.数字"，后缀一律忽略。</summary>
        public static bool IsNewer(string remote, string local)
        {
            return CompareVersions(remote, local) > 0;
        }

        public static int CompareVersions(string a, string b)
        {
            var pa = SplitVersion(a);
            var pb = SplitVersion(b);
            var n = Math.Max(pa.Length, pb.Length);
            for (var i = 0; i < n; i++)
            {
                var x = i < pa.Length ? pa[i] : 0;
                var y = i < pb.Length ? pb[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }

        private static int[] SplitVersion(string text)
        {
            if (string.IsNullOrEmpty(text)) return new int[0];
            var parts = text.Trim().TrimStart('v', 'V').Split('.');
            var list = new List<int>();
            foreach (var part in parts)
            {
                var digits = string.Empty;
                foreach (var ch in part)
                {
                    if (ch >= '0' && ch <= '9') digits += ch;
                    else break;                     // "1-beta" 这种后缀直接截断
                }
                int value;
                if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    value = 0;
                list.Add(value);
            }
            return list.ToArray();
        }

        // ---------------------------------------------------------------- 下载

        /// <summary>
        /// 按"先 Gitee 后 GitHub"的顺序把升级包下到 <paramref name="destFile"/>。
        /// 两个源都失败才算失败。
        /// </summary>
        public static bool DownloadUpdate(UpdateManifest manifest, string destFile, out string source, out string error)
        {
            source = string.Empty;
            error = string.Empty;
            var errors = new List<string>();

            var urls = new List<string>();
            if (!string.IsNullOrEmpty(manifest.UpdateUrl))
            {
                urls.Add(manifest.UpdateUrl);
            }
            else
            {
                var file = manifest.UpdateFile;
                if (string.IsNullOrEmpty(file)) file = AppInfo.UpdateFileName;

                var tag = "v" + manifest.Version;
                urls.Add(AppInfo.GiteeRepo + "/releases/download/" + tag + "/" + file);
                urls.Add(AppInfo.GitHubRepo + "/releases/download/" + tag + "/" + file);
            }

            for (var i = 0; i < urls.Count; i++)
            {
                try
                {
                    Download(urls[i], destFile);
                    source = i == 0 && string.IsNullOrEmpty(manifest.UpdateUrl) ? "gitee" : urls[i];
                    return true;
                }
                catch (Exception ex)
                {
                    errors.Add(ex.Message);
                }
            }

            error = string.Join("；", errors.ToArray());
            return false;
        }

        // ---------------------------------------------------------------- 应用

        /// <summary>
        /// 解包升级包到一个临时目录，然后启动里面的升级程序接管后续动作，本程序随即退出。
        ///
        /// **必须由升级程序在进程外做替换**：正在运行的 exe 换不掉自己。
        /// 升级程序会等本进程退出、覆盖文件、按清单删文件、安装版再修注册表，最后重启本程序。
        /// </summary>
        public static bool StageAndLaunch(string zipPath, bool installed, out string error)
        {
            error = string.Empty;

            try
            {
                var stage = Path.Combine(Path.GetTempPath(),
                    "TrpgMaps_update_" + AppInfo.Version + "_" + Environment.TickCount.ToString("x"));

                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                Directory.CreateDirectory(stage);

                var bytes = File.ReadAllBytes(zipPath);
                MiniZip.ExtractTo(bytes, stage, true);

                var updater = FindUpdater(stage);
                if (updater == null)
                {
                    error = "升级包里没有找到升级程序 TrpgMapsUpdater.exe，已中止（请到发布页手动下载安装包）。";
                    return false;
                }

                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = updater;
                psi.Arguments = "--apply " + Quote(stage) + " " + Quote(AppEnv.Root) + (installed ? " --installed" : "");
                psi.WorkingDirectory = stage;
                psi.UseShellExecute = false;    // 句柄不继承，避免调用方等它退出
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                error = "准备升级失败：" + ex.Message;
                AppLog.Write("准备升级失败", ex);
                return false;
            }
        }

        /// <summary>升级程序可能在包根，也可能在工具箱目录里，两处都找一下。</summary>
        private static string FindUpdater(string stage)
        {
            var root = Path.Combine(stage, "TrpgMapsUpdater.exe");
            if (File.Exists(root)) return root;

            var nested = Path.Combine(stage, "tools\\TrpgMapsUpdater.exe");
            if (File.Exists(nested)) return nested;

            var found = Directory.GetFiles(stage, "TrpgMapsUpdater.exe", SearchOption.AllDirectories);
            return found.Length > 0 ? found[0] : null;
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        /// <summary>
        /// 把升级包里的"删除清单 + 删除脚本"导出到临时目录，方便用户事后手工核对
        /// （自动升级走的是升级程序，不需要这些；它们存在的意义是"看得见"和"能手动重做"）。
        /// </summary>
        public static string ReadTextEntry(string zipPath, string entryName)
        {
            try
            {
                var bytes = File.ReadAllBytes(zipPath);
                var entries = MiniZip.ReadEntries(bytes);
                foreach (var entry in entries)
                {
                    if (string.Equals(entry.Name, entryName, StringComparison.OrdinalIgnoreCase))
                    {
                        var raw = MiniZip.ReadEntry(bytes, entry);
                        try { return new System.Text.UTF8Encoding(false, true).GetString(raw); }
                        catch { return System.Text.Encoding.Default.GetString(raw); }
                    }
                }
            }
            catch
            {
                // 读不到就算了：这只是给界面显示用的
            }
            return string.Empty;
        }

        // ---------------------------------------------------------------- HTTP

        private static void ConfigureTls()
        {
            if (_tlsConfigured) return;
            _tlsConfigured = true;

            try
            {
                // .NET 3.5 的默认协议是 Ssl3|Tls1.0，而 Gitee/GitHub 早就只认 TLS1.2 了。
                // 这里用枚举的**数值** 3072 直接或进去（3.5 里没有 Tls12 这个名字），
                // 老系统上没有 TLS1.2 的话 schannel 会自己忽略，不会更糟。
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            }
            catch
            {
                // 忽略：某些精简系统上这个属性只读
            }

            ServicePointManager.DefaultConnectionLimit = 4;
        }

        /// <summary>把 HTTP 头里的 <c>Content-Disposition</c> 之类都忽略掉，这里只做基本校验。</summary>
        private static HttpWebRequest CreateRequest(string url)
        {
            ConfigureTls();

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "TrpgMaps/" + AppInfo.Version + " (Windows)";
            request.Timeout = TimeoutMs;
            request.ReadWriteTimeout = TimeoutMs;
            request.AllowAutoRedirect = true;
            request.KeepAlive = false;
            return request;
        }

        private static string Get(string url)
        {
            using (var response = (HttpWebResponse)CreateRequest(url).GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static void Download(string url, string destFile)
        {
            using (var response = (HttpWebResponse)CreateRequest(url).GetResponse())
            using (var input = response.GetResponseStream())
            using (var output = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    output.Write(buffer, 0, read);
                }
            }

            // 包太小基本可以断定不是升级包（比如拿到了一个 HTML 错误页）
            var length = new FileInfo(destFile).Length;
            if (length < 1024)
            {
                try { File.Delete(destFile); } catch { }
                throw new IOException("下载到的文件只有 " + length + " 字节，不是有效的升级包。");
            }
        }

        /// <summary>后台跑一次检查，结果通过回调回到调用线程。</summary>
        public static void QueryAsync(Action<UpdateResult> callback)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateResult result;
                try
                {
                    result = Query();
                }
                catch (Exception ex)
                {
                    result = new UpdateResult { Ok = false, Error = ex.Message };
                }
                if (callback != null) callback(result);
            });
        }

        /// <summary>后台下载升级包，进度 0-1（拿不到总长度时是 -1）。</summary>
        public static void DownloadAsync(UpdateManifest manifest, string destFile,
            Action<bool, string, string> callback)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string source, error;
                var ok = DownloadUpdate(manifest, destFile, out source, out error);
                if (callback != null) callback(ok, source, error);
            });
        }
    }
}
