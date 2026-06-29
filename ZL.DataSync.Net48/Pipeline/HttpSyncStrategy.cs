using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SqlSugar;
using ZL.DataSync.Config;
using ZL.DataSync.Infrastructure;

namespace ZL.DataSync.Pipeline;

/// <summary>
/// 基于 HTTP API 的数据上传策略。
///
/// 工作流程：
/// <code>
/// 1. 读 SQLite: SELECT * FROM table WHERE _Synced = 0 ORDER BY ProcessTime LIMIT @batchSize
/// 2. 构建 JSON 请求体（兼容 DataUploadServer 的 DataUploadReq 格式）
/// 3. 批量 POST 到远程 API（每批最多 20 条）
/// 4. 上传成功后标记本地 _Synced = 1
/// </code>
///
/// 设计决策：
/// - 使用静态 HttpClient 连接池，避免频繁创建销毁导致端口耗尽
/// - PooledConnectionLifetime = 2 分钟：短于 DNS 刷新间隔，确保域名解析更新能及时生效
/// - 每批次最多 20 条：HTTP API 通常有 payload 大小限制，20 是经验值
/// - 静态 HttpClient 在应用退出时由 GC 自动回收，此处不主动 Dispose
/// </summary>
public sealed class HttpSyncStrategy : ISyncStrategy
{
    /// <summary>共享 HTTP 客户端（静态，整个进程生命周期复用）</summary>
    private static readonly HttpClient s_http = CreateSharedHttpClient();
    
    /// <summary>应用退出时的 Dispose 回调，确保 HttpClient 被正确释放</summary>
    private static readonly Action s_onExit = () => s_http.Dispose();
    
    static HttpSyncStrategy()
    {
        // 注册应用退出回调（仅控制台应用有效）
        AppDomain.CurrentDomain.ProcessExit += (s, e) => s_onExit?.Invoke();
    }

    private readonly HttpUploadConfig _config;
    private readonly string _targetName;
    private readonly IStructuredLogger _logger;

    /// <summary>
    /// 创建共享 HttpClient。PooledConnectionLifetime 设为 2 分钟，
    /// 确保 DNS 解析变更能及时反映（避免长连接导致旧 IP 一直使用）。
    /// </summary>
    private static HttpClient CreateSharedHttpClient()
    {
        return new HttpClient(new HttpClientHandler())
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// 创建 HTTP 同步策略。
    /// </summary>
    /// <param name="config">HTTP 上传配置</param>
    /// <param name="targetName">目标名称</param>
    /// <param name="logger">结构化日志</param>
    public HttpSyncStrategy(HttpUploadConfig config, string targetName, IStructuredLogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _targetName = targetName ?? throw new ArgumentNullException(nameof(targetName));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string TargetName => _targetName;

    /// <summary>
    /// 从 SQLite 读取未同步的记录，批量 POST 到 API。
    /// </summary>
    /// <param name="tableName">本地表名</param>
    /// <param name="remoteTable">远程表名映射（HTTP 策略忽略此参数）</param>
    /// <param name="batchSize">批次大小</param>
    /// <param name="localDb">本地 SQLite 连接</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>同步报告</returns>
    public async Task<SyncReport> SyncTableAsync(
        string tableName,
        string? remoteTable,
        int batchSize,
        SqlSugarClient localDb,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. 读未同步数据 — SqlSugar SqlQuery&lt;Dictionary&gt; 返回空 keys，必须用 dynamic 再转换
        var dynamicRows = localDb.Ado.SqlQuery<dynamic>(
            $"SELECT * FROM {SqlSugarHelpers.QuoteIdentifier(tableName, SqlSugar.DbType.Sqlite)} " +
            $"WHERE _Synced = 0 ORDER BY ProcessTime LIMIT @limit",
            new SugarParameter("@limit", batchSize)
        );

        var rows = SqlSugarHelpers.FilterValidRows(dynamicRows);
        if (rows.Count == 0)
            return SyncReport.Ok(tableName, 0, 0, null, sw.Elapsed.TotalMilliseconds);

        // 2. 确定目标 URL
        string? endpoint = GetEndpoint(tableName);
        if (string.IsNullOrEmpty(endpoint))
        {
            _logger.Warning($"HTTP 目标 {_targetName} 未配置 Endpoint，跳过表 {tableName}");
            return SyncReport.Fail(tableName, rows.Count, "未配置 HTTP Endpoint", sw.Elapsed.TotalMilliseconds);
        }

        // 3. 分批上传（使用索引迭代避免 Skip/Take 的 O(n²) 复杂度）
        const int MaxHttpBatchSize = 20; // HTTP API 通常有 payload 大小限制
        int ok = 0;
        int fail = 0;
        var successRows = new List<Dictionary<string, object?>>(rows.Count);

        for (int i = 0; i < rows.Count; i += MaxHttpBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int batchEnd = Math.Min(i + MaxHttpBatchSize, rows.Count);
            int currentBatchSize = batchEnd - i;

            // 构建 JSON 请求体
            var requestBatch = new List<Dictionary<string, object?>>(currentBatchSize);
            for (int j = i; j < batchEnd; j++)
            {
                requestBatch.Add(BuildRequestBody(tableName, rows[j]));
            }

            var json = JsonConvert.SerializeObject(requestBatch);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var resp = await s_http.PostAsync(endpoint, content, ct).ConfigureAwait(false);
                var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    ok += currentBatchSize;
                    for (int j = i; j < batchEnd; j++)
                        successRows.Add(rows[j]);
                }
                else
                {
                    fail += currentBatchSize;
                    _logger.Warning($"HTTP 上传失败 [{tableName}]: HTTP {resp.StatusCode} -> {ShortenResponse(respText, 200)}");
                }
            }
            catch (Exception ex)
            {
                fail += currentBatchSize;
                _logger.Warning($"HTTP 上传异常 [{tableName}]: {ex.Message}");
            }
        }

        // 4. 批量标记同步成功
        if (successRows.Count > 0)
        {
            try
            {
                await SqlSugarHelpers.BatchMarkSyncedAsync(localDb, tableName, SqlSugar.DbType.Sqlite, successRows, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning($"HTTP 目标 {_targetName} 批量标记同步失败 {tableName}: {ex.Message}");
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
    /// 从配置中获取表对应的端点 URL。
    /// </summary>
    private string? GetEndpoint(string tableName)
    {
        if (_config.TableEndpoints.TryGetValue(tableName, out var tableEp))
            return tableEp;
        return _config.Endpoint;
    }

    // ═══════════════════════════════════════════════════════════════
    //  辅助方法
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 构建标准 HTTP 请求体（兼容 DataUploadServer 的 DataUploadReq 格式）。
    /// </summary>
    private Dictionary<string, object?> BuildRequestBody(string tableName, Dictionary<string, object?> dict)
    {
        string? deviceName = ResolveDeviceName(dict);
        string? type = ResolveType(dict);
        string? GetOrNull(string key) => dict.TryGetValue(key, out var v) ? v?.ToString() : null;

        return new Dictionary<string, object?>
        {
            ["deviceName"] = deviceName,
            ["type"] = type,
            ["barCode"] = GetOrNull("BarCode"),
            ["EngineNo"] = GetOrNull("EngineNo"),
            ["FinalResult"] = GetOrNull("FinalResult"),
            ["DetectItems"] = GetOrNull("DetectItems"),
            ["paramArr"] = dict.TryGetValue("ParamArr", out var pa) ? pa : new List<object>(),
            ["checkResult"] = GetOrNull("FinalResult"),
            ["detectTime"] = dict.TryGetValue("ProcessTime", out var pt) && pt is DateTime d
                ? d.ToString("yyyy-MM-dd HH:mm:ss")
                : DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            ["data"] = GetOrNull("data")
        };
    }

    /// <summary>
    /// 解析设备名称（优先配置，其次从数据中提取）。
    /// </summary>
    private string? ResolveDeviceName(Dictionary<string, object?> dict)
    {
        if (_config.DeviceName != null) return _config.DeviceName;
        if (dict.TryGetValue("StationCode", out var sc) && sc is string scStr) return scStr;
        return null;
    }

    /// <summary>
    /// 解析数据分类标识（优先配置，其次从数据中提取）。
    /// </summary>
    private string? ResolveType(Dictionary<string, object?> dict)
    {
        if (_config.Type != null) return _config.Type;
        if (dict.TryGetValue("UploadFlag", out var uf) && uf is string ufStr) return ufStr;
        return null;
    }

    private static string ShortenResponse(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";

    public void Dispose()
    {
        // s_http 是静态共享实例，由应用生命周期管理
    }
}
