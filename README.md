# ClipMate

**一个轻量的 Windows 剪贴板历史管理器** —— 给你的复制粘贴加一个「历史记录 + 收藏夹」。

![platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![license](https://img.shields.io/badge/license-MIT-green)
![size](https://img.shields.io/badge/installer-44MB-lightgrey)

<p align="center">
  <img src="assets/ClipMate.png" width="128" alt="ClipMate icon">
</p>

---

## 这是什么

ClipMate 常驻系统托盘，自动记录你复制过的文本内容。按 `Ctrl+Shift+V` 随时唤出历史列表，双击任意一条即可重新粘贴回剪贴板。还可以把密码、命令、常用模板放进**分组**永久保留，单击分组即取。

## 为什么做这个

原本用的是 [Ditto](https://ditto-cp.sourceforge.net/)，但它的几个问题让人难受。于是用 [REA](https://rea.tools/) 对 Microsoft Store 版 Ditto 3.25.113.0 做了数据层 + 行为层逆向分析，把根因挖清楚后重写了一个：

| Ditto 的问题（逆向结论） | ClipMate 的做法 |
|---|---|
| 设置了条数上限仍然占用 104MB 磁盘 —— 条数限制**其实生效了**（Main 表仅 502 行），但 `Data` 表里 **65 条未压缩 `CF_DIB` 位图占了 107.5MB / 98.8%**，而 Ditto 完全没有「总字节上限」这个维度 | **条数 + 总字节 + 保留天数三重淘汰**，任一维度超限即清理 |
| 删了条目磁盘一点不降 —— Ditto 用「`MainDeletes` 记账表 + 两阶段触发器」软删除：删主表只写记账，必须再删记账表才真删数据。实测删 64 条图片后文件 104MB → 1.8MB，第二步一慢就永远不降 | **单事务真删**（无间接层）+ `wal_checkpoint(TRUNCATE)` + `VACUUM` + 再 checkpoint，删完立即回收物理空间 |
| 分组要双击两次才进入 | **单击分组即进入** |
| 「朋友」局域网共享用不上，却仍监听 TCP 23443 并占用防火墙例外 | **彻底不实现**，零网络监听、零端口占用 |
| 图片剪贴板默默吃掉 98.8% 空间 | **默认不保存图片**，可手动开启并单独设图片总量上限；开启后统一转 PNG 存储而非原始 DIB |

> 完整的逆向分析（数据库 schema、两阶段删除链实证、PE 资源与字符串证据）见 [`docs/ditto-reverse-engineering.md`](docs/ditto-reverse-engineering.md)。

## 功能

- 📋 **自动记录** 复制的文本，重开程序也不丢
- 🔍 **即时搜索** 历史内容，支持模糊匹配
- 📁 **分组收藏** 单击进入分组，组内条目永久保留、不参与自动清理
- ⌨️ **全局热键** 默认 `Ctrl+Shift+V`，可在设置里按组合键直接录制（支持 `` Ctrl+` `` 等符号键）
- 🧹 **三重自动清理** 条数 / 总字节 / 保留天数，超限自动淘汰并回收磁盘
- 🖼️ **图片可选** 默认关闭（避免磁盘暴涨），开启后可设图片总量上限
- 🔒 **纯本地** 无网络请求、无遥测、无账号，数据只存在你自己的电脑上
- 🪶 **轻量** 单文件 49MB（自包含 .NET 运行时，免装依赖），安装包 44MB

## 下载安装

从 [Releases](../../releases) 下载最新版本：

| 方式 | 文件 | 说明 |
|---|---|---|
| 安装包（推荐） | `ClipMate-Setup.exe` | 安装向导 + 桌面快捷方式 + 可选开机自启 + 卸载入口 |
| 便携版 | `ClipMate.exe` | 单文件，双击即用，不写注册表（除开机自启外） |

**系统要求**：Windows 10 / 11 x64（已在 Win10 22H2、Win11 实测）。程序自带 .NET 8 运行时，无需额外安装。

> ⚠️ **杀软误报提示**：自包含单文件 exe 且未做代码签名，可能被 Windows Defender / 第三方杀软误报。
> - 个人使用：把 `ClipMate.exe` 加入杀软排除项（Defender：设置 → 病毒和威胁防护 → 排除项）
> - 首次运行若被 SmartScreen 拦截，点「更多信息」→「仍要运行」
> - 正式分发建议使用代码签名证书（`signtool sign`）或向微软提交误报申诉

## 使用

1. **唤出历史** — 按 `Ctrl+Shift+V`（或单击托盘图标）
2. **粘贴** — 双击条目，默认粘贴后自动关闭窗口（可在设置里关掉）
3. **搜索** — 唤出后直接在顶部搜索框输入
4. **分组** — 底部「新建分组」→ 右键条目「移动到分组」→ 单击左侧分组切换
5. **置顶** — 右键条目「置顶/取消置顶」，置顶项不参与自动清理
6. **立即清理** — 托盘右键 →「立即清理」

### 设置项

| 设置 | 默认 | 说明 |
|---|---|---|
| 历史条数上限 | 500 | 0 = 不限 |
| 历史总大小上限 | 64 MB | **Ditto 没有的维度**，0 = 不限 |
| 保留天数 | 30 天 | 0 = 不限 |
| 单条最大 | 512 KB | 超限不保存 |
| 保存图片 | 关闭 | 开启后统一转 PNG |
| 图片总大小上限 | 16 MB | 仅开启图片时生效 |
| 显示热键 | `Ctrl+Shift+V` | 点击输入框直接按组合键录制，`Esc` 清空=禁用 |
| 双击粘贴后自动关闭窗口 | 开启 | — |
| 粘贴后移除该条 | 关闭 | 一次性剪贴板场景 |
| 去重 | 开启 | 与上一条相同则不新增 |
| 开机自启动 | 开启 | — |

## 从源码构建

### 环境要求

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- （仅制作安装包时需要）[Inno Setup 6](https://jrsoftware.org/isinfo.php)，可用 `winget install JRSoftware.InnoSetup` 安装
- （仅编译需中文安装向导时）把 [`ChineseSimplified.isl`](https://raw.githubusercontent.com/jrsoftware/issrc/main/Files/Languages/ChineseSimplified.isl) 放到 Inno Setup 的 `Languages` 目录

### 编译运行

```bat
:: 直接运行
dotnet run --project src\ClipMate\ClipMate.csproj

:: 或编译 Release
dotnet build src\ClipMate\ClipMate.csproj -c Release
```

### 打包便携版（单文件 exe）

```bat
dotnet publish src\ClipMate\ClipMate.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true -o publish\portable
```

产物 `publish\portable\ClipMate.exe`，自包含 .NET 运行时，双击即用。

> **体积优化说明**：self-contained 发布默认会把整个 `Microsoft.WindowsDesktop.App` runtime pack（WinForms + WPF 合并包）全量复制，但纯 WinForms 应用用不到 WPF。项目在 `ClipMate.csproj` 里通过 MSBuild target 在 `_ComputeFilesToBundle` 之前从发布清单排除 WPF 程序集（约 40MB），并保留 `WindowsBase.dll` / `UIAutomation*`（WinForms 间接依赖）。
>
> 注意：`PublishTrimmed`（IL 裁剪）**不支持 WinForms**，会报 `NETSDK1175`，因此本方案只用「排除整程序集」而非裁剪代码。

### 制作安装包

```bat
iscc.exe packaging\ClipMate.iss
```

产物 `publish\installer\ClipMate-Setup.exe`。

### 一键完成两者

```bat
build.cmd
```

### 运行自测

```bat
dotnet run --project src\ClipMate.Tests\ClipMate.Tests.csproj -c Release
```

覆盖：三重淘汰逻辑、物理空间回收、置顶/分组保护、图片默认策略、热键解析与 `FormatKey`↔`Parse` 往返。

## 项目结构

```
ClipMate/
├── src/
│   ├── ClipMate/                  # 主程序
│   │   ├── Program.cs             # 入口：STA 线程、单实例、托盘
│   │   ├── ClipMate.csproj        # 项目文件（含体积优化 target）
│   │   ├── Core/                  # 核心逻辑（与 UI 解耦）
│   │   │   ├── AppConfig.cs       # 配置读写（JSON）
│   │   │   ├── AppLifecycle.cs    # 单实例 Mutex + 开机自启
│   │   │   ├── ClipEntry.cs       # 数据模型
│   │   │   ├── ClipboardReader.cs # 剪贴板读/写
│   │   │   ├── ClipboardService.cs# 协调器：粘贴/捕获/淘汰
│   │   │   ├── ClipboardWatcher.cs# AddClipboardFormatListener
│   │   │   ├── HotkeyManager.cs   # 全局热键 + 键码互转
│   │   │   └── Storage.cs         # SQLite 存储 + 三重淘汰 + 空间回收
│   │   └── UI/
│   │       ├── MainForm.cs        # 主窗口（分组树 + 列表）
│   │       └── SettingsForm.cs    # 设置窗口
│   └── ClipMate.Tests/            # 自测 harness
├── assets/                        # 图标资源（ClipMate.ico / ClipMate.png）
├── packaging/ClipMate.iss         # Inno Setup 安装脚本
├── docs/
│   ├── LICENSE.txt                # 安装向导展示的许可证文本
│   └── ditto-reverse-engineering.md  # Ditto 逆向分析报告
├── make_icon.py                   # 纯标准库图标生成器（SDF 抗锯齿）
├── build.cmd                      # 一键发布
├── LICENSE                        # MIT
└── README.md
```

## 技术栈

| 层 | 选型 | 理由 |
|---|---|---|
| 语言/框架 | C# / .NET 8 / WinForms | 原生 Win32 控件、启动快、体积可控，无需 Web 运行时 |
| 存储 | Microsoft.Data.Sqlite | 单文件库、事务可靠、支持窗口函数做字节淘汰 |
| 剪贴板监听 | `AddClipboardFormatListener` | 系统级消息通知，无轮询、零 CPU 占用 |
| 热键 | `RegisterHotKey` | 系统级注册，无需焦点 |
| 打包 | `dotnet publish` + Inno Setup | 单文件自包含 + 标准 Windows 安装体验 |

## 已知限制

- 仅提供 **x64** 构建；x86 / ARM64 需改 `-r win-x86` / `-r win-arm64` 自行发布
- 目前只记录**文本**（和可选的图片），不记录文件列表（`CF_HDROP` 会转成路径文本保存）
- 富文本粘贴时只保留纯文本，不保留格式
- 未做代码签名，企业环境可能被组策略拦截

## 贡献

欢迎提 Issue 和 PR。提交前请：

1. 确认 `dotnet build` 零警告零错误
2. 跑一遍 `src/ClipMate.Tests` 自测
3. 新增逻辑请附带对应测试用例

## 许可

[MIT](LICENSE) © 2026 ClipMate contributors

## 致谢

- [Ditto](https://ditto-cp.sourceforge.net/) —— 逆向分析的对象，也是这个项目要解决的痛点来源
- [REA](https://rea.tools/) —— 用于 Ditto 数据层与行为层逆向分析的工具

> 本项目与 Ditto、REA 均无隶属关系。逆向分析仅用于理解行为、定位问题根因，所有代码为独立实现。
