#pragma warning disable IDE0063, IDE0130
namespace LSAdapter;

public static class Logger
{

  private static LogLevel s_Level = LogLevel.INFO;
  public static LogLevel Level { get => s_Level; set => s_Level = value; }

  private static StreamWriter s_StreamWriter = new(Console.OpenStandardOutput());
  public static StreamWriter StreamWriter
  {
    get => s_StreamWriter;
    set
    {
      s_StreamWriter = value;
    }
  }


  public static void LogInfo(string format, params object[] objs)
  {
    if (s_Level > LogLevel.INFO)
      return;

    s_StreamWriter.WriteLine($"[{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}] [INFO] " + string.Format(format, objs));
  }

  public static void LogInfo(object msg)
  {
    if (s_Level > LogLevel.INFO)
      return;

    s_StreamWriter.WriteLine($"[{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}] [INFO] {msg}");
  }

  public static void LogError(string format, params object[] objs)
  {
    if (s_Level > LogLevel.ERROR)
      return;

    s_StreamWriter.WriteLine($"[{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}] [ERROR] " + string.Format(format, objs));
  }

  public static void LogError(object msg)
  {
    if (s_Level > LogLevel.ERROR)
      return;

    s_StreamWriter.WriteLine($"[{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}] [ERROR] {msg}");
  }

  public enum LogLevel
  {
    INFO = 0,
    WARNING,
    ERROR,
  }
}
