using System;

namespace TrpgMaps
{
    /// <summary>
    /// 软件的"身份信息"：版本号、作者、仓库地址、更新源。
    ///
    /// **这里是版本号的唯一出处。** 其它任何地方（关于面板、安装包、升级包、
    /// 注册表、文件命名）都从这里取，不要再各写一份 —— 曾经因为版本号写了两处，
    /// 改了一处忘了另一处，"关于"里显示的和注册表里记的对不上。
    ///
    /// 同时 <c>Properties\AssemblyInfo.cs</c> 里的 AssemblyVersion 也要跟着改：
    /// 那是给 Windows 资源管理器的"详细信息"页看的，这里是给程序自己看的。
    /// </summary>
    internal static class AppInfo
    {
        /// <summary>显示名。</summary>
        public const string Name = "TrpgMaps";

        /// <summary>
        /// 当前版本号，**不带 v 前缀**。带前缀的写法统一用 <see cref="VersionText"/>。
        /// 只允许 "数字.数字.数字" 这种形式（更新比较是按段比数字，不支持 -beta 后缀）。
        /// </summary>
        public const string Version = "1.0.0";

        /// <summary>作者 / 发布者。</summary>
        public const string Author = "starnight-dirt";

        /// <summary>个人主页。</summary>
        public const string GitHubHome = "https://github.com/starnight-dirt";
        public const string GiteeHome = "https://gitee.com/starnight-dirt";

        /// <summary>源码仓库。</summary>
        public const string GitHubRepo = "https://github.com/starnight-dirt/TrpgMaps";
        public const string GiteeRepo = "https://gitee.com/starnight-dirt/TrpgMaps";

        /// <summary>作者所属组织名（注册表 Publisher 用）。</summary>
        public const string Publisher = "starnight-dirt";

        /// <summary>带 v 前缀的版本，界面上显示用。</summary>
        public static string VersionText { get { return "v" + Version; } }

        /// <summary>
        /// 便携版与安装版共用的"程序标识"。注册表、互斥量、数据目录都用它，
        /// 改名的话务必一起改（改完记得同步 tools 下的打包脚本）。
        /// </summary>
        public const string RegistryKey = "TrpgMaps";

        /// <summary>
        /// 工程目录**上传到 GitHub / Gitee 以后**在仓库里的名字。
        ///
        /// ⚠️ 本地开发目录叫 <c>win7版本</c>，而发布出去的源码包里它叫
        /// <c>win32</c> —— 更新清单就摆在这个目录里，所以这个名字必须和源码包
        /// 里的目录名一致（由 <c>scripts\make_release.ps1</c> 在打包时改名）。
        /// 想改它，要连着 Core\UpdateCheck.cs 里那两组候选地址一起改。
        /// </summary>
        public const string RepoDir = "win32";

        /// <summary>
        /// 更新清单文件名。**放在仓库的 <see cref="RepoDir"/> 目录里**
        /// （即 <c>&lt;repo&gt;/win32/version.json</c>），程序按这个文件名去拉。
        /// 之所以用独立的小 json 而不是解析 GitHub/Gitee 的 release 接口：
        /// 两家的接口返回格式不一样、还可能要 token，而一个小文件两边都能放。
        /// </summary>
        public const string ManifestFile = "version.json";

        /// <summary>发行包文件名前缀，例如 TrpgMaps_v1.0.0_win32_</summary>
        public static string ReleasePrefix { get { return Name + "_v" + Version + "_win32_"; } }

        /// <summary>升级包文件名。</summary>
        public static string UpdateFileName { get { return ReleasePrefix + "update.zip"; } }

        /// <summary>便携版包文件名。</summary>
        public static string PortableFileName { get { return ReleasePrefix + "portable.zip"; } }

        /// <summary>安装包文件名。</summary>
        public static string InstallerFileName { get { return ReleasePrefix + "installer.exe"; } }
    }
}
