using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ZL.DataSync;
using ZL.DataSync.Config;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// SyncEngine 的 DI 扩展。
/// </summary>
public static class DataSyncServiceCollectionExtensions
{
    /// <summary>
    /// 注册数据同步引擎。
    /// </summary>
    public static IServiceCollection AddDataSync(
        this IServiceCollection services,
        Action<DataSyncConfig> configure)
    {
        var config = new DataSyncConfig();
        configure(config);
        ValidateConfig(config);
        services.AddSingleton(config);
        services.AddSingleton<SyncEngine>();
        return services;
    }

    /// <summary>
    /// 从 IConfiguration 配置节注册数据同步引擎。
    /// </summary>
    public static IServiceCollection AddDataSyncFromConfig(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "DataSync")
    {
        var section = configuration.GetSection(sectionName);
        if (!section.Exists())
        {
            throw new ArgumentException($"配置节 '{sectionName}' 不存在", nameof(sectionName));
        }

        // 手动从配置绑定
        var config = new DataSyncConfig();

        var localDbPath = section["LocalDbPath"];
        if (!string.IsNullOrWhiteSpace(localDbPath))
            config.LocalDbPath = localDbPath;

        var batchSizeStr = section["BatchSize"];
        if (int.TryParse(batchSizeStr, out var batchSize))
            config.BatchSize = batchSize;

        var intervalStr = section["SyncIntervalSeconds"];
        if (int.TryParse(intervalStr, out var interval))
            config.SyncIntervalSeconds = interval;

        var retryStr = section["MaxRetryCount"];
        if (int.TryParse(retryStr, out var retry))
            config.MaxRetryCount = retry;

        var backoffStr = section["RetryBackoffSeconds"];
        if (int.TryParse(backoffStr, out var backoff))
            config.RetryBackoffSeconds = backoff;

        var upsertStr = section["EnableUpsert"];
        if (bool.TryParse(upsertStr, out var enableUpsert))
            config.EnableUpsert = enableUpsert;

        var cleanupStr = section["EnableCleanup"];
        if (bool.TryParse(cleanupStr, out var enableCleanup))
            config.EnableCleanup = enableCleanup;

        // 解析 RemoteTargets: DataSync:RemoteTargets:0:Name, DataSync:RemoteTargets:0:Type, ...
        var targets = new List<RemoteTargetConfig>();
        for (int i = 0; ; i++)
        {
            var name = section[$"RemoteTargets:{i}:Name"];
            if (string.IsNullOrWhiteSpace(name)) break;

            var target = new RemoteTargetConfig { Name = name };
            var typeStr = section[$"RemoteTargets:{i}:Type"];
            if (Enum.TryParse(typeStr, ignoreCase: true, out TargetType type))
                target.Type = type;

            var connStr = section[$"RemoteTargets:{i}:ConnectionString"];
            if (!string.IsNullOrWhiteSpace(connStr))
                target.ConnectionString = connStr;

            if (target.Type == TargetType.Http)
            {
                var httpEndpoint = section[$"RemoteTargets:{i}:HttpConfig:Endpoint"];
                if (!string.IsNullOrWhiteSpace(httpEndpoint))
                    target.HttpConfig = new HttpUploadConfig { Endpoint = httpEndpoint };

                var httpTimeoutStr = section[$"RemoteTargets:{i}:HttpConfig:TimeoutSeconds"];
                if (int.TryParse(httpTimeoutStr, out var httpTimeout) && target.HttpConfig != null)
                    target.HttpConfig.TimeoutSeconds = httpTimeout;
            }

            targets.Add(target);
        }
        config.RemoteTargets = targets;

        ValidateConfig(config);
        services.AddSingleton(config);
        services.AddSingleton<SyncEngine>();
        return services;
    }

    /// <summary>
    /// 从配置文件路径注册数据同步引擎（JSON 文件）。
    /// </summary>
    public static IServiceCollection AddDataSyncFromJsonFile(
        this IServiceCollection services,
        string jsonFilePath,
        string sectionName = "DataSync")
    {
        string json;
        try
        {
            json = File.ReadAllText(jsonFilePath);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"无法读取配置文件: {jsonFilePath}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"无权访问配置文件: {jsonFilePath}", ex);
        }

        DataSyncConfig? config;
        try
        {
            config = Newtonsoft.Json.JsonConvert.DeserializeObject<DataSyncConfig>(json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"JSON 解析失败 [{jsonFilePath}]: {ex.Message}", ex);
        }

        if (config == null)
            throw new InvalidOperationException($"配置文件为空或格式错误: {jsonFilePath}");

        ValidateConfig(config);
        services.AddSingleton(config);
        services.AddSingleton<SyncEngine>();
        return services;
    }

    /// <summary>
    /// 基本配置校验。
    /// </summary>
    private static void ValidateConfig(DataSyncConfig config)
    {
        if (config.BatchSize <= 0)
            throw new ArgumentException("BatchSize 必须大于 0", nameof(config.BatchSize));

        if (config.SyncIntervalSeconds <= 0)
            throw new ArgumentException("SyncIntervalSeconds 必须大于 0", nameof(config.SyncIntervalSeconds));

        if (config.RemoteTargets.Count > 0)
        {
            foreach (var target in config.RemoteTargets)
            {
                if (string.IsNullOrWhiteSpace(target.Name))
                    throw new ArgumentException("目标 Name 不能为空", nameof(target.Name));

                if (string.IsNullOrWhiteSpace(target.ConnectionString))
                    throw new ArgumentException($"目标 {target.Name} 的 ConnectionString 不能为空", nameof(target.ConnectionString));

                if (target.Type == TargetType.Http && target.HttpConfig == null)
                    throw new ArgumentException($"HTTP 目标 {target.Name} 必须配置 HttpConfig", nameof(target.Name));
            }
        }
    }
}
