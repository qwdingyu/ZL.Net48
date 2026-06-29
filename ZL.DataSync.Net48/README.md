# ZL.DataSync

轻量级数据同步管道：从 SQLite 缓冲库可靠地分发数据到远程目标（MySQL / SQL Server / HTTP API）。

## 设计原则

1. **轻量**：无外部依赖（无 Kafka、无 Redis、无 ZooKeeper），一个 DLL 搞定
2. **可靠**：SQLite 本地缓冲 → 增量同步 → 标记已同步 → 水位线追踪，断网自动续传
3. **扩展**：多目标（同时推 MySQL + SQL Server）、多策略（数据库 / HTTP API）、表映射
4. **简单**：3-5 年业务场景够用，不需要复杂的 CDC 或事务日志解析

## 架构

```
┌──────────────────────────────────────────────────────┐
│                   应用层（PcStationIot）               │
│  PLC采集 → SQLite 写入(_Synced=0)                     │
└────────────────┬─────────────────────────────────────┘
                 │ _Synced=0 未同步记录
                 ▼
┌──────────────────────────────────────────────────────┐
│              ZL.DataSync.SyncEngine                   │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐  │
│  │ Target: MES  │  │ Target: ERP  │  │ Target: API  │  │
│  │ MySQL 策略   │  │ SQLServer   │  │ HTTP 策略    │  │
│  └──────┬──────┘  └──────┬──────┘  └──────┬──────┘  │
│         │                │                 │         │
│  ┌──────▼────────────────▼─────────────────▼──────┐  │
│  │           SQLite 本地缓冲                       │  │
│  │  t_ad_boltsdata (_Synced=0/1)                  │  │
│  │  _SyncWatermark (水位线追踪)                    │  │
│  └───────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────┘
```

## 快速使用

### 1. 基本配置

```csharp
var config = new DataSyncConfig
{
    LocalDbPath = @"Data\station_data.db",
    BatchSize = 100,
    SyncIntervalSeconds = 5,
    MaxRetryCount = 3,
    RetryBackoffSeconds = 2,
    EnableUpsert = true,
    EnableCleanup = true,
    DataRetentionDays = 730,
    CleanupIntervalSeconds = 3600,
    RemoteTargets = new List<RemoteTargetConfig>
    {
        new()
        {
            Name = "MES-MySQL",
            Type = TargetType.MySql,
            ConnectionString = "server=192.168.1.100;database=MES;user=root;password=xxx;",
        },
        new()
        {
            Name = "ERP-SQLServer",
            Type = TargetType.SqlServer,
            ConnectionString = "Data Source=192.168.1.101;Initial Catalog=ERP;User ID=sa;Password=xxx;",
            TableMappings = new Dictionary<string, string>
            {
                { "t_ad_boltsdata", "T_ProductionRecords" }  // 本地表 → 远程表映射
            }
        }
    }
};

var engine = new SyncEngine(config, new PfliteLoggerAdapter("DataSync"));
engine.Start();
```

### 2. 使用 DI 注册

```csharp
services.AddDataSync(cfg =>
{
    cfg.LocalDbPath = @"Data\station_data.db";
    cfg.SyncIntervalSeconds = 5;
    cfg.RemoteTargets = new()
    {
        new() { Name = "MES", Type = TargetType.MySql, ConnectionString = "..." }
    };
});
```

### 3. 手动触发一次同步

```csharp
var reports = await engine.ForceSyncAsync();
foreach (var kv in reports)
{
    Console.WriteLine($"{kv.Key}: 成功 {kv.Value.SyncedCount} 条");
}
```

### 4. 查询同步状态

```csharp
var status = engine.Status;
Console.WriteLine($"运行中: {status.IsRunning}");
Console.WriteLine($"总同步: {status.TotalSynced}");
Console.WriteLine($"最后同步: {status.LastSyncTime}");
Console.WriteLine($"健康: {status.IsHealthy}");
```

## 数据流转

### 写入 SQLite（采集侧）

```csharp
// 在 PcStationIot 的 StationSqlStore 中：
var dict = new Dictionary<string, object?>
{
    ["Id"] = nextId,
    ["StationCode"] = stationCode,
    ["BarCode"] = barCode,
    ["ProcessTime"] = DateTime.UtcNow,
    ["_Synced"] = false,    // ← 关键：标记未同步
    ["_SyncTime"] = null,   // ← 同步成功后更新
    // ... 其他业务字段
};

await localDb.Insertable(dict).AS(tableName).ExecuteCommandAsync();
```

### 同步引擎自动处理

1. 每 5 秒扫描一次所有目标
2. 每个目标读 `WHERE _Synced = 0 ORDER BY ProcessTime LIMIT 100`
3. 远程库不存在则自动建表
4. 逐条写入远程库（支持 Upsert）
5. 写入成功 → 设置 `_Synced = 1`
6. 失败 → 指数退避重试，下次循环继续

### 数据清理

每 1 小时检查一次，删除 `ProcessTime < 2年前 AND _Synced = 1` 的记录。

## 多目标分发

同一 SQLite 可以推送到多个目标，每个目标独立循环、独立水位线、独立失败重试：

```json
{
  "LocalDbPath": "Data/station_data.db",
  "RemoteTargets": [
    {
      "Name": "MES",
      "Type": "MySql",
      "ConnectionString": "..."
    },
    {
      "Name": "Backup",
      "Type": "SqlServer",
      "ConnectionString": "..."
    },
    {
      "Name": "Webhook",
      "Type": "Http",
      "HttpConfig": {
        "Endpoint": "http://192.168.1.100:94/api/mesdetect/DataUpLoad",
        "DeviceName": "装配F线静音房检测1",
        "Type": "5",
        "TimeoutSeconds": 30
      }
    }
  ]
}
```

## 与 DataUploadServer 的集成

`DataUploadServer`（旧版 Windows 服务）的逻辑可以迁移为 `ZL.DataSync` 的 HTTP 目标：

| DataUploadServer | ZL.DataSync 对应 |
|---|---|
| `DataUploadScheduler` | 内嵌在 SyncEngine 的循环中 |
| `DataUploadService` | `HttpSyncStrategy` |
| `TestResultsDao.GetTestResultsForUpload` | 自动读 `_Synced=0` |
| `TestResultsDao.UpdateTransmitStatusAsync` | 自动写 `_Synced=1` |
| `DataCleanupScheduler` | 内嵌在 SyncEngine 的清理循环中 |

迁移只需要在配置中声明一个 HTTP 目标，不再需要单独的 Windows 服务。

## 文件结构

```
ZL.DataSync/
├── Config/
│   ├── DataSyncConfig.cs          # 同步引擎配置（不可变）
│   └── RemoteTargetConfig.cs      # 远程目标配置 + HTTP 配置
├── Pipeline/
│   ├── ISyncStrategy.cs           # 同步策略接口
│   ├── DatabaseSyncStrategy.cs    # 数据库同步策略（MySQL/SQL Server/PG/Oracle）
│   └── HttpSyncStrategy.cs        # HTTP API 同步策略
├── Sync/
│   ├── SyncReport.cs              # 同步报告 + 同步状态
│   └── SyncEngine.cs              # 同步引擎（核心：循环、重试、清理、表发现）
└── Infrastructure/
    ├── WatermarkStore.cs          # 水位线存储（SQLite 内的 _SyncWatermark 表）
    ├── ILogger.cs                 # 日志接口 + DebugLogger
    ├── PfliteLoggerAdapter.cs     # 桥接 ZL.PFLite 的 LogKit
    └── ServiceCollectionExtensions.cs  # DI 扩展
```

## 扩展点

### 新增同步策略

实现 `ISyncStrategy` 接口即可：

```csharp
public class RabbitMqSyncStrategy : ISyncStrategy
{
    public async Task<SyncReport> SyncTableAsync(...)
    {
        // 读 SQLite → 序列化 → 发布到 RabbitMQ
    }
}
```

然后在 `SyncEngine.CreateStrategy()` 中加一个 `switch` 分支。

### 自定义清理逻辑

继承 `SyncEngine` 或重写清理方法。

## 版本历史

- v1.0.0 (2025-06-XX) — 初始版本
