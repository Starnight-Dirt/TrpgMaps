using System;
using System.Runtime.InteropServices;

namespace TrpgMaps
{
    /// <summary>
    /// 内置音乐播放器的原生层封装（`TrpgAudio.dll`，源码见 `native\audio_bridge.c`）。
    ///
    /// 为什么这一版多了个 DLL：win7 版只能编 .NET 3.5、不能引托管库，而系统 API
    /// （MCI）只认得 MP3 / WAV —— 实测 OGG 和 M4A 一律打不开，FLAC 也不行。
    /// 所以自带一个**自研的原生小 DLL**（686 KB），里面装的是：
    ///   · miniaudio v0.11.25 —— 公共领域 / MIT-0，负责 WAV / MP3 / FLAC + 出声
    ///   · stb_vorbis      —— 公共领域，负责 OGG Vorbis
    /// 两个都是可商用的宽松许可，所以这个项目以后挂 MIT 也没问题（BASS 就不行，
    /// 它是"非商业免费"，跟着 MIT 项目分发会有授权冲突）。
    ///
    /// 支持情况（**都是 32 位真机实测过的**）：
    ///   mp3 / wav / flac / ogg  → 走 miniaudio 自带解码器（流式，内存占用小）
    ///   m4a / aac / wma         → 走 **Windows 自带的 Media Foundation 解码器**
    ///                             （整首解到内存，约 10 MB/分钟，见 native\mf_aac.cpp）
    /// 不支持的那些照样会出现在列表里，点开会明确提示，而不是干瞪眼。
    ///
    /// ⚠️ DLL 不在就整个降级：<see cref="Available"/> 为 false，界面显示"音频功能不可用"，
    /// 其余功能（地图 / 绘图 / 投屏）一点不受影响。
    /// </summary>
    internal static class AudioEngine
    {
        private const string Dll = "TrpgAudio.dll";

        // ---- 原生导出（**必须 Cdecl**：C 侧是 __cdecl，默认 Winapi=stdcall 会对不上）----
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr trpg_audio_version();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr trpg_audio_last_error();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int trpg_audio_init();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void trpg_audio_shutdown();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int trpg_audio_open(string filePath);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int trpg_audio_probe(string filePath);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void trpg_audio_close();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int trpg_audio_play();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int trpg_audio_pause();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void trpg_audio_set_looping(int loop);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int trpg_audio_is_playing();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int trpg_audio_at_end();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern float trpg_audio_length();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern float trpg_audio_position();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int trpg_audio_seek(float seconds);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void trpg_audio_set_volume(float volume);

        // ---------------------------------------------------------------- 探测

        private static int _probe = -1;          // -1 未探测 / 0 不可用 / 1 可用
        private static string _why = string.Empty;

        /// <summary>原生播放库能不能用（DLL 在不在、设备开不开得起来）。</summary>
        public static bool Available
        {
            get { return Probe() == 1; }
        }

        /// <summary>不能用时的原因（直接显示给用户）。</summary>
        public static string UnavailableReason
        {
            get { Probe(); return _why; }
        }

        /// <summary>原生库版本串（写进 app.log 用）。</summary>
        public static string Version
        {
            get { Probe(); return _version; }
        }

        private static string _version = string.Empty;

        private static int Probe()
        {
            if (_probe >= 0) return _probe;

            try
            {
                _version = Marshal.PtrToStringAnsi(trpg_audio_version());
                if (trpg_audio_init() == 0)
                {
                    _why = Loc.T("core.audio.device", LastError);
                    _probe = 0;
                }
                else
                {
                    _probe = 1;
                }
            }
            catch (DllNotFoundException)
            {
                _why = Loc.T("core.audio.no.dll");
                _probe = 0;
            }
            catch (Exception ex)
            {
                _why = Loc.T("core.audio.load", ex.Message);
                _probe = 0;
            }

            return _probe;
        }

        /// <summary>原生层最后一条错误（英文/半中文，直接给用户看够用）。</summary>
        public static string LastError
        {
            get
            {
                try
                {
                    var text = Marshal.PtrToStringAnsi(trpg_audio_last_error());
                    return string.IsNullOrEmpty(text) ? Loc.T("core.unknown") : text;
                }
                catch
                {
                    return Loc.T("core.unknown");
                }
            }
        }

        // ------------------------------------------------------------ 播放控制

        /// <summary>当前打开的文件（没有则空串）。</summary>
        public static string CurrentPath = string.Empty;

        /// <summary>这一首是不是循环（单曲循环模式用）。</summary>
        public static bool Loop
        {
            get { return _loop; }
        }

        private static bool _loop;

        public static void SetLoop(bool loop)
        {
            _loop = loop;
            if (!Available) return;
            try { trpg_audio_set_looping(loop ? 1 : 0); }
            catch { /* 忽略 */ }
        }

        /// <summary>打开一首曲子（停在第 0 秒，不自动播）。失败时返回原因。</summary>
        public static bool Open(string path, out string error)
        {
            error = null;
            if (!Available) { error = UnavailableReason; return false; }

            try
            {
                Close();
                if (trpg_audio_open(path) == 0)
                {
                    error = LastError;
                    return false;
                }
                CurrentPath = path;
                SetLoop(_loop);          // 新曲子也要带上当前的循环设置
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 探测一个文件能不能解码（列表里给不支持的格式标灰）。
        /// 原生层用的是**独立的 decoder**，不会碰当前正在播的那首。
        /// </summary>
        public static bool CanDecode(string path)
        {
            if (!Available) return false;
            try { return trpg_audio_probe(path) != 0; }
            catch { return false; }
        }

        public static void Close()
        {
            CurrentPath = string.Empty;
            if (!Available) return;
            try { trpg_audio_close(); }
            catch { /* 忽略 */ }
        }

        public static bool Play()
        {
            if (!Available) return false;
            try { return trpg_audio_play() != 0; }
            catch { return false; }
        }

        public static void Pause()
        {
            if (!Available) return;
            try { trpg_audio_pause(); }
            catch { /* 忽略 */ }
        }

        public static bool IsPlaying
        {
            get
            {
                if (!Available) return false;
                try { return trpg_audio_is_playing() != 0; }
                catch { return false; }
            }
        }

        /// <summary>放到头了（用来决定要不要切下一曲）。</summary>
        public static bool AtEnd
        {
            get
            {
                if (!Available) return false;
                try { return trpg_audio_at_end() != 0; }
                catch { return false; }
            }
        }

        /// <summary>时长（秒）。拿不到时返回 0，界面按"未知"处理。</summary>
        public static float Length
        {
            get
            {
                if (!Available) return 0f;
                try { return trpg_audio_length(); }
                catch { return 0f; }
            }
        }

        public static float Position
        {
            get
            {
                if (!Available) return 0f;
                try { return trpg_audio_position(); }
                catch { return 0f; }
            }
        }

        public static bool Seek(float seconds)
        {
            if (!Available) return false;
            try { return trpg_audio_seek(seconds) != 0; }
            catch { return false; }
        }

        /// <summary>音量 0–1（界面滑块就是这个范围）。</summary>
        public static float Volume
        {
            get { return _volume; }
        }

        private static float _volume = 0.8f;

        public static void SetVolume(float volume)
        {
            if (volume < 0f) volume = 0f;
            if (volume > 1f) volume = 1f;
            _volume = volume;
            if (!Available) return;
            try { trpg_audio_set_volume(volume); }
            catch { /* 忽略 */ }
        }

        /// <summary>退出前收摊（关设备）。</summary>
        public static void Shutdown()
        {
            if (!Available) return;
            try { trpg_audio_shutdown(); }
            catch { /* 忽略 */ }
        }
    }
}
