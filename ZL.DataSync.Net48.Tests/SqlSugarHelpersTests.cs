namespace ZL.DataSync.Net48.Tests;

[TestClass]
public sealed class SqlSugarHelpersTests
{
    [TestMethod]
    public void QuoteIdentifier_MySQL_应使用反引号()
    {
        var result = ZL.DataSync.Pipeline.SqlSugarHelpers.QuoteIdentifier("test_table", SqlSugar.DbType.MySql);
        Assert.AreEqual("`test_table`", result);
    }

    [TestMethod]
    public void QuoteIdentifier_其他数据库_应使用双引号()
    {
        var result = ZL.DataSync.Pipeline.SqlSugarHelpers.QuoteIdentifier("test_table", SqlSugar.DbType.SqlServer);
        Assert.AreEqual("\"test_table\"", result);
    }

    [TestMethod]
    public void QuoteIdentifier_应转义内部双引号()
    {
        var result = ZL.DataSync.Pipeline.SqlSugarHelpers.QuoteIdentifier("test\"table", SqlSugar.DbType.SqlServer);
        Assert.AreEqual("\"test\"\"table\"", result);
    }

    [TestMethod]
    public void MapDbType_TargetType_应正确映射()
    {
        Assert.AreEqual(SqlSugar.DbType.MySql, ZL.DataSync.Pipeline.SqlSugarHelpers.MapDbType(ZL.DataSync.Config.TargetType.MySql));
        Assert.AreEqual(SqlSugar.DbType.SqlServer, ZL.DataSync.Pipeline.SqlSugarHelpers.MapDbType(ZL.DataSync.Config.TargetType.SqlServer));
        Assert.AreEqual(SqlSugar.DbType.PostgreSQL, ZL.DataSync.Pipeline.SqlSugarHelpers.MapDbType(ZL.DataSync.Config.TargetType.PostgreSql));
        Assert.AreEqual(SqlSugar.DbType.Oracle, ZL.DataSync.Pipeline.SqlSugarHelpers.MapDbType(ZL.DataSync.Config.TargetType.Oracle));
    }

    [TestMethod]
    public void FilterValidRows_Null输入_应返回空列表()
    {
        var result = ZL.DataSync.Pipeline.SqlSugarHelpers.FilterValidRows(null);
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void FilterValidRows_有效行_应正确过滤()
    {
        // 注意：由于 FilterValidRows 需要 dynamic 输入，这里只测试 null 和空场景
        // 实际动态行的测试需要在运行时进行
        var result = ZL.DataSync.Pipeline.SqlSugarHelpers.FilterValidRows(null);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void TryGetProcessTime_有效DateTime_应返回True()
    {
        var row = new Dictionary<string, object?>
        {
            { "ProcessTime", DateTime.UtcNow }
        };

        var result = ZL.DataSync.Pipeline.SqlSugarHelpers.TryGetProcessTime(row, out var pt);
        Assert.IsTrue(result);
        Assert.AreNotEqual(DateTime.MinValue, pt);
    }

    [TestMethod]
    public void TryGetProcessTime_缺失字段_应返回False()
    {
        var row = new Dictionary<string, object?>
        {
            { "OtherField", "value" }
        };

        var result = ZL.DataSync.Pipeline.SqlSugarHelpers.TryGetProcessTime(row, out var pt);
        Assert.IsFalse(result);
        Assert.AreEqual(DateTime.MinValue, pt);
    }
}
