namespace ZL.DataSync.Net48.Tests;

using System;
using ZL.DataSync.Config;
using ZL.DataSync.Pipeline;
using ZL.DataSync.Infrastructure;

[TestClass]
public sealed class DatabaseSyncStrategyTests
{
    private sealed class TestLogger : IStructuredLogger
    {
        public IStructuredLogger ForSource(string source) => this;
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
        public void Debug(string message) { }
        public void Flush() { }
        public void Dispose() { }
    }

    [TestMethod]
    public void Constructor_NullTarget_应抛出ArgumentNullException()
    {
        try
        {
            new DatabaseSyncStrategy(null!, new TestLogger());
            Assert.Fail("应抛出 ArgumentNullException");
        }
        catch (ArgumentNullException)
        {
            // 预期异常
        }
    }

    [TestMethod]
    public void Constructor_NullLogger_应抛出ArgumentNullException()
    {
        var target = new RemoteTargetConfig { ConnectionString = "server=localhost;" };
        try
        {
            new DatabaseSyncStrategy(target, null!);
            Assert.Fail("应抛出 ArgumentNullException");
        }
        catch (ArgumentNullException)
        {
            // 预期异常
        }
    }

    [TestMethod]
    public void TargetName_应正确返回()
    {
        var target = new RemoteTargetConfig { Name = "MyDb", ConnectionString = "server=localhost;" };
        var strategy = new DatabaseSyncStrategy(target, new TestLogger());
        Assert.AreEqual("MyDb", strategy.TargetName);
    }

    [TestMethod]
    public void Dispose_应可安全调用多次()
    {
        var target = new RemoteTargetConfig { Name = "MyDb", ConnectionString = "server=localhost;" };
        var strategy = new DatabaseSyncStrategy(target, new TestLogger());
        strategy.Dispose();
        strategy.Dispose();
    }
}
