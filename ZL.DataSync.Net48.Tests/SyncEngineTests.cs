namespace ZL.DataSync.Net48.Tests;

[TestClass]
public sealed class SyncEngineTests
{
    private const string TestDbPath = "test_sync_engine.db";
    private const string DiagPath = "test_diagnostic.log";

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

    private static void Log(string msg)
    {
        File.AppendAllText(DiagPath, $"{DateTime.UtcNow:O} {msg}{Environment.NewLine}");
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
            Log("TEST START SyncEngine_无远程目标_应正常启动但不报错");
            engine = new ZL.DataSync.SyncEngine(config);
            engine.Start();

            Assert.AreEqual(0, engine.Status.TotalTables);

            var stopTask = engine.StopAsync();
            var timeoutTask = Task.Delay(5000);
            var completed = Task.WhenAny(stopTask, timeoutTask).Result;

            Assert.IsTrue(completed == stopTask);
            Assert.IsFalse(engine.Status.IsRunning);
            Log("TEST PASS: no exception");
        }
        catch (Exception e)
        {
            ex = e;
            Log($"CAUGHT: {e.GetType().FullName}: {e.Message}");
            Log(e.StackTrace ?? "");
            if (e is AggregateException agg1)
            {
                Log($"INNER: {agg1.InnerException?.GetType().FullName}: {agg1.InnerException?.Message}");
                Log(agg1.InnerException?.StackTrace ?? "");
            }
        }
        finally { engine?.Dispose(); }

        if (ex == null) return;

        Assert.Fail($"测试异常: {ex.GetType().Name}: {ex.Message}");
    }

    [TestMethod]
    public void SyncEngine_空配置_应抛出异常()
    {
        Log("TEST START SyncEngine_空配置_应抛出异常");
        Exception? ex = null;
        try
        {
            new ZL.DataSync.SyncEngine(null!);
            Assert.Fail("应抛出 ArgumentNullException");
        }
        catch (Exception e)
        {
            ex = e;
            Log($"CAUGHT: {e.GetType().FullName}: {e.Message}");
            Log(e.StackTrace ?? "");
            if (e is AggregateException agg2)
            {
                Log($"INNER: {agg2.InnerException?.GetType().FullName}: {agg2.InnerException?.Message}");
                Log(agg2.InnerException?.StackTrace ?? "");
            }
        }

        if (ex == null)
        {
            Log("NO EXCEPTION CAUGHT");
            Assert.Fail("未捕获到异常");
        }

        // MSTest 可能将异常包装为 AggregateException
        if (ex is ArgumentNullException)
        {
            Log("PASS: ArgumentNullException");
            return;
        }
        if (ex is AggregateException agg3 && agg3.InnerException is ArgumentNullException)
        {
            Log("PASS: AggregateException wrapping ArgumentNullException");
            return;
        }

        Log($"FAIL: expected ArgumentNullException, got {ex.GetType().FullName}");
        Assert.Fail($"预期 ArgumentNullException，实际: {ex.GetType().FullName}: {ex.Message}");
    }

    [TestMethod]
    public void SyncEngine_空LocalDbPath_应抛出异常()
    {
        Log("TEST START SyncEngine_空LocalDbPath_应抛出异常");
        var config = new ZL.DataSync.Config.DataSyncConfig
        {
            LocalDbPath = string.Empty
        };

        Exception? ex = null;
        try
        {
            new ZL.DataSync.SyncEngine(config);
            Assert.Fail("应抛出 ArgumentException");
        }
        catch (Exception e)
        {
            ex = e;
            Log($"CAUGHT: {e.GetType().FullName}: {e.Message}");
            Log(e.StackTrace ?? "");
            if (e is AggregateException agg4)
            {
                Log($"INNER: {agg4.InnerException?.GetType().FullName}: {agg4.InnerException?.Message}");
                Log(agg4.InnerException?.StackTrace ?? "");
            }
        }

        if (ex == null)
        {
            Log("NO EXCEPTION CAUGHT");
            Assert.Fail("未捕获到异常");
        }

        // MSTest 可能将异常包装为 AggregateException
        if (ex is ArgumentException)
        {
            Log("PASS: ArgumentException");
            return;
        }
        if (ex is AggregateException agg5 && agg5.InnerException is ArgumentException)
        {
            Log("PASS: AggregateException wrapping ArgumentException");
            return;
        }

        Log($"FAIL: expected ArgumentException, got {ex.GetType().FullName}");
        Assert.Fail($"预期 ArgumentException，实际: {ex.GetType().FullName}: {ex.Message}");
    }
}
