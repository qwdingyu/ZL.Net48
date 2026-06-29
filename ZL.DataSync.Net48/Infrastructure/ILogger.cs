namespace ZL.DataSync.Infrastructure;

/// <summary>
/// 调试用 Logger（不依赖外部库），用于开发阶段快速验证。
/// </summary>
internal sealed class DebugLogger : IStructuredLogger
{
    public DebugLogger(string? source = null) { }
    public IStructuredLogger ForSource(string source) => this;

    public void Info(string message) => System.Diagnostics.Debug.WriteLine($"[INFO] {message}");
    public void Warning(string message) => System.Diagnostics.Debug.WriteLine($"[WARN] {message}");
    public void Error(string message) => System.Diagnostics.Debug.WriteLine($"[ERROR] {message}");
    public void Debug(string message) => System.Diagnostics.Debug.WriteLine($"[DEBUG] {message}");

    public void Flush() { }
    public void Dispose() { }
}

/// <summary>
/// 结构化日志接口。
/// </summary>
public interface IStructuredLogger
{
    IStructuredLogger ForSource(string source);
    void Info(string message);
    void Warning(string message);
    void Error(string message);
    void Debug(string message);
    void Flush();
    void Dispose();
}
