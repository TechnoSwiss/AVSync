using System.Runtime.InteropServices;
using System.Text.Json;

namespace AVSync;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var options = Options.Parse(args);
        timeBeginPeriod(1); // 1 ms timer/sleep resolution for the render loop and network threads
        try
        {
            Application.Run(new MainForm(options));
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
}

/// <summary>
/// Command line:
///   --source         always run as the source
///   --sync [host]    always run as a sync app (optionally connecting straight to host instead of discovering)
///   --port N         UDP port (default 47800)
///   --fullscreen     start full screen
/// </summary>
public sealed class Options
{
    public bool ForceSource { get; private set; }
    public bool ForceSync { get; private set; }
    public string? SyncHost { get; private set; }
    public int Port { get; private set; } = Protocol.DefaultPort;
    public bool FullScreen { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant().TrimStart('-', '/'))
            {
                case "source": o.ForceSource = true; break;
                case "sync":
                    o.ForceSync = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) o.SyncHost = args[++i];
                    break;
                case "port" when i + 1 < args.Length && int.TryParse(args[i + 1], out int port):
                    o.Port = port;
                    i++;
                    break;
                case "fullscreen": o.FullScreen = true; break;
            }
        }
        return o;
    }
}

/// <summary>Per-user settings persisted in %LOCALAPPDATA%\AVSync\settings.json.</summary>
public sealed class Settings
{
    public double AudioOffsetMs { get; set; }
    public bool ShowInfo { get; set; } = true;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVSync", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
