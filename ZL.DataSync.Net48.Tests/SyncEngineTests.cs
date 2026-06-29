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

        ZL.DataSync.SyncEngine? engine = null;
        Exception? ex = null;
        try
        {
            engine = new ZL.DataSync.SyncEngine(config);
            engine.Start();

            Assert.AreEqual(0, engine.Status.TotalTables);

            var stopTask = engine.StopAsync();
            var timeoutTask = Task.Delay(5000);
            var completed = Task.WhenAny(stopTask, timeoutTask).Result;

            Assert.IsTrue(completed == stopTask);
            Assert.IsFalse(engine.Status.IsRunning);
        }
        catch (Exception e) { ex = e; }
        finally { engine?.Dispose(); }

        if (ex == null) return;

        Console.WriteLine($"TEST FAILED: {ex.GetType().FullName}: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
        if (ex is AggregateException agg)
        {
            Console.WriteLine($"INNER: {agg.InnerException?.GetType().FullName}: {agg.InnerException?.Message}");
            Console.WriteLine(agg.InnerException?.StackTrace);
        }

        Assert.Fail($"测试异常: {ex.GetType().Name}: {ex.Message}");
    }

    [TestMethod]
    public void SyncEngine_空配置_应抛出异常()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new ZL.DataSync.SyncEngine(null!));
    }

    [TestMethod]
    public void SyncEngine_空LocalDbPath_应抛出异常()
    {
        var config = new ZL.DataSync.Config.DataSyncConfig
        {
            LocalDbPath = string.Empty
        };

        Assert.ThrowsException<ArgumentException>(() => new ZL.DataSync.SyncEngine(config));
    }
}
