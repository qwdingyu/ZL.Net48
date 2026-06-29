# ZL.Net48

`.NET Framework 4.8` 兼容组件集合仓库。当前包含：

- `src/ZL.DataSync.Net48`：`ZL.DataSync` 的 .NET Framework 4.8 独立兼容版本，用于桌面 Runner 免 .NET Core 运行时场景。

## 仓库定位

- 统一托管 Net48 相关项目，避免每个 DLL 单独建仓库。
- 与 `iot-sdk` 的现代 .NET 版本形成互补，按运行时边界独立维护。

## 构建

本仓库包含 .NET Framework 4.8 项目，请在 Windows 环境构建：

```bash
cd ZL.DataSync.Net48
dotnet pack ZL.DataSync.Net48.csproj -c Release
```

产物位于：

```text
ZL.DataSync.Net48/bin/Release/net48/ZL.DataSync.Net48.{version}.nupkg
```

## 发布

推送 `v*` 标签或使用 GitHub Actions 手动触发即可自动构建并推送到 NuGet。

## 消费者

- `PcStationIot` 等桌面 .NET Framework 4.8 项目可通过 NuGet 引用 `ZL.DataSync.Net48`。

## 同步说明

`ZL.DataSync.Net48` 与上游 `ZL.DataSync` 保持同步：
- 功能逻辑尽量保持一致
- 仅对 .NET Framework 不兼容 API 做适配（如 `HttpClientHandler` 替代 `SocketsHttpHandler`）
