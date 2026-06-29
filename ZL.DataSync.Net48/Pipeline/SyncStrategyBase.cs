using System.Data;
using SqlSugar;
using ZL.DataSync.Config;
using ZL.DataSync.Infrastructure;

namespace ZL.DataSync.Pipeline;

/// <summary>
/// 同步策略抽象基类：提取具体策略（Database / ProcessTime / HTTP）的公共逻辑，
/// 包括：远程建表（DCL 双重检查锁）、远程批量写入、本地标记同步。
/// 所有数据库类具体策略应继承此类，仅实现差异化部分（读数据 + 水位线管理）。
/// </summary>
public abstract class SyncStrategyBase : ISyncStrategy
{
    /// <summary>目标名称（由子类在构造函数中初始化，接口实现需要公开）</summary>
    public string TargetName { get; }

    /// <summary>远程数据库类型（由子类在构造函数中映射）</summary>
    protected SqlSugar.DbType RemoteDbType { get; }

    /// <summary>远程连接池（由子类在构造函数中创建，Dispose 时释放）</summary>
    protected SqlSugarScope RemoteScope { get; }

    /// <summary>日志（由子类在构造函数中注入）</summary>
    protected readonly IStructuredLogger Logger;

    /// <summary>建表锁（DCL 双重检查锁，避免并发重复建表）</summary>
    private readonly object _createTableLock = new();

    /// <summary>Dispose 锁（保证 SqlSugarScope 只被释放一次，防止双重释放）</summary>
    private readonly object _disposeLock = new();
    private bool _disposed;

    /// <summary>
    /// 创建同步策略基类实例。
    /// </summary>
    /// <param name="targetName">目标名称（用于日志标识）</param>
    /// <param name="remoteConnectionString">远程数据库连接字符串</param>
    /// <param name="dbType">远程数据库类型（避免在构造中调用抽象方法）</param>
    /// <param name="logger">结构化日志</param>
    protected SyncStrategyBase(string targetName, string remoteConnectionString, SqlSugar.DbType dbType, IStructuredLogger logger)
    {
        TargetName = targetName ?? throw new ArgumentNullException(nameof(targetName));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        RemoteDbType = dbType;
        RemoteScope = new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = remoteConnectionString ?? throw new ArgumentNullException(nameof(remoteConnectionString)),
            DbType = RemoteDbType,
            IsAutoCloseConnection = false  // 启用连接池，复用连接
        });
    }

    // ═══════════════════════════════════════════════════════════════
    //  ISyncStrategy 实现
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 同步单表数据到目标。由子类实现具体读取和标记逻辑。
    /// 公共逻辑（建表、批量写入、本地标记）由基类提供。
    /// </summary>
    public abstract Task<SyncReport> SyncTableAsync(
        string tableName,
        string? remoteTable,
        int batchSize,
        SqlSugarClient localDb,
        CancellationToken ct);

    // ═══════════════════════════════════════════════════════════════
    //  公共工具方法（子类复用）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 确保远程表存在。基于样本数据自动建表，使用 DCL 双重检查锁避免并发冲突。
    /// 在锁外执行 CREATE TABLE SQL（避免 lock 中 await 导致死锁）。
    /// </summary>
    protected async Task EnsureTableAsync(string targetTable, Dictionary<string, object?> sampleRow, CancellationToken ct)
    {
        // 快速路径：已存在则跳过（无锁）
        if (RemoteScope.DbMaintenance.IsAnyTable(targetTable, false))
            return;

        // 委托 SqlSugarHelpers 构建 SQL（在锁外执行，避免 lock 中 await）
        string createSql = SqlSugarHelpers.BuildCreateTableSql(targetTable, RemoteDbType, sampleRow, TargetName, Logger);
        if (string.IsNullOrEmpty(createSql)) return;

        lock (_createTableLock)
        {
            // 双重锁：另一个线程可能在等待锁期间完成了建表
            if (RemoteScope.DbMaintenance.IsAnyTable(targetTable, false))
                return;

            RemoteScope.Ado.ExecuteCommand(createSql);
        }
    }

    /// <summary>
    /// 通过 Ado.ExecuteCommandAsync 批量插入行。
    /// </summary>
    /// <param name="targetTable">远程表名</param>
    /// <param name="rows">待插入行</param>
    /// <param name="ct">取消令牌</param>
    protected async Task InsertRowsViaAdoAsync(string targetTable, List<Dictionary<string, object?>> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;

        var (sql, parameters) = SqlSugarHelpers.BuildBatchInsertSql(targetTable, RemoteDbType, rows);
        if (parameters.Count == 0) return;

        await RemoteScope.Ado.ExecuteCommandAsync(sql, parameters.ToArray()).ConfigureAwait(false);
    }

    /// <summary>
    /// 通过 Ado.ExecuteCommandAsync 插入单行。
    /// </summary>
    /// <param name="targetTable">远程表名</param>
    /// <param name="row">待插入行</param>
    /// <param name="ct">取消令牌</param>
    protected async Task InsertRowViaAdoAsync(string targetTable, Dictionary<string, object?> row, CancellationToken ct)
    {
        var (sql, parameters) = SqlSugarHelpers.BuildSingleInsertSql(targetTable, RemoteDbType, row);
        if (string.IsNullOrEmpty(sql) || parameters.Count == 0) return;

        await RemoteScope.Ado.ExecuteCommandAsync(sql, parameters.ToArray()).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量标记本地记录为已同步（仅当子类需要标记本地状态时调用）。
    /// 委托 SqlSugarHelpers.BatchMarkSyncedAsync。
    /// </summary>
    /// <param name="localDb">本地 SQLite 连接</param>
    /// <param name="tableName">本地表名</param>
    /// <param name="rows">待标记行</param>
    /// <param name="ct">取消令牌</param>
    protected async Task BatchMarkSyncedAsync(SqlSugarClient localDb, string tableName, List<Dictionary<string, object?>> rows, CancellationToken ct)
    {
        await SqlSugarHelpers.BatchMarkSyncedAsync(localDb, tableName, SqlSugar.DbType.Sqlite, rows, ct).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════════
    //  数据过滤/转换（扩展点）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 应用数据过滤和转换。子类在批量写入前调用此方法。
    /// </summary>
    /// <param name="rows">原始行列表</param>
    /// <param name="config">目标配置（包含 DataFilter/DataTransform 回调）</param>
    /// <returns>过滤/转换后的行列表</returns>
    protected List<Dictionary<string, object?>> ApplyFiltersAndTransforms(List<Dictionary<string, object?>> rows, RemoteTargetConfig? config)
    {
        if (config == null || (config.DataFilter == null && config.DataTransform == null))
            return rows;

        var result = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            // 1. 过滤
            if (config.DataFilter != null && !config.DataFilter(row))
                continue;

            // 2. 转换
            if (config.DataTransform != null)
            {
                var transformed = new Dictionary<string, object?>(row);
                config.DataTransform(transformed);
                result.Add(transformed);
            }
            else
            {
                result.Add(row);
            }
        }
        return result;
    }

    public void Dispose()
    {
        // 使用 lock 保证 SqlSugarScope 只被 Dispose 一次
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                RemoteScope?.Dispose();
            }
            catch
            {
                // SqlSugarScope.Dispose 可能在连接字符串无效时抛出，忽略
            }
        }
    }
}
