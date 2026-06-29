namespace ZL.DataSync.Net48.Tests;

using System;
using ZL.DataSync.Config;
using ZL.DataSync.Pipeline;
using ZL.DataSync.Infrastructure;

[TestClass]
public sealed class HttpSyncStrategyTests
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
    public void Constructor_NullConfig_应抛出ArgumentNullException()
    {
        try
        {
            new HttpSyncStrategy(null!, "Target", new TestLogger());
            Assert.Fail("应抛出 ArgumentNullException");
        }
        catch (ArgumentNullException)
        {
            // 预期异常
        }
    }

    [TestMethod]
    public void Constructor_NullTargetName_应抛出ArgumentNullException()
    {
        var config = new HttpUploadConfig { Endpoint = "http://example.com" };
        try
        {
            new HttpSyncStrategy(config, null!, new TestLogger());
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
        var config = new HttpUploadConfig { Endpoint = "http://example.com" };
        try
        {
            new HttpSyncStrategy(config, "Target", null!);
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
        var config = new HttpUploadConfig { Endpoint = "http://example.com" };
        var strategy = new HttpSyncStrategy(config, "MyTarget", new TestLogger());
        Assert.AreEqual("MyTarget", strategy.TargetName);
    }

    [TestMethod]
    public void Dispose_应可安全调用多次()
    {
        var config = new HttpUploadConfig { Endpoint = "http://example.com" };
        var strategy = new HttpSyncStrategy(config, "MyTarget", new TestLogger());
        strategy.Dispose();
        strategy.Dispose();
    }
}
