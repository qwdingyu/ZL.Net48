namespace ZL.DataSync.Config;

/// <summary>
/// 数据同步配置（不可变，线程安全）。
/// </summary>
public sealed class DataSyncConfig
{
    // ─── 本地 SQLite ────────────────────────────────────────────────

    /// <summary>本地 SQLite 文件路径（绝对路径，如 /data/sync.db）</summary>
    public string LocalDbPath { get; set; } = string.Empty;

    // ─── 远程目标（多目标支持）──────────────────────────────────────

    /// <summary>
    /// 远程目标列表（支持同时同步到 MySQL、SQL Server 等多个目标）。
    /// 为空时仅写入本地 SQLite，不主动分发。
    /// </summary>
    public List<RemoteTargetConfig> RemoteTargets { get; set; } = new();

    // ─── 管道行为 ───────────────────────────────────────────────────

    /// <summary>
    /// 每次同步批次大小。默认 100。
    /// 每次循环最多读取 batchSize 条未同步记录。
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// 同步循环间隔（秒）。默认 5 秒。
    /// 每次同步完成后等待此时间再进入下一轮。
    /// </summary>
    public int SyncIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// 失败后最大重试次数。默认 3。
    /// 注意：当前实现使用无限指数退避（failStreak 在连续成功后清零），
    /// 此字段保留以备未来扩展。
    /// </summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>
    /// 失败后初始退避时间（秒）。默认 2 秒，按指数退避：min(2^failStreak * RetryBackoffSeconds, 300)。
    /// </summary>
    public int RetryBackoffSeconds { get; set; } = 2;

    /// <summary>
    /// 是否在每表内保持精确的去重保证（基于业务主键的 UPSERT 语义）。
    /// 默认 true。设为 false 可提升性能（仅 INSERT，依赖远程库的幂等性）。
    /// </summary>
    public bool EnableUpsert { get; set; } = true;

    /// <summary>
    /// 是否启用数据清理。默认 true。
    /// 清理已同步超过 DataRetentionDays 天的记录。
    /// </summary>
    public bool EnableCleanup { get; set; } = true;

    /// <summary>
    /// 已同步数据的保留天数。默认 730 天（2年）。
    /// </summary>
    public int DataRetentionDays { get; set; } = 730;

    /// <summary>
    /// 数据清理检查间隔（秒）。默认 3600（1小时）。
    /// </summary>
    public int CleanupIntervalSeconds { get; set; } = 3600;
}
