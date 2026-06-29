using System.Data;
using SqlSugar;
using ZL.DataSync.Infrastructure;

namespace ZL.DataSync.Pipeline;

/// <summary>
/// SqlSugar 共享工具方法。被所有同步策略复用，减少重复代码。
/// </summary>
internal static class SqlSugarHelpers
{
    internal const string SyncColumn = "_Synced";
    internal const string SyncTimeColumn = "_SyncTime";
    internal const string ProcessTimeColumn = "ProcessTime";
    internal const string IdColumn = "Id";

    /// <summary>根据数据库方言添加引号（MySQL 用反引号，其他用双引号）</summary>
    public static string QuoteIdentifier(string name, SqlSugar.DbType dbType)
    {
        // 防御 SQL 注入：转义引号字符
        var safeName = name.Replace("\"", "\"\"");
        return dbType == SqlSugar.DbType.MySql ? $"`{safeName}`" : $"\"{safeName}\"";
    }

    /// <summary>TargetType → SqlSugar.DbType 映射</summary>
    public static SqlSugar.DbType MapDbType(Config.TargetType type) => type switch
    {
        Config.TargetType.MySql => SqlSugar.DbType.MySql,
        Config.TargetType.SqlServer => SqlSugar.DbType.SqlServer,
        Config.TargetType.PostgreSql => SqlSugar.DbType.PostgreSQL,
        Config.TargetType.Oracle => SqlSugar.DbType.Oracle,
        _ => SqlSugar.DbType.MySql
    };

    /// <summary>从行中提取 ProcessTime 字段</summary>
    public static bool TryGetProcessTime(Dictionary<string, object?> row, out DateTime pt)
    {
        pt = DateTime.MinValue;
        if (row.TryGetValue("ProcessTime", out var val) && val is DateTime d && d > DateTime.MinValue)
        {
            pt = d;
            return true;
        }
        return false;
    }

    /// <summary>将 SqlSugar dynamic 对象转换为 Dictionary（返回可为 null）</summary>
    public static Dictionary<string, object?>? ConvertToDictionary(dynamic row)
    {
        var obj = row as object;
        if (obj is IDictionary<string, object?> dict)
            return new Dictionary<string, object?>(dict);
        return null;
    }

    /// <summary>
    /// 过滤有效行（非 null 且 Count &gt; 0）。
    /// 被所有同步策略复用，消除重复代码。
    /// </summary>
    /// <param name="dynamicRows">SqlSugar 返回的 dynamic 结果集</param>
    /// <returns>过滤后的行列表</returns>
    public static List<Dictionary<string, object?>> FilterValidRows(dynamic? dynamicRows)
    {
        if (dynamicRows == null) return new List<Dictionary<string, object?>>();

        var rows = new List<Dictionary<string, object?>>();
        foreach (var row in dynamicRows)
        {
            var dict = ConvertToDictionary(row);
            if (dict != null && dict.Count > 0)
                rows.Add(dict);
        }
        return rows;
    }

    /// <summary>
    /// 从 rows 中提取 [start, end) 范围内有效行。
    /// 被所有同步策略复用，消除重复代码。
    /// </summary>
    /// <param name="rows">原始行列表</param>
    /// <param name="start">起始索引（包含）</param>
    /// <param name="end">结束索引（不包含）</param>
    /// <returns>有效行列表</returns>
    public static List<Dictionary<string, object?>> ExtractValidRows(List<Dictionary<string, object?>> rows, int start, int end)
    {
        var batch = new List<Dictionary<string, object?>>(end - start);
        for (int j = start; j < end; j++)
        {
            var row = rows[j];
            if (row != null && row.Count > 0)
                batch.Add(row);
        }
        return batch;
    }

    /// <summary>收集所有行中的列名（跳过 _ 开头的内部字段）</summary>
    public static List<string> CollectColumnList(List<Dictionary<string, object?>> rows)
    {
        var colList = new List<string>();
        var colSet = new HashSet<string>();
        foreach (var row in rows)
        {
            foreach (var key in row.Keys)
            {
                if (!key.StartsWith("_") && colSet.Add(key))
                    colList.Add(key);
            }
        }
        return colList;
    }

    /// <summary>构建批量 INSERT SQL（含占位符和参数）</summary>
    public static (string Sql, List<SugarParameter> Parameters) BuildBatchInsertSql(
        string targetTable,
        SqlSugar.DbType remoteDbType,
        List<Dictionary<string, object?>> rows)
    {
        var colList = CollectColumnList(rows);

        var paramCount = 0;
        var rowTemplates = new List<string>();
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var placeholders = new List<string>();
            for (int c = 0; c < colList.Count; c++)
            {
                if (row.TryGetValue(colList[c], out var value) && value != null)
                {
                    placeholders.Add($"@p{paramCount}");
                    paramCount++;
                }
                else
                {
                    placeholders.Add("NULL");
                }
            }
            rowTemplates.Add($"({string.Join(",", placeholders)})");
        }

        var colQuoted = string.Join(", ", colList.Select(c => QuoteIdentifier(c, remoteDbType)));
        var sql = $"INSERT INTO {QuoteIdentifier(targetTable, remoteDbType)} ({colQuoted}) VALUES {string.Join(", ", rowTemplates)}";

        var parameters = new List<SugarParameter>();
        foreach (var row in rows)
        {
            foreach (var col in colList)
            {
                if (row.TryGetValue(col, out var value) && value != null)
                    parameters.Add(new SugarParameter($"p{parameters.Count}", value));
            }
        }

        return (sql, parameters);
    }

    /// <summary>构建单行 INSERT SQL（含参数）</summary>
    public static (string Sql, List<SugarParameter> Parameters) BuildSingleInsertSql(
        string targetTable,
        SqlSugar.DbType remoteDbType,
        Dictionary<string, object?> row)
    {
        var columns = row.Keys.Where(k => !k.StartsWith("_")).ToList();
        if (columns.Count == 0)
            return (string.Empty, new List<SugarParameter>());

        var paramNames = new List<string>();
        var parameters = new List<SugarParameter>();
        foreach (var col in columns)
        {
            var name = $"p{parameters.Count}";
            paramNames.Add($"@{name}");
            parameters.Add(new SugarParameter(name, row[col] ?? DBNull.Value));
        }

        var sql = $"INSERT INTO {QuoteIdentifier(targetTable, remoteDbType)} ({string.Join(", ", columns.Select(c => QuoteIdentifier(c, remoteDbType)))}) VALUES ({string.Join(",", paramNames)})";
        return (sql, parameters);
    }

    /// <summary>根据 CLR 类型推断 SQL 类型（MySQL bool → TINYINT(1)）</summary>
    public static string AdaptSqlType(SqlSugar.DbType remoteDbType, Type? clrType, object? sampleValue)
    {
        if (clrType == null || sampleValue == null || sampleValue == DBNull.Value)
            return "TEXT";

        return clrType switch
        {
            Type t when t == typeof(int) || t == typeof(short) || t == typeof(long) => "BIGINT",
            Type t when t == typeof(float) || t == typeof(double) || t == typeof(decimal) => "DOUBLE",
            Type t when t == typeof(bool) => remoteDbType == SqlSugar.DbType.MySql ? "TINYINT(1)" : "BIT",
            Type t when t == typeof(DateTime) || t == typeof(DateTimeOffset) => "DATETIME",
            Type t when t == typeof(byte[]) => "BLOB",
            _ => "TEXT"
        };
    }

    /// <summary>
    /// 批量标记本地记录为已同步。优先按 Id 批量标记，失败则退化为按 ProcessTime 标记。
    /// 被 DatabaseSyncStrategy 和 HttpSyncStrategy 复用。
    /// </summary>
    /// <param name="localDb">本地 SQLite 连接</param>
    /// <param name="tableName">本地表名</param>
    /// <param name="sqliteDbType">本地数据库类型</param>
    /// <param name="rows">待标记的行</param>
    /// <param name="ct">取消令牌</param>
    public static async Task BatchMarkSyncedAsync(
        SqlSugarClient localDb,
        string tableName,
        SqlSugar.DbType sqliteDbType,
        List<Dictionary<string, object?>> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return;

        // 收集 Id（优先方式）和 ProcessTime（回退方式）
        var ids = new List<object?>();
        var processTimeById = new Dictionary<object, DateTime>();

        foreach (var r in rows)
        {
            if (r.TryGetValue(IdColumn, out var id) && id != null)
            {
                ids.Add(id);
            }

            if (r.TryGetValue(ProcessTimeColumn, out var ptVal) && ptVal is DateTime dt && dt > DateTime.MinValue)
            {
                // 用第一个遇到的 Id 绑定该 ProcessTime；如果同一个 Id 有多个值，取最后一个
                var boundId = r.TryGetValue(IdColumn, out var bId) && bId != null ? bId : null;
                if (boundId != null)
                {
                    processTimeById[boundId] = dt;
                }
            }
        }

        // 优先按 Id 批量标记（分批，防止参数过多）
        if (ids.Count > 0)
        {
            try
            {
                const int MaxBatchSize = 500;
                for (int i = 0; i < ids.Count; i += MaxBatchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = ids.Skip(i).Take(MaxBatchSize).ToList();
                    var (sql, parameters) = BuildMarkSyncedSql(tableName, sqliteDbType, batch, null);
                    await localDb.Ado.ExecuteCommandAsync(sql, parameters.ToArray()).ConfigureAwait(false);
                }
                return;
            }
            catch
            {
                // 退化为按 ProcessTime + Id 组合标记
            }
        }

        // Id 缺失时回退：每条记录按 (Id OR ProcessTime) 分别标记
        // 对没有 Id 的记录，按 ProcessTime 标记（可能误标记同时间的其他行，这是已知的已知风险）
        var rowsWithoutId = rows
            .Where(r => !r.ContainsKey(IdColumn) || r[IdColumn] == null)
            .ToList();

        if (processTimeById.Count > 0)
        {
            // 有 Id + ProcessTime 绑定的记录：逐条按 Id 标记
            foreach (var kvp in processTimeById)
            {
                ct.ThrowIfCancellationRequested();
                var (sql, parameters) = BuildMarkSyncedSql(tableName, sqliteDbType, new List<object?> { kvp.Key }, null);
                await localDb.Ado.ExecuteCommandAsync(sql, parameters.ToArray()).ConfigureAwait(false);
            }
        }

        if (rowsWithoutId.Count > 0)
        {
            // 完全无 Id 的记录：回退到按 ProcessTime 标记
            var ptTimes = rowsWithoutId
                .Select(r => r.TryGetValue(ProcessTimeColumn, out var v) && v is DateTime dt && dt > DateTime.MinValue ? (DateTime?)dt : null)
                .Where(t => t.HasValue)
                .Distinct()
                .Select(t => t!.Value)
                .ToList();

            if (ptTimes.Count > 0)
            {
                var (sql, parameters) = BuildMarkSyncedSql(tableName, sqliteDbType, null, ptTimes);
                await localDb.Ado.ExecuteCommandAsync(sql, parameters.ToArray()).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 构建标记同步成功的 SQL。
    /// </summary>
    /// <param name="tableName">本地表名</param>
    /// <param name="dbType">本地数据库类型</param>
    /// <param name="ids">按主键 Id 标记（优先方式）</param>
    /// <param name="processTimes">按 ProcessTime 标记（Id 缺失时回退）</param>
    /// <returns>SQL 语句和参数列表</returns>
    internal static (string Sql, List<SugarParameter> Parameters) BuildMarkSyncedSql(
        string tableName,
        SqlSugar.DbType dbType,
        List<object?>? ids,
        List<DateTime>? processTimes)
    {
        var paramNames = new List<string>();
        var parameters = new List<SugarParameter>();

        if (ids != null && ids.Count > 0)
        {
            foreach (var id in ids)
            {
                var name = $"id{parameters.Count}";
                paramNames.Add($"@{name}");
                parameters.Add(new SugarParameter(name, id));
            }
            var sql = $"UPDATE {QuoteIdentifier(tableName, dbType)} SET {SyncColumn} = 1, {SyncTimeColumn} = @now WHERE Id IN ({string.Join(",", paramNames)})";
            parameters.Add(new SugarParameter("now", DateTime.UtcNow));
            return (sql, parameters);
        }

        if (processTimes != null && processTimes.Count > 0)
        {
            foreach (var pt in processTimes)
            {
                var name = $"pt{parameters.Count}";
                paramNames.Add($"@{name}");
                parameters.Add(new SugarParameter(name, pt));
            }
            var sql = $"UPDATE {QuoteIdentifier(tableName, dbType)} SET {SyncColumn} = 1, {SyncTimeColumn} = @now WHERE {ProcessTimeColumn} IN ({string.Join(",", paramNames)})";
            parameters.Add(new SugarParameter("now", DateTime.UtcNow));
            return (sql, parameters);
        }

        return (string.Empty, new List<SugarParameter>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  自动建表
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 基于样本数据构建 CREATE TABLE SQL。
    /// 调用方负责加锁和 IsAnyTable 检查。
    ///
    /// 设计决策：
    /// - 跳过 _ 开头的列（约定为内部字段，如 _Synced、_ProcessTime）
    /// - 根据 CLR 类型推断 SQL 类型（bool → TINYINT(1) for MySQL）
    /// </summary>
    /// <param name="targetTable">远程表名</param>
    /// <param name="dbType">目标数据库类型</param>
    /// <param name="sampleRow">样本行（用于推断列类型）</param>
    /// <param name="targetName">目标名称（用于日志）</param>
    /// <param name="logger">日志接口</param>
    /// <returns>CREATE TABLE SQL 语句</returns>
    public static string BuildCreateTableSql(
        string targetTable,
        SqlSugar.DbType dbType,
        Dictionary<string, object?> sampleRow,
        string targetName,
        IStructuredLogger logger)
    {
        var cols = new List<string>();
        foreach (var kvp in sampleRow)
        {
            if (kvp.Key.StartsWith("_")) continue; // 跳过内部字段
            string sqlType = AdaptSqlType(dbType, kvp.Value?.GetType(), kvp.Value);
            cols.Add($"{QuoteIdentifier(kvp.Key, dbType)} {sqlType}");
        }

        if (cols.Count == 0)
        {
            logger.Warning($"[{targetName}] 表 {targetTable} 没有有效列，跳过建表");
            return string.Empty;
        }

        var sql = $"CREATE TABLE {QuoteIdentifier(targetTable, dbType)} ({string.Join(", ", cols)})";

        if (dbType == SqlSugar.DbType.MySql)
            sql += " ENGINE=InnoDB DEFAULT CHARSET=utf8mb4";

        return sql;
    }
}
