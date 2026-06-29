using SqlSugar;
using ZL.DataSync.Config;
using ZL.DataSync.Infrastructure;

namespace ZL.DataSync.Pipeline;

/// <summary>
/// 基于 <c>_Synced = 0</c> 标记的数据库远程同步策略。
///
/// 工作流程：
/// <code>
/// 1. 读 SQLite: SELECT * FROM table WHERE _Synced = 0 ORDER BY ProcessTime LIMIT @batchSize
/// 2. 确保远程表存在（自动建表）
/// 3. 批量写入远程（每 50 条一批，失败逐条回退）
/// 4. 标记本地: UPDATE table SET _Synced = 1 WHERE Id IN (...)
/// </code>
///
/// 适用场景：MySQL / SQL Server / PostgreSQL / Oracle 等数据库远程同步。
/// </summary>
public sealed class DatabaseSyncStrategy : SyncStrategyBase
{
    private readonly RemoteTargetConfig _target;

    /// <summary>
    /// 创建数据库同步策略。
    /// </summary>
    /// <param name="target">远程目标配置</param>
    /// <param name="logger">结构化日志</param>
    public DatabaseSyncStrategy(RemoteTargetConfig target, IStructuredLogger logger)
        : base(target.Name, target.ConnectionString, SqlSugarHelpers.MapDbType(target.Type), logger)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    /// <summary>
    /// 同步单表数据到远程目标。
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

        // 1. 读取未同步数据（基于 _Synced = 0 标记）
        // 注意：SqlSugar 的 SqlQuery&lt;Dictionary&gt; 返回空 keys，必须用 SqlQuery&lt;dynamic&gt; 再转换
        var dynamicRows = localDb.Ado.SqlQuery<dynamic>(
            $"SELECT * FROM {SqlSugarHelpers.QuoteIdentifier(tableName, SqlSugar.DbType.Sqlite)} " +
            $"WHERE {SqlSugarHelpers.SyncColumn} = 0 ORDER BY ProcessTime LIMIT @limit",
            new SugarParameter("@limit", batchSize)
        );

        var rows = SqlSugarHelpers.FilterValidRows(dynamicRows);
        if (rows.Count == 0)
            return SyncReport.Ok(tableName, 0, 0, null, sw.Elapsed.TotalMilliseconds);

        // 2. 应用数据过滤和转换（扩展点）
        rows = ApplyFiltersAndTransforms(rows, _target);
        if (rows.Count == 0)
            return SyncReport.Ok(tableName, 0, 0, null, sw.Elapsed.TotalMilliseconds);

        // 3. 确保远程表存在（DCL 双重检查锁）
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

                    // 写入成功后，累加成功行
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
                // 批量失败，逐条回退容错
                var result = await FailoverBatchAsync(targetTable, rows, i, batchEnd, successRows, ct).ConfigureAwait(false);
                ok += result.Ok;
                fail += result.Fail;
            }
        }

        // 5. 批量标记本地同步成功（先远程写入成功后，再统一标记 — 减少数据丢失风险）
        if (successRows.Count > 0)
        {
            try
            {
                await BatchMarkSyncedAsync(localDb, tableName, successRows, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[{TargetName}] 批量标记同步失败 {tableName}: {ex.Message}");
                // 标记失败不影响主流程，下次循环会继续
            }
        }

        int totalProcessed = ok + fail;
        return fail == 0 && totalProcessed > 0
            ? SyncReport.Ok(tableName, rows.Count, ok, null, sw.Elapsed.TotalMilliseconds)
            : SyncReport.Fail(tableName, rows.Count, $"成功 {ok}/{rows.Count}, 失败 {fail}", sw.Elapsed.TotalMilliseconds);
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
