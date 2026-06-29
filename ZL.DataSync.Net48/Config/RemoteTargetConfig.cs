using System;
using System.Collections.Generic;

namespace ZL.DataSync.Config;

/// <summary>
/// 数据过滤/转换回调结果。
/// </summary>
public sealed class DataFilterResult
{
    public DataFilterResult(bool shouldSync, Dictionary<string, object?>? transformedRow = null)
    {
        ShouldSync = shouldSync;
        TransformedRow = transformedRow;
    }

    /// <summary>是否应同步该行（true=保留，false=过滤掉）。</summary>
    public bool ShouldSync { get; }

    /// <summary>转换后的行（可为 null，表示保持原行）。</summary>
    public Dictionary<string, object?>? TransformedRow { get; }
}

/// <summary>
/// 远程目标配置（一个目标 = 一个数据库/HTTP API 端点）。
///
/// 扩展点：
/// - DataFilter：在同步前过滤数据（如只同步特定 StationCode 的记录）
/// - DataTransform：在同步前转换数据（如修改列值、添加额外字段）
/// </summary>
public sealed class RemoteTargetConfig
{
    /// <summary>目标名称（用于日志标识，如 "MES-MySQL"）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>目标类型</summary>
    public TargetType Type { get; set; } = TargetType.MySql;

    /// <summary>连接字符串</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// 表映射配置。Key=本地表名，Value=远程表名。
    /// 为空时默认表名相同。
    /// </summary>
    public Dictionary<string, string> TableMappings { get; set; } = new();

    /// <summary>
    /// 同步策略类型。默认 DatabaseSyncStrategy（基于 _Synced 标记）。
    /// 设置为 ProcessTime 时使用 ProcessTimeSyncStrategy（基于 ProcessTime + _SyncLog 水位线）。
    /// </summary>
    public SyncStrategyType StrategyType { get; set; } = SyncStrategyType.Database;

    /// <summary>
    /// 数据上传模式（当 Type=Http 时使用）。
    /// </summary>
    public HttpUploadConfig? HttpConfig { get; set; }

    // ─── 扩展点：数据过滤/转换 ─────────────────────────────────────

    /// <summary>
    /// 数据过滤回调。在同步前对每行数据进行过滤。
    /// 返回 true 保留该行，false 过滤掉。
    /// 例如：只同步特定 StationCode 的记录、过滤掉 FinalResult="FAIL" 的记录。
    /// 为 null 时不过滤任何行。
    /// </summary>
    public Func<Dictionary<string, object?>, bool>? DataFilter { get; set; }

    /// <summary>
    /// 数据转换回调。在同步前对每行数据进行转换/增强。
    /// 可以修改行中的值，或添加额外字段。
    /// 为 null 时保持原行不变。
    /// </summary>
    public Action<Dictionary<string, object?>>? DataTransform { get; set; }
}

/// <summary>同步策略类型</summary>
public enum SyncStrategyType
{
    /// <summary>基于 _Synced 标记（ZL.DataSync 独立使用）</summary>
    Database,
    /// <summary>基于 ProcessTime 增量 + _SyncLog 水位线（PcStationIot 集成使用）</summary>
    ProcessTime
}

/// <summary>远程目标类型</summary>
public enum TargetType
{
    /// <summary>MySQL 数据库</summary>
    MySql,
    /// <summary>SQL Server 数据库</summary>
    SqlServer,
    /// <summary>PostgreSQL 数据库</summary>
    PostgreSql,
    /// <summary>Oracle 数据库</summary>
    Oracle,
    /// <summary>HTTP API（JSON 格式推送）</summary>
    Http
}

/// <summary>
/// HTTP 上传配置（仅 Type=Http 时使用）。
/// </summary>
public sealed class HttpUploadConfig
{
    /// <summary>API 端点 URL</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// 每个表对应的 API 端点。
    /// Key=本地表名，Value=API URL。
    /// 为空时使用上面的 Endpoint 作为默认值。
    /// </summary>
    public Dictionary<string, string> TableEndpoints { get; set; } = new();

    /// <summary>请求超时（秒）。默认 30</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// 自定义请求头（如认证 Token）。
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>
    /// 自定义请求体模板。
    /// 支持变量：{deviceName}, {barCode}, {timestamp}, {table}, {data}
    /// 为空时自动生成标准 JSON 结构。
    /// </summary>
    public string? BodyTemplate { get; set; }

    /// <summary>设备标识（用于请求体中的 deviceName 字段）</summary>
    public string? DeviceName { get; set; }

    /// <summary>数据分类标识（用于请求体中的 type 字段）</summary>
    public string? Type { get; set; }
}
