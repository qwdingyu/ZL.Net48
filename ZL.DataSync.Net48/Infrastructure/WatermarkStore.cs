using SqlSugar;

namespace ZL.DataSync.Infrastructure;

/// <summary>
/// 水位线存储：按表名 + 目标名追踪每个表的最后同步水位。
/// 水位线可以是时间戳（ProcessTime）或自增 ID（Id）。
/// 存储在本地 SQLite 的 _SyncWatermark 表内。
/// </summary>
internal sealed class WatermarkStore : IDisposable
{
    private readonly SqlSugarClient _localDb;
    private readonly IStructuredLogger _logger;
    private bool _disposed;

    /// <summary>
    /// 使用外部共享的 ISqlSugarClient（自动适配 SqlSugarClient）。
    /// </summary>
    public WatermarkStore(ISqlSugarClient client, IStructuredLogger? logger = null)
    {
        _localDb = client as SqlSugarClient ?? throw new ArgumentNullException("client must be SqlSugarClient");
        _logger = logger ?? new DebugLogger();
    }

    /// <summary>
    /// 确保水位线表存在。
    /// </summary>
    public void EnsureTable()
    {
        if (!_localDb.DbMaintenance.IsAnyTable("_SyncWatermark", false))
        {
            _localDb.Ado.ExecuteCommand(
                "CREATE TABLE IF NOT EXISTS \"_SyncWatermark\" (" +
                "\"TableName\" TEXT NOT NULL, " +
                "\"TargetName\" TEXT NOT NULL, " +
                "\"WatermarkType\" TEXT NOT NULL DEFAULT 'DateTime', " +
                "\"WatermarkValue\" TEXT NOT NULL, " +
                "\"LastSyncTime\" TEXT, " +
                "PRIMARY KEY (TableName, TargetName)" +
                ")");
        }
    }

    /// <summary>
    /// 读取指定表+目标的水位线。
    /// </summary>
    public string? ReadWatermark(string tableName, string targetName)
    {
        try
        {
            var sql = "SELECT WatermarkValue FROM _SyncWatermark WHERE TableName = @tableName AND TargetName = @targetName";
            var result = _localDb.Ado.SqlQuery<string>(sql,
                new SugarParameter("@tableName", tableName),
                new SugarParameter("@targetName", targetName)
            );

            if (result != null && result.Count > 0 && result[0] != null)
                return result[0];

            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning($"读取水位线失败 [{tableName}/{targetName}]: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 写入水位线。
    /// </summary>
    public void WriteWatermark(string tableName, string targetName, string watermarkValue)
    {
        try
        {
            // 检查是否已存在
            var count = _localDb.Ado.SqlQuery<int>(
                "SELECT COUNT(*) FROM _SyncWatermark WHERE TableName = @tableName AND TargetName = @targetName",
                new SugarParameter("@tableName", tableName),
                new SugarParameter("@targetName", targetName)
            );

            var hasRow = count != null && count.Count > 0 && count[0] > 0;

            if (hasRow)
            {
                _localDb.Ado.ExecuteCommand(
                    "UPDATE _SyncWatermark SET WatermarkValue = @wm, LastSyncTime = @now " +
                    "WHERE TableName = @tableName AND TargetName = @targetName",
                    new SugarParameter("@wm", watermarkValue),
                    new SugarParameter("@now", DateTime.UtcNow.ToString("O")),
                    new SugarParameter("@tableName", tableName),
                    new SugarParameter("@targetName", targetName)
                );
            }
            else
            {
                _localDb.Ado.ExecuteCommand(
                    "INSERT INTO _SyncWatermark (TableName, TargetName, WatermarkType, WatermarkValue, LastSyncTime) " +
                    "VALUES (@tableName, @targetName, 'DateTime', @wm, @now)",
                    new SugarParameter("@tableName", tableName),
                    new SugarParameter("@targetName", targetName),
                    new SugarParameter("@wm", watermarkValue),
                    new SugarParameter("@now", DateTime.UtcNow.ToString("O"))
                );
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"写入水位线失败 [{tableName}/{targetName}]: {ex.Message}");
        }
    }

    /// <summary>
    /// 读取指定表+目标的最后同步时间（用于清理已同步的过期数据）。
    /// </summary>
    public DateTime? GetLastSyncTime(string tableName, string targetName)
    {
        try
        {
            var sql = "SELECT LastSyncTime FROM _SyncWatermark WHERE TableName = @tableName AND TargetName = @targetName";
            var result = _localDb.Ado.SqlQuery<DateTime?>(sql,
                new SugarParameter("@tableName", tableName),
                new SugarParameter("@targetName", targetName)
            );

            if (result != null && result.Count > 0 && result[0] != null)
                return result[0];

            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning($"读取最后同步时间失败 [{tableName}/{targetName}]: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 注意：不再直接 _localDb.Dispose()，因为它是共享的
        // 由 SyncEngine 负责释放
    }
}
