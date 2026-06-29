namespace ZL.DataSync.Net48.Tests;

[TestClass]
public sealed class DataSyncConfigTests
{
    [TestMethod]
    public void DataSyncConfig_默认值_应包含空目标列表()
    {
        var config = new ZL.DataSync.Config.DataSyncConfig();

        Assert.AreEqual(string.Empty, config.LocalDbPath);
        Assert.AreEqual(100, config.BatchSize);
        Assert.AreEqual(5, config.SyncIntervalSeconds);
        Assert.AreEqual(3, config.MaxRetryCount);
        Assert.AreEqual(2, config.RetryBackoffSeconds);
        Assert.IsTrue(config.EnableUpsert);
        Assert.IsTrue(config.EnableCleanup);
        Assert.AreEqual(730, config.DataRetentionDays);
        Assert.AreEqual(3600, config.CleanupIntervalSeconds);
        Assert.IsNotNull(config.RemoteTargets);
        Assert.AreEqual(0, config.RemoteTargets.Count);
    }

    [TestMethod]
    public void RemoteTargetConfig_Http配置_应能正确设置()
    {
        var target = new ZL.DataSync.Config.RemoteTargetConfig
        {
            Name = "TestHttp",
            Type = ZL.DataSync.Config.TargetType.Http,
            ConnectionString = "http://example.com",
            HttpConfig = new ZL.DataSync.Config.HttpUploadConfig
            {
                Endpoint = "http://example.com/api",
                DeviceName = "Device1",
                Type = "1",
                TimeoutSeconds = 30
            }
        };

        Assert.AreEqual("TestHttp", target.Name);
        Assert.AreEqual(ZL.DataSync.Config.TargetType.Http, target.Type);
        Assert.IsNotNull(target.HttpConfig);
        Assert.AreEqual("http://example.com/api", target.HttpConfig.Endpoint);
        Assert.AreEqual("Device1", target.HttpConfig.DeviceName);
        Assert.AreEqual(30, target.HttpConfig.TimeoutSeconds);
    }

    [TestMethod]
    public void RemoteTargetConfig_数据库目标_应能正确设置表映射()
    {
        var target = new ZL.DataSync.Config.RemoteTargetConfig
        {
            Name = "TestMySql",
            Type = ZL.DataSync.Config.TargetType.MySql,
            ConnectionString = "server=localhost;database=test;",
            TableMappings = new Dictionary<string, string>
            {
                { "local_table", "remote_table" }
            }
        };

        Assert.AreEqual("TestMySql", target.Name);
        Assert.AreEqual(ZL.DataSync.Config.TargetType.MySql, target.Type);
        Assert.IsNotNull(target.TableMappings);
        Assert.AreEqual(1, target.TableMappings.Count);
        Assert.IsTrue(target.TableMappings.ContainsKey("local_table"));
        Assert.AreEqual("remote_table", target.TableMappings["local_table"]);
    }
}
