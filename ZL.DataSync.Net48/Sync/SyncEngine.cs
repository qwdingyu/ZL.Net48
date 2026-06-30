using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlSugar;
using ZL.DataSync.Config;
using ZL.DataSync.Infrastructure;
using ZL.DataSync.Pipeline;

namespace ZL.DataSync;

/// <summary>
/// 数据同步引擎：后台运行，自动发现本地表，按策略分发到多个远程目标。
/// 支持断网续传、指数退避重试、水位线追踪、过期数据清理。
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private readonly DataSyncConfig _config;
    private readonly IStructuredLogger _logger;
    private readonly WatermarkStore _watermark;
    private readonly CancellationTokenSource _cts = new();
    private readonly SyncStatus _status = new();
    private bool _disposed;

    // 共享的本地 SQLite 连接（复用，避免频繁创建）
    private readonly ISqlSugarClient _localDb;

    // 每个目标的策略缓存 + 任务
    private readonly object _strategyLock = new();
    private readonly Dictionary<string, (ISyncStrategy Strategy, Task Task)> _targetEntries = new();

    // 已发现的本地表（线程安全只读）
    private HashSet<string> _discoveredTables;

    // 清理任务（需要等待其完成以释放 SQLite 文件锁）
    private Task? _cleanupTask;

    /// <summary>同步状态查询</summary>
    public SyncStatus Status => _status;

    /// <summary>
    /// 是否使用外部共享连接（不释放）。
    /// </summary>
    private readonly bool _ownsLocalDb;

    /// <summary>
    /// 创建同步引擎（使用共享的本地 SQLite 连接）。
    /// </summary>
    /// <param name="config">同步配置</param>
    /// <param name="logger">日志（null 时使用 DebugLogger）</param>
    /// <param name="sharedLocalClient">共享的 ISqlSugarClient（用于避免多连接打开同一 SQLite 文件）</param>
    public SyncEngine(DataSyncConfig config, IStructuredLogger? logger = null, ISqlSugarClient? sharedLocalClient = null)
        : this(config, logger, sharedLocalClient, ownsLocalDb: sharedLocalClient == null)
    {
    }

    /// <summary>
    /// 创建同步引擎（内部构造器）。
    /// </summary>
    private SyncEngine(DataSyncConfig config, IStructuredLogger? logger, ISqlSugarClient? sharedLocalClient, bool ownsLocalDb)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(_config.LocalDbPath))
            throw new ArgumentException("LocalDbPath 不能为空", nameof(config));

        _logger = logger ?? new Infrastructure.DebugLogger();
        _ownsLocalDb = ownsLocalDb;

        // 使用外部传入的共享连接，避免多客户端打开同一 SQLite 文件
        _localDb = sharedLocalClient ?? new SqlSugarClient(new ConnectionConfig
        {
            DbType = SqlSugar.DbType.Sqlite,
            ConnectionString = $"Data Source={_config.LocalDbPath}",
            IsAutoCloseConnection = false  // 复用连接
        });

        _watermark = new WatermarkStore(_localDb);
        _watermark.EnsureTable();
        _status.Reset();
        // 初始为空集合，Start() 中 DiscoverLocalTables() 会赋值
        _discoveredTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 创建同步引擎（使用独立的本地连接，向后兼容）。
    /// </summary>
    /// <param name="config">同步配置</param>
    /// <param name="logger">日志（null 时使用 DebugLogger）</param>
    public SyncEngine(DataSyncConfig config, IStructuredLogger? logger)
        : this(config, logger, null)
    {
    }

    /// <summary>
    /// 启动同步引擎。后台开始所有目标的同步循环。
    /// </summary>
    public void Start()
    {
        if (_status.IsRunning)
        {
            _logger.Warning("同步引擎已在运行中");
            return;
        }

        _status.IsRunning = true;
        _status.LastStartTime = DateTime.UtcNow;
        _status.StatusText = "启动中";

        // 发现本地表
        DiscoverLocalTables();

        // 为每个目标创建策略并启动同步循环
        foreach (var target in _config.RemoteTargets)
        {
            var strategy = CreateStrategy(target);
            var task = RunTargetLoopAsync(target, strategy, _cts.Token);
            lock (_strategyLock)
            {
                _targetEntries[target.Name] = (strategy, task);
            }
            _logger.Info($"[{target.Name}] 同步循环已启动，间隔 {_config.SyncIntervalSeconds}s");
        }

        // 启动清理循环（如果启用）
        if (_config.EnableCleanup)
        {
            _cleanupTask = Task.Run(() => CleanupLoopAsync(_cts.Token), _cts.Token);
            _logger.Info($"数据清理已启动，间隔 {_config.CleanupIntervalSeconds}s，保留 {_config.DataRetentionDays} 天");
        }

        _status.StatusText = "运行中";
        _logger.Info($"同步引擎启动完成，{_discoveredTables.Count} 个表, {_config.RemoteTargets.Count} 个目标");
    }

    /// <summary>
    /// 停止同步引擎。等待所有目标完成当前同步后优雅退出。
    /// </summary>
    public async Task StopAsync()
    {
        if (!_status.IsRunning) return;

        _logger.Info("正在停止同步引擎...");
        _cts.Cancel();

        List<Task> runningTasks;
        lock (_strategyLock)
        {
            runningTasks = _targetEntries.Values.Select(e => e.Task).ToList();
        }

        foreach (var task in runningTasks)
        {
            try { await Task.WhenAny(task, Task.Delay(10000)).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Warning($"停止同步循环时异常: {ex.Message}"); }
        }

        // 清理策略
        List<ISyncStrategy> strategies;
        lock (_strategyLock)
        {
            strategies = _targetEntries.Values.Select(e => e.Strategy).ToList();
            _targetEntries.Clear();
        }

        foreach (var s in strategies) s.Dispose();

        // 等待清理任务完成（如果有）
        if (_cleanupTask != null)
        {
            try
            {
                await Task.WhenAny(_cleanupTask, Task.Delay(10000)).ConfigureAwait(false);
                if (!_cleanupTask.IsCompleted)
                    _logger.Warning("清理任务在超时后仍未完成");
            }
            catch (Exception ex) { _logger.Warning($"停止清理循环时异常: {ex.Message}"); }
        }

        _status.IsRunning = false;
        _status.StatusText = "已停止";
        _logger.Info("同步引擎已停止");
    }

    /// <summary>
    /// 手动触发一次同步（非周期性的，用于 UI 按钮等场景）。
    /// </summary>
    public async Task<Dictionary<string, SyncReport>> ForceSyncAsync()
    {
        var reports = new Dictionary<string, SyncReport>();

        foreach (var target in _config.RemoteTargets)
        {
            ISyncStrategy strategy;
            lock (_strategyLock)
            {
                strategy = CreateStrategy(target);
            }

            try
            {
                var report = await SyncAllTablesAsync(target, strategy, CancellationToken.None).ConfigureAwait(false);
                reports[target.Name] = report;
            }
            catch (Exception ex)
            {
                reports[target.Name] = SyncReport.Fail(target.Name, 0, ex.Message, 0);
            }
            finally
            {
                strategy.Dispose();
            }
        }

        return reports;
    }

    // ═══════════════════════════════════════════════════════════════
    //  核心循环
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 单个目标的后台同步循环。
    /// 按固定间隔轮询，异常时指数退避重试。
    ///
    /// 设计决策：
    /// - 指数退避上限 = 2^8 * backoffSeconds = 256 * backoffSeconds，最大 300 秒
    /// - 连续成功后 failStreak 清零，重新开始计数
    /// </summary>
    private async Task RunTargetLoopAsync(RemoteTargetConfig target, ISyncStrategy strategy, CancellationToken ct)
    {
        int failStreak = 0;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_config.SyncIntervalSeconds), ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var report = await SyncAllTablesAsync(target, strategy, ct).ConfigureAwait(false);
                if (report.TargetCount > 0)
                {
                    failStreak = 0;
                    _status.FailStreak = failStreak;
                    _status.AddSynced(report.SyncedCount);
                    _status.AddFailed(report.FailedCount);
                    _status.LastSyncTime = DateTime.UtcNow;
                    _status.StatusText = report.Success ? "同步中" : $"失败({report.FailedCount})";
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                failStreak++;
                _status.FailStreak = failStreak;
                _status.LastError = ex.Message;
                _status.StatusText = $"异常({failStreak})";

                // 指数退避：min(2^failStreak * backoffSeconds, 300秒)
                // 限制最大指数为 8（2^8 = 256），避免无限增长
                int wait = Math.Min(
                    (int)Math.Pow(2, Math.Min(failStreak, 8)) * _config.RetryBackoffSeconds,
                    300);

                _logger.Warning($"[{target.Name}] 同步失败: {ex.Message}，{wait}s后重试");
                try { await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            _logger.Debug($"[{target.Name}] 同步循环完成: {sw.ElapsedMilliseconds}ms");
        }

        strategy.Dispose();
    }

    /// <summary>
    /// 同步所有已发现的表到一个目标。
    /// 串行执行（SQLite 单写者模型，并发写入会导致 database is locked）。
    /// </summary>
    private async Task<SyncReport> SyncAllTablesAsync(RemoteTargetConfig target, ISyncStrategy strategy, CancellationToken ct)
    {
        int totalTarget = 0;
        int totalOk = 0;
        int totalFail = 0;
        string? lastError = null;
        DateTime? lastWatermark = null;

        var localClient = _localDb as SqlSugarClient ?? throw new InvalidOperationException("本地数据库必须是 SqlSugarClient");

        // 串行同步所有表（SQLite 不支持多写者，并发写入会导致 database is locked）
        var reports = new List<SyncReport>();
        foreach (var table in _discoveredTables)
        {
            if (ct.IsCancellationRequested) break;

            string? remoteTable = target.TableMappings.TryGetValue(table, out var alias) ? alias : null;
            var report = await strategy.SyncTableAsync(table, remoteTable, _config.BatchSize, localClient, ct).ConfigureAwait(false);
            reports.Add(report);
        }

        foreach (var report in reports)
        {
            if (ct.IsCancellationRequested) break;

            totalTarget += report.TargetCount;
            totalOk += report.SyncedCount;
            totalFail += report.FailedCount;

            if (!report.Success && string.IsNullOrEmpty(lastError))
                lastError = report.LastError;

            if (report.LastWatermark != null && DateTime.TryParse(report.LastWatermark, out var wm))
                lastWatermark ??= wm;
        }

        if (totalFail == 0 && totalOk > 0)
        {
            return SyncReport.Ok($"[{target.Name}] 汇总", totalTarget, totalOk, lastWatermark?.ToString("O") ?? "success", 0);
        }

        return SyncReport.Fail($"[{target.Name}] 汇总", totalTarget, totalFail, lastError ?? "未知错误", 0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  清理
    // ═══════════════════════════════════════════════════════════════

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_config.CleanupIntervalSeconds), ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;

            try
            {
                await CleanupSyncedDataAsync(_config.DataRetentionDays, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.Warning($"数据清理异常: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 清理已同步的过期数据（分批删除，P1-2 修复）。
    /// </summary>
    private async Task CleanupSyncedDataAsync(int retentionDays, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        int totalDeleted = 0;
        const int MaxCleanupBatchSize = 5000;

        foreach (var table in _discoveredTables)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                bool hasSynced = HasColumn(_localDb, table, "_Synced");
                if (!hasSynced) continue;

                // 分批删除：每次删除最多 MaxCleanupBatchSize 条
                int deleted = 0;
                do
                {
                    var ids = await _localDb.Queryable<dynamic>()
                        .AS(table)
                        .Where("_Synced = 1 AND ProcessTime < @cutoff", cutoff)
                        .Select("Id")
                        .Take(MaxCleanupBatchSize)
                        .ToListAsync().ConfigureAwait(false);

                    if (ids == null || ids.Count == 0) break;
                    var idList = ids
                        .OfType<IDictionary<string, object?>>()
                        .Select(d => d.ContainsKey("Id") ? d["Id"] : null)
                        .Where(id => id != null)
                        .Distinct()
                        .ToList();
                    if (idList.Count == 0) break;

                    // 构建参数化 DELETE
                    var parameters = new List<SugarParameter>();
                    var conditions = new List<string>();
                    for (int i = 0; i < idList.Count; i++)
                    {
                        conditions.Add($"@id{i}");
                        parameters.Add(new SugarParameter($"@id{i}", idList[i]));
                    }

                    int rowsAffected = await _localDb.Ado.ExecuteCommandAsync(
                        $"DELETE FROM [{table}] WHERE Id IN ({string.Join(",", conditions)})",
                        parameters.ToArray()
                    ).ConfigureAwait(false);

                    deleted += rowsAffected;
                    totalDeleted += rowsAffected;
                } while (deleted >= MaxCleanupBatchSize); // 持续循环直到本轮删除不足上限
            }
            catch (Exception ex)
            {
                _logger.Warning($"清理表 {table} 异常: {ex.Message}");
            }
        }

        if (totalDeleted > 0)
            _logger.Info($"数据清理完成: 删除 {totalDeleted} 条记录");
    }

    private static bool HasColumn(ISqlSugarClient db, string table, string colName)
    {
        try
        {
            var cols = db.Ado.SqlQuery<ColumnInfoRow>($"PRAGMA table_info(\"{table}\")");
            return cols != null && cols.Any(r => r.Name == colName);
        }
        catch (Exception)
        {
            return false; // PRAGMA 失败（表不存在等）不影响主流程
        }
    }

    private sealed class ColumnInfoRow
    {
        public string? Name { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  表发现 & 策略创建
    // ═══════════════════════════════════════════════════════════════

    private void DiscoverLocalTables()
    {
        try
        {
            // 排除系统表（_SyncWatermark, _SyncLog）
            var tables = _localDb.Ado.SqlQuery<TableInfoRow>(
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE '_Sync%' ORDER BY name");

            if (tables != null)
            {
                _discoveredTables = new HashSet<string>(
                    tables
                    .Select(t => t.Name ?? string.Empty)
                    .Where(n => !string.IsNullOrWhiteSpace(n)),
                    StringComparer.OrdinalIgnoreCase);
                _logger.Info($"发现 {_discoveredTables.Count} 个本地业务表: {string.Join(", ", _discoveredTables)}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"发现本地表失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 根据目标和策略类型创建对应的同步策略实例。
    /// 注意：策略内部自行管理远程连接池（SqlSugarScope），无需外部传入。
    /// </summary>
    private ISyncStrategy CreateStrategy(RemoteTargetConfig target)
    {
        if (target.StrategyType == Config.SyncStrategyType.ProcessTime)
        {
            return new ProcessTimeSyncStrategy(target, _logger);
        }

        return target.Type switch
        {
            Config.TargetType.MySql or Config.TargetType.SqlServer or Config.TargetType.PostgreSql or Config.TargetType.Oracle =>
                new Pipeline.DatabaseSyncStrategy(target, _logger),
            Config.TargetType.Http =>
                new Pipeline.HttpSyncStrategy(target.HttpConfig ?? throw new InvalidOperationException($"HTTP 目标 {target.Name} 缺少 HttpConfig"),
                    target.Name, _logger),
            _ => throw new ArgumentOutOfRangeException(nameof(target.Type), $"不支持的目标类型: {target.Type}")
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 安全释放：使用 Task.Wait 替代 .GetAwaiter().GetResult() 避免 SynchronizationContext 死锁
        DisposeSynchronously();

        // 等待清理任务完成（最多 15 秒），确保 SQLite 连接全部释放
        if (_cleanupTask != null)
        {
            try { _cleanupTask.Wait(TimeSpan.FromSeconds(15)); }
            catch { /* 忽略等待期间的异常 */ }
        }

        // 仅在 ownsLocalDb 为 true 时才释放连接（由 SyncEngine 自己创建的连接才由它释放）
        if (_ownsLocalDb)
        {
            // 显式关闭底层连接，确保 SQLite 文件锁及时释放，避免 TestCleanup 删除失败
            if (_localDb is SqlSugarClient sqlClient)
                sqlClient.Close();
            _localDb?.Dispose();
        }
        _watermark?.Dispose();
    }

    /// <summary>
    /// 同步释放资源，避免 Dispose 中调用 async 方法导致的死锁。
    /// 注意：_targetEntries 在第一个锁中已 Clear，所以需要在第一个锁中收集 strategies。
    /// </summary>
    private void DisposeSynchronously()
    {
        _cts.Cancel();

        List<Task> runningTasks;
        List<ISyncStrategy> strategies;

        // 一次性收集任务和策略（_targetEntries 在锁内被清空）
        lock (_strategyLock)
        {
            runningTasks = _targetEntries.Values.Select(e => e.Task).ToList();
            strategies = _targetEntries.Values.Select(e => e.Strategy).ToList();
            _targetEntries.Clear();
        }

        // 等待目标同步任务完成（最多 15 秒）
        // 注意：不等待 _cleanupTask，因为 CleanupLoopAsync 可能因 CleanupIntervalSeconds 很长而阻塞在 Task.Delay
        // 连接将在 Dispose 中显式关闭，确保文件锁释放
        try
        {
            Task.WaitAll(runningTasks.ToArray(), TimeSpan.FromSeconds(15));
        }
        catch
        {
            // 忽略等待期间的异常
        }

        // 释放策略（包括远程 SqlSugarScope）
        foreach (var s in strategies)
        {
            try { s.Dispose(); }
            catch (Exception ex) { _logger.Warning($"释放策略时异常: {ex.Message}"); }
        }

        _status.IsRunning = false;
        _status.StatusText = "已停止";
    }

    private sealed class TableInfoRow
    {
        public string? Name { get; set; }
    }
}
