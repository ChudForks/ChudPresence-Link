using System.Runtime.InteropServices;
using ChudPresence.Link.Windows;

internal static class LaunchOptionsChecks
{
    public static void Run(Action<bool, string> check)
    {
        var directory = Path.GetFullPath("test data");
        var defaults = LaunchOptions.Parse([], directory, 17879);
        check(defaults == new LaunchOptions(directory, 17879, false), "launch arguments preserve defaults");
        var options = LaunchOptions.Parse(["--minimized", "--port", "17880", "--data-dir", directory], "unused", 17879);
        check(options == new LaunchOptions(directory, 17880, true), "launch arguments preserve custom port, data path, and minimized mode");
        foreach (var invalid in new string[][]
        {
            ["--data-dir"], ["--data-dir", "--minimized"], ["--data-dir", " "],
            ["--port"], ["--port", "1023"], ["--port", "65536"], ["--port", "--minimized"], ["--unknown"]
        })
        {
            var rejected = false;
            try { LaunchOptions.Parse(invalid, directory, 17879); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "invalid launch arguments are rejected: " + string.Join(" ", invalid));
        }

        if (!OperatingSystem.IsWindows()) return;
        var executable = @"C:\Program Files\ChudPresence Link\ChudPresence.Link.exe";
        foreach (var dataDirectory in new[] { @"D:\", @"D:\Link test\", @"D:\Link test", @"\\server\share\" })
        {
            var command = new LaunchOptions(dataDirectory, 17880, true).StartupCommand(executable);
            var parsed = SplitWindowsCommandLine(command);
            var restored = LaunchOptions.Parse(parsed[1..], "unused", 17879);
            check(parsed[0] == executable && restored == new LaunchOptions(dataDirectory, 17880, true),
                "Windows startup command round-trips data directory: " + dataDirectory);
        }
        var quoted = "embedded \"quote\" and slash\\";
        check(SplitWindowsCommandLine(LaunchOptions.QuoteArgument(executable) + " " + LaunchOptions.QuoteArgument(quoted))[1] == quoted,
            "Windows command arguments preserve embedded quotes and trailing backslashes");
    }

    private static string[] SplitWindowsCommandLine(string command)
    {
        var argv = CommandLineToArgv(command, out var count);
        if (argv == 0) throw new InvalidOperationException("Windows could not parse the startup command.");
        try
        {
            return Enumerable.Range(0, count).Select(index =>
                Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))!).ToArray();
        }
        finally { LocalFree(argv); }
    }

    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode)]
    private static extern nint CommandLineToArgv(string command, out int count);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
