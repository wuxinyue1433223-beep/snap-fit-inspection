using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;

namespace ProgrammerShell.App.Services;

public sealed class FileLoggerProvider : ILoggerProvider
{
 private readonly string _logPath;
 private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);

 public FileLoggerProvider(string logPath)
 {
 _logPath = logPath;
 var dir = Path.GetDirectoryName(_logPath);
 if (!string.IsNullOrWhiteSpace(dir))
 Directory.CreateDirectory(dir);
 }

 public ILogger CreateLogger(string categoryName)
 {
 return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _logPath));
 }

 public void Dispose() => _loggers.Clear();

 public static string GetDefaultLogPath()
 {
 var root = FindWorkspaceRoot();
 if (string.IsNullOrWhiteSpace(root))
 root = AppContext.BaseDirectory;

 return Path.Combine(root, "ProgrammerShell.log");
 }

 private static string? FindWorkspaceRoot()
 {
 var dir = new DirectoryInfo(AppContext.BaseDirectory);
 for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
 {
 var srcDir = Path.Combine(dir.FullName, "src");
 if (Directory.Exists(srcDir))
 return dir.FullName;
 }

 return null;
 }

 private sealed class FileLogger : ILogger
 {
 private static readonly object _gate = new();
 private readonly string _category;
 private readonly string _path;

 public FileLogger(string category, string path)
 {
 _category = category;
 _path = path;
 }

 public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
 public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

 public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
 Func<TState, Exception?, string> formatter)
 {
 if (!IsEnabled(logLevel)) return;
 var message = formatter(state, exception);
 if (string.IsNullOrWhiteSpace(message) && exception is null) return;

 var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {logLevel} {_category}: {message}";
 if (exception is not null)
 line += Environment.NewLine + exception;

 lock (_gate)
 {
 File.AppendAllText(_path, line + Environment.NewLine);
 }
 }
 }

 private sealed class NullScope : IDisposable
 {
 public static NullScope Instance { get; } = new();
 public void Dispose() { }
 }
}
