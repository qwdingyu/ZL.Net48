namespace ZL.DataSync.Net48.Tests;

[TestClass]
public sealed class SyncEngineTests
{
    private const string TestDbPath = "test_sync_engine.db";

    [TestInitialize]
    public void Setup()
    {
        // 清理可能存在的测试数据库
        if (File.Exists(TestDbPath))
            File.Delete(TestDbPath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        // 清理测试数据库
        if (File.Exists(TestDbPath))
            File.Delete(TestDbPath);
    }

    [TestMethod]
    public void SyncEngine_创建并启动_应能正常启动和停止()
    {
        // .NET Framework 4.8 + System.Data.SQLite 依赖 native SQLite.Interop.dll，
        // 在非 Windows 平台（macOS/Linux）上无法加载，仅限 Windows 环境运行。
        if (!Environment.OSVersion.Platform.ToString().Contains("Win"))
        {
            Assert.Inconclusive("SQLite 依赖 Windows 原生库，当前平台跳过");
            return;
        }

        var config = new ZL.DataSync.Config.DataSyncConfig
        {
            LocalDbPath = TestDbPath,
            BatchSize = 10,
            SyncIntervalSeconds = 1,
            RemoteTargets = new List<ZL.DataSync.Config.RemoteTargetConfig>()
        };

        using var engine = new ZL.DataSync.SyncEngine(config);
        engine.Start();

        Assert.AreEqual("运行中", engine.Status.StatusText);

        Thread.Sleep(500);

        var stopTask = engine.StopAsync();
        var timeoutTask = Task.Delay(5000);
        var completed = Task.WhenAny(stopTask, timeoutTask).Result;

        Assert.IsTrue(completed == stopTask, "StopAsync 应该在超时前完成");
        Assert.IsFalse(engine.Status.IsRunning);
    }

    [TestMethod]
    public void SyncEngine_无远程目标_应正常启动但不报错()
    {
        // .NET Framework 4.8 + System.Data.SQLite 依赖 native SQLite.Interop.dll，
        // 在非 Windows 平台（macOS/Linux）上无法加载，仅限 Windows 环境运行。
        if (!Environment.OSVersion.Platform.ToString().Contains("Win"))
        {
            Assert.Inconclusive("SQLite 依赖 Windows 原生库，当前平台跳过");
            return;
        }

        var config = new ZL.DataSync.Config.DataSyncConfig
        {
            LocalDbPath = TestDbPath,
            BatchSize = 10,
            SyncIntervalSeconds = 1,
            RemoteTargets = new List<ZL.DataSync.Config.RemoteTargetConfig>()
        };

        using var engine = new ZL.DataSync.SyncEngine(config);
        engine.Start();

        Assert.AreEqual(0, engine.Status.TotalTables);

        var stopTask = engine.StopAsync();
        var timeoutTask = Task.Delay(5000);
        var completed = Task.WhenAny(stopTask, timeoutTask).Result;

        Assert.IsTrue(completed == stopTask);
        Assert.IsFalse(engine.Status.IsRunning);
    }

    [TestMethod]
    public void SyncEngine_空配置_应抛出异常()
    {
        try
        {
            new ZL.DataSync.SyncEngine(null!);
            Assert.Fail("应抛出 ArgumentNullException");
        }
        catch (ArgumentNullException)
        {
            // 预期异常
        }
        catch (AggregateException agg)
        {
            // MSTest 可能将异常包装为 AggregateException
            if (agg.InnerException is ArgumentNullException)
                return;
            Assert.Fail($"预期 ArgumentNullException，实际: {agg.InnerException?.GetType().Name ?? "null"}");
        }
    }

    [TestMethod]
    public void SyncEngine_空LocalDbPath_应抛出异常()
    {
        var config = new ZL.DataSync.Config.DataSyncConfig
        {
            LocalDbPath = string.Empty
        };

        try
        {
            new ZL.DataSync.SyncEngine(config);
            Assert.Fail("应抛出 ArgumentException");
        }
        catch (ArgumentException)
        {
            // 预期异常
        }
        catch (AggregateException agg)
        {
            // MSTest 可能将异常包装为 AggregateException
            if (agg.InnerException is ArgumentException)
                return;
            Assert.Fail($"预期 ArgumentException，实际: {agg.InnerException?.GetType().Name ?? "null"}");
        }
    }
}
