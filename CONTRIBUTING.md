# 贡献指南

感谢你愿意为 ClipMate 出力。以下是参与开发的约定，尽量简短。

## 环境准备

1. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
2. 克隆仓库

   ```bat
   git clone https://github.com/<your-account>/ClipMate.git
   cd ClipMate
   ```

3. 编译并运行

   ```bat
   dotnet build src\ClipMate\ClipMate.csproj -c Release
   dotnet run --project src\ClipMate\ClipMate.csproj
   ```

## 提交前自检

请确保以下两项都通过，再提交 PR：

```bat
:: 1) 编译必须零警告零错误
dotnet build src\ClipMate\ClipMate.csproj -c Release

:: 2) 自测全部通过
dotnet run --project src\ClipMate.Tests\ClipMate.Tests.csproj -c Release
```

自测覆盖：三重淘汰逻辑、物理空间回收、置顶/分组保护、图片默认策略、热键 `FormatKey`↔`Parse` 往返。

## 代码约定

- 目标框架固定 `net8.0-windows`，UI 用 WinForms（不引入 WPF / Electron 等重依赖）
- **禁止新增网络功能**（不做剪贴板云同步/局域网共享），这是本项目的设计边界
- 核心逻辑放 `src/ClipMate/Core/`，保持与 UI 解耦，便于单测
- 涉及存储/淘汰/热键的改动，必须补充 `src/ClipMate.Tests` 用例
- 注释用中文，代码标识符用英文
- 提交信息用中文或英文均可，但需说明「改了什么 + 为什么」

## 分支与 PR

1. 从 `main` 拉新分支：`git checkout -b fix/short-description`
2. 提交改动（一个 PR 只做一件事）
3. 推送并开 PR，描述里写清：
   - 问题现象（或需求）
   - 改动点
   - 验证方式（跑了哪些测试、手动验证步骤）

## 报告问题

开 Issue 时请附上：

- Windows 版本（`winver`）
- ClipMate 版本
- 复现步骤与预期/实际行为
- 相关日志或截图（日志位置：`%LOCALAPPDATA%\ClipMate\`）

> 提交日志前请先自行检查，避免泄露剪贴板里的密码、密钥等敏感内容。
