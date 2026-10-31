using System.Globalization;
using System.Text;

namespace ChudPresence.Link.Windows;

internal sealed record LaunchOptions(string DataDirectory, int Port, bool Minimized)
{
    public static LaunchOptions Parse(string[] arguments, string defaultDataDirectory, int defaultPort)
    {
        var directory = defaultDataDirectory;
        var port = defaultPort;
        var minimized = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--minimized":
                    minimized = true;
                    break;
                case "--data-dir":
                    if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]) || arguments[index].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("Use --data-dir followed by a folder path.");
                    directory = Path.GetFullPath(arguments[index]);
                    break;
                case "--port":
                    if (++index >= arguments.Length || !int.TryParse(arguments[index], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1024 or > 65535)
                        throw new ArgumentException("Use --port followed by a number between 1024 and 65535.");
                    break;
                default:
                    throw new ArgumentException("Unknown argument: " + arguments[index]);
            }
        }
        return new(directory, port, minimized);
    }

    public string StartupCommand(string executable) =>
        $"{QuoteArgument(executable)} --minimized --port {Port.ToString(CultureInfo.InvariantCulture)} --data-dir {QuoteArgument(DataDirectory)}";

    // Windows argv parsing treats backslashes before a closing quote as escapes.
    // Double them (and those before literal quotes) to preserve directory roots.
    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
