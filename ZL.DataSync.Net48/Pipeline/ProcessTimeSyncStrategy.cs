using SqlSugar;
using ZL.DataSync.Config;
using ZL.DataSync.Infrastructure;

namespace ZL.DataSync.Pipeline;

/// <summary>
/// 基于 <c>ProcessTime</c> 增量标记的同步策略。
///
/// 工作流程：
/// <code>
/// 1. 读 _SyncLog 水位线: SELECT SyncTime FROM _SyncLog WHERE TableName = @tableName ORDER BY SyncTime DESC LIMIT 1
/// 2. 增量读取: SELECT * FROM table WHERE ProcessTime > @lastTime ORDER BY ProcessTime LIMIT @batchSize
/// 3. 批量写入远程
/// 4. 原子更新水位线: INSERT OR REPLACE INTO _SyncLog (TableName, SyncTime)
/// </code>
///
/// 适用场景：与 PcStationIot RemoteSyncService 集成的场景，不修改业务表中的任何标记字段。
/// </summary>
public sealed class ProcessTimeSyncStrategy : SyncStrategyBase
{
    private readonly RemoteTargetConfig _target;

    /// <summary>
    /// 创建 ProcessTime 同步策略。
    /// </summary>
    /// <param name="target">远程目标配置</param>
    /// <param name="logger">结构化日志</param>
    public ProcessTimeSyncStrategy(RemoteTargetConfig target, IStructuredLogger logger)
        : base(GetName(target), GetConnStr(target), GetDbType(target), logger ?? throw new ArgumentNullException(nameof(logger)))
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    private static string GetName(RemoteTargetConfig t) => t?.Name ?? throw new ArgumentNullException(nameof(t), "目标 Name 不能为空");
    private static string GetConnStr(RemoteTargetConfig t) => t?.ConnectionString ?? throw new ArgumentNullException(nameof(RemoteTargetConfig));
    private static SqlSugar.DbType GetDbType(RemoteTargetConfig t) => t == null ? throw new ArgumentNullException(nameof(RemoteTargetConfig)) : SqlSugarHelpers.MapDbType(t.Type);

    /// <summary>
    /// 同步单表数据到远程目标（基于 ProcessTime 增量）。
    /// </summary>
    /// <param name="tableName">本地表名</param>
    /// <param name="remoteTable">远程表名映射（可为 null，默认同本地表名）</param>
    /// <param name="batchSize">批次大小</param>
    /// <param name="localDb">本地 SQLite 连接（由引擎提供，复用连接）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>同步报告</returns>
    public override async Task<SyncReport> SyncTableAsync(
        string tableName,
        string? remoteTable,
        int batchSize,
        SqlSugarClient localDb,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string targetTable = string.IsNullOrWhiteSpace(remoteTable) ? tableName : remoteTable;

        // 1. 读取 _SyncLog 水位线（与 PcStationIot RemoteSyncService.ReadSyncTimeAsync 一致）
        DateTime? lastSyncTime = await ReadSyncLogAsync(localDb, tableName, ct).ConfigureAwait(false);

        // 2. 读取未同步数据（基于 ProcessTime 增量）
        var dynamicRows = localDb.Ado.SqlQuery<dynamic>(
            $"SELECT * FROM {SqlSugarHelpers.QuoteIdentifier(tableName, SqlSugar.DbType.Sqlite)} " +
            $"WHERE ProcessTime > @lastTime ORDER BY ProcessTime LIMIT @limit",
            new SugarParameter("@lastTime", lastSyncTime ?? DateTime.MinValue),
            new SugarParameter("@limit", batchSize)
        );

        var rows = SqlSugarHelpers.FilterValidRows(dynamicRows);
        if (rows.Count == 0)
            return SyncReport.Ok(tableName, 0, 0, null, sw.Elapsed.TotalMilliseconds);

        // 3. 确保远程表存在
        await EnsureTableAsync(targetTable, rows[0], ct).ConfigureAwait(false);

        // 4. 批量写入远程（每 MaxRemoteBatchSize 条一批）
        // 水位线逻辑：只从 successRows 中提取最大 ProcessTime，
        // 避免写入失败的数据导致水位线误推进（否则失败数据会被永久跳过）。
        const int MaxRemoteBatchSize = 50;
        int ok = 0;
        int fail = 0;
        var successRows = new List<Dictionary<string, object?>>(rows.Count);

        for (int i = 0; i < rows.Count; i += MaxRemoteBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int batchEnd = Math.Min(i + MaxRemoteBatchSize, rows.Count);

            try
            {
                var batchValidRows = SqlSugarHelpers.ExtractValidRows(rows, i, batchEnd);
                if (batchValidRows.Count > 0)
                {
                    await InsertRowsViaAdoAsync(targetTable, batchValidRows, ct).ConfigureAwait(false);

                    foreach (var row in batchValidRows)
                    {
                        ok++;
                        successRows.Add(row);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[{TargetName}] 批量写入失败 {targetTable}: {ex.Message}");
                // 批量失败，逐条回退
                var result = await FailoverBatchAsync(targetTable, rows, i, batchEnd, successRows, ct).ConfigureAwait(false);
                ok += result.Ok;
                fail += result.Fail;
            }
        }

        // 5. 原子更新 _SyncLog 水位线（只从成功写入的行中提取最大 ProcessTime）
        DateTime? maxProcessTime = null;
        if (successRows.Count > 0)
        {
            foreach (var row in successRows)
            {
                if (SqlSugarHelpers.TryGetProcessTime(row, out var pt) && (maxProcessTime == null || pt > maxProcessTime.Value))
                    maxProcessTime = pt;
            }
        }

        if (successRows.Count > 0 && maxProcessTime.HasValue)
        {
            try
            {
                await WriteSyncLogAsync(localDb, tableName, maxProcessTime.Value, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[{TargetName}] 写入 _SyncLog 水位线失败 {tableName}: {ex.Message}");
            }
        }

        int totalProcessed = ok + fail;
        return fail == 0 && totalProcessed > 0
            ? SyncReport.Ok(tableName, rows.Count, ok, maxProcessTime?.ToString("o"), sw.Elapsed.TotalMilliseconds)
            : SyncReport.Fail(tableName, rows.Count, $"成功 {ok}/{rows.Count}, 失败 {fail}", sw.Elapsed.TotalMilliseconds);
    }

    // ═══════════════════════════════════════════════════════════════
    //  _SyncLog 水位线读写
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 读取 _SyncLog 水位线。与 RemoteSyncService.ReadSyncTimeAsync 逻辑一致。
    /// </summary>
    private async Task<DateTime?> ReadSyncLogAsync(SqlSugarClient localDb, string tableName, CancellationToken ct)
    {
        try
        {
            var result = await localDb.Ado.SqlQueryAsync<string>(
                "SELECT SyncTime FROM _SyncLog WHERE TableName = @tableName ORDER BY SyncTime DESC LIMIT 1",
                new SugarParameter("@tableName", tableName)
            );
            if (result != null && result.Count > 0 && result[0] != null &&
                DateTime.TryParse(result[0], out var d))
                return d;
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[{TargetName}] 读取 _SyncLog 水位线失败 {tableName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 原子写入 _SyncLog 水位线。使用 INSERT OR REPLACE 避免 COUNT+INSERT/UPDATE 的非原子问题。
    /// 表结构: TableName (TEXT PK), SyncTime (TEXT ISO-8601)
    /// </summary>
    private async Task WriteSyncLogAsync(SqlSugarClient localDb, string tableName, DateTime processTime, CancellationToken ct)
    {
        try
        {
            await localDb.Ado.ExecuteCommandAsync(
                "INSERT OR REPLACE INTO _SyncLog (TableName, SyncTime) VALUES (@tableName, @syncTime)",
                new SugarParameter("@tableName", tableName),
                new SugarParameter("@syncTime", processTime.ToString("o"))
            );
        }
        catch (Exception ex)
        {
            Logger.Warning($"[{TargetName}] 写入 _SyncLog 失败 {tableName}: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  辅助方法
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 批量写入失败时逐条回退容错。
    /// </summary>
    /// <returns>成功数、失败数</returns>
    private async Task<(int Ok, int Fail)> FailoverBatchAsync(
        string targetTable,
        List<Dictionary<string, object?>> rows,
        int start,
        int end,
        List<Dictionary<string, object?>> successRows,
        CancellationToken ct)
    {
        int ok = 0;
        int fail = 0;

        for (int j = start; j < end; j++)
        {
            var row = rows[j];
            if (row == null || row.Count == 0) continue;

            try
            {
                await InsertRowViaAdoAsync(targetTable, row, ct).ConfigureAwait(false);
                ok++;
                successRows.Add(row);
            }
            catch (Exception ex2)
            {
                fail++;
                Logger.Warning($"[{TargetName}] 单条写入失败 {targetTable}: {ex2.Message}");
            }
        }

        return (ok, fail);
    }
}
