namespace ZL.DataSync;

/// <summary>
/// 同步报告：一次同步循环的结果。
/// </summary>
public sealed class SyncReport
{
    public DateTime Timestamp { get; set; }
    public string TableName { get; set; } = string.Empty;
    public int TargetCount { get; set; }       // 目标库中待同步的记录数
    public int SyncedCount { get; set; }        // 实际成功同步的数量
    public int FailedCount { get; set; }        // 失败的数量
    public string? LastError { get; set; }
    public string? LastWatermark { get; set; }  // 上次成功同步的水位值
    public double ElapsedMs { get; set; }

    /// <summary>
    /// 同步成功：没有失败（包括无数据的情况）。
    /// </summary>
    public bool Success => FailedCount == 0;

    public bool HasData => TargetCount > 0;

    public static SyncReport Ok(string tableName, int target, int synced, string? watermark, double elapsedMs)
        => new()
        {
            Timestamp = DateTime.UtcNow,
            TableName = tableName,
            TargetCount = target,
            SyncedCount = synced,
            FailedCount = 0,
            LastWatermark = watermark,
            ElapsedMs = elapsedMs
        };

    /// <summary>
    /// 同步失败报告。
    /// </summary>
    public static SyncReport Fail(string tableName, int target, string? error, double elapsedMs)
        => new()
        {
            Timestamp = DateTime.UtcNow,
            TableName = tableName,
            TargetCount = target,
            SyncedCount = 0,
            FailedCount = target > 0 ? target : 1,  // 即使 target=0，有错误也标记为 1 条失败，避免 Success 误判
            LastError = error,
            ElapsedMs = elapsedMs
        };

    /// <summary>
    /// 同步失败报告（可指定实际失败数）。
    /// </summary>
    public static SyncReport Fail(string tableName, int target, int failedCount, string? error, double elapsedMs)
        => new()
        {
            Timestamp = DateTime.UtcNow,
            TableName = tableName,
            TargetCount = target,
            SyncedCount = 0,
            FailedCount = failedCount,
            LastError = error,
            ElapsedMs = elapsedMs
        };
}

/// <summary>
/// 同步引擎运行状态。
/// 
/// 线程安全说明：
/// - TotalSynced / TotalFailed 使用 Interlocked 操作（_totalSynced / _totalFailed 为 internal long 字段）
/// - SyncEngine 直接操作 _totalSynced/_totalFailed，避免通过属性 getter 传给 Interlocked.Add（属性返回 int 值，非引用）
/// - FailStreak / StatusText 仅在单线程 RunTargetLoop 中更新，无需加锁
/// </summary>
public sealed class SyncStatus
{
    public bool IsRunning { get; set; }
    public int TotalTables { get; set; }

    // 使用 long + 内部字段支持 Interlocked.Add（SyncEngine 需要在外部调用 Interlocked.Add）
    internal long _totalSynced;
    internal long _totalFailed;

    /// <summary>累计成功同步的记录数（通过 Interlocked.Add 更新，线程安全）</summary>
    public int TotalSynced
    {
        get => (int)Interlocked.Read(ref _totalSynced);
        set => Interlocked.Exchange(ref _totalSynced, value);
    }

    /// <summary>累计失败同步的记录数（通过 Interlocked.Add 更新，线程安全）</summary>
    public int TotalFailed
    {
        get => (int)Interlocked.Read(ref _totalFailed);
        set => Interlocked.Exchange(ref _totalFailed, value);
    }
    public DateTime? LastSyncTime { get; set; }
    public DateTime? LastStartTime { get; set; }
    public int FailStreak { get; set; }  // 连续失败次数
    public string? LastError { get; set; }
    public string? StatusText { get; set; } = "未启动";  // 用于 UI 展示的状态文本

    /// <summary>
    /// 健康：未运行 或 从未连续失败过。
    /// </summary>
    public bool IsHealthy => !IsRunning || FailStreak == 0;

    /// <summary>线程安全地增加同步成功计数</summary>
    public void AddSynced(int count)
    {
        Interlocked.Add(ref _totalSynced, count);
    }

    /// <summary>线程安全地增加同步失败计数</summary>
    public void AddFailed(int count)
    {
        Interlocked.Add(ref _totalFailed, count);
    }

    public void Reset()
    {
        TotalTables = 0;
        Interlocked.Exchange(ref _totalSynced, 0);
        Interlocked.Exchange(ref _totalFailed, 0);
        LastSyncTime = null;
        LastStartTime = null;
        FailStreak = 0;
        LastError = null;
        StatusText = "未启动";
    }
}
