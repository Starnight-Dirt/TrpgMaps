using System.Reflection;
using System.Runtime.InteropServices;

// ⚠️ 版本号有两处，改一处必须改另一处：
//   * 这里（AssemblyVersion / AssemblyFileVersion）—— Windows 资源管理器里
//     "属性 → 详细信息"和任务管理器显示的就是它；
//   * Core\AppInfo.cs 的 AppInfo.Version —— 程序自己（关于面板、注册表、
//     升级包文件名）用的是它。
// 两处都必须是 "数字.数字.数字.数字" 的形式，更新比较按段比数字。
[assembly: AssemblyTitle("TrpgMaps")]
[assembly: AssemblyDescription("TrpgMaps —— 跑团地图投屏工具（Win7 / 32 位兼容版）")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("starnight-dirt")]
[assembly: AssemblyProduct("TrpgMaps")]
[assembly: AssemblyCopyright("Copyright (c) starnight-dirt")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]
[assembly: Guid("8e2b7c41-5a3d-4e6f-9b12-7c4d0a5e1f88")]

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
