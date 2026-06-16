using System.Text;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.SponsorBlock;

/// <summary>
/// Writes SponsorBlock log entries to a dedicated file in Jellyfin's log directory
/// so they appear as a separate log in the dashboard.
/// </summary>
public sealed class SponsorBlockLog : IDisposable
{
	private readonly string _logDir;
	private readonly TimeProvider _time;
	private readonly SemaphoreSlim _lock = new(1, 1);
	private readonly Dictionary<string, StreamWriter> _writers = new();
	private string? _currentDate;

	/// <summary>
	/// Initializes the log with the Jellyfin log directory.
	/// </summary>
	/// <param name="paths">Application paths (used to find the log directory).</param>
	/// <param name="time">Time provider.</param>
	public SponsorBlockLog(IApplicationPaths paths, TimeProvider time)
	{
		_logDir = Path.Combine(paths.PluginConfigurationsPath, "..", "..", "log");
		_time = time;
		Directory.CreateDirectory(_logDir);
	}

	internal SponsorBlockLog(string logDir, TimeProvider time)
	{
		_logDir = logDir;
		_time = time;
		Directory.CreateDirectory(_logDir);
	}

	/// <summary>
	/// Writes an information-level log entry.
	/// </summary>
	public void Information(string message)
	{
		Write("INF", message);
	}

	/// <summary>
	/// Writes a debug-level log entry.
	/// </summary>
	public void Debug(string message)
	{
		Write("DBG", message);
	}

	/// <summary>
	/// Writes a warning-level log entry.
	/// </summary>
	public void Warning(string message)
	{
		Write("WRN", message);
	}

	/// <summary>
	/// Writes an error-level log entry.
	/// </summary>
	public void Error(string message)
	{
		Write("ERR", message);
	}

	private void Write(string level, string message)
	{
		var now = _time.GetLocalNow();
		var timestamp = now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
		var line = $"[{timestamp}] [{level}] {message}";

		_lock.Wait();
		try
		{
			var date = now.ToString("yyyyMMdd");
			if (date != _currentDate)
			{
				_currentDate = date;
			}

			if (!_writers.TryGetValue(date, out var writer))
			{
				var path = Path.Combine(_logDir, $"log_SponsorBlock_{date}.log");
				var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
				writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
				_writers[date] = writer;
			}

			writer.WriteLine(line);
		}
		finally
		{
			_lock.Release();
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		foreach (var writer in _writers.Values)
		{
			writer.Dispose();
		}

		_writers.Clear();
		_lock.Dispose();
	}
}
