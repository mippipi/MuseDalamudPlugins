namespace FfxivUploader.Core;

/// <summary>uploader-app.log への1行ログ。PS 版 uploader.log と同じ書式。</summary>
public sealed class AppLog
{
    private readonly string logPath;
    private readonly object gate = new();
    private readonly Queue<string> recent = new();
    private const long RotateBytes = 5 * 1024 * 1024;

    public AppLog(string logPath)
    {
        this.logPath = logPath;
    }

    public event Action<string>? LineWritten;

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (gate)
        {
            recent.Enqueue(line);
            while (recent.Count > 200) recent.Dequeue();
            try
            {
                RotateIfNeeded();
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch { /* ログ失敗で本体を止めない */ }
        }
        LineWritten?.Invoke(line);
    }

    public IReadOnlyList<string> RecentLines
    {
        get { lock (gate) return recent.ToArray(); }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(logPath);
        if (info.Exists && info.Length > RotateBytes)
        {
            var old = logPath + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(logPath, old);
        }
    }
}
