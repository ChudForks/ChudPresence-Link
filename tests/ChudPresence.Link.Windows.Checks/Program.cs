using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class Program
{
    private static int Main(string[] args)
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        try
        {
            var rootOption = Array.IndexOf(args, "--source-root");
            var root = rootOption >= 0 && rootOption + 1 < args.Length
                ? Path.GetFullPath(args[rootOption + 1])
                : FindSourceRoot();
            var source = Path.Combine(root, "src", "ChudPresence.Link.Windows");
            var project = XDocument.Load(Path.Combine(source, "ChudPresence.Link.Windows.csproj"));
            Check(project.Descendants("UseWinUI").Any(e => e.Value == "true") &&
                !project.Descendants("UseWindowsForms").Any(e => e.Value == "true"),
                "desktop project uses native WinUI without Windows Forms");

            var files = Directory.EnumerateFiles(source, "*.xaml", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(source, path).Split(Path.DirectorySeparatorChar)
                    .Any(part => part is "obj" or "bin")).ToArray();
            Check(files.Any(path => Path.GetFileName(path) == "LinkWindow.xaml"), "primary window is implemented in XAML");
            var documents = files.ToDictionary(path => path, path => XDocument.Load(path, LoadOptions.SetLineInfo));
            Check(documents.Values.All(document => document.Root is not null), "all application XAML is well formed");

            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            foreach (var (path, document) in documents)
            {
                foreach (var dictionary in document.Descendants().Where(element => element.Name.LocalName == "ResourceDictionary"))
                {
                    var dictionarySource = dictionary.Attribute("Source")?.Value;
                    if (dictionarySource is null || Uri.TryCreate(dictionarySource, UriKind.Absolute, out _)) continue;
                    var relative = dictionarySource.Replace('/', Path.DirectorySeparatorChar);
                    Check(File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, relative))),
                        Path.GetFileName(path) + " resource dictionary exists: " + dictionarySource);
                }

                if (document.Root?.Attribute(x + "Class") is not { } className) continue;
                var codePath = new[] { path + ".cs", Path.ChangeExtension(path, ".cs") }.FirstOrDefault(File.Exists);
                Check(codePath is not null, Path.GetFileName(path) + " has its code behind");
                var code = File.ReadAllText(codePath!);
                var shortName = className.Value.Split('.').Last();
                Check(Regex.IsMatch(code, @"\bpartial\s+class\s+" + Regex.Escape(shortName) + @"\b"),
                    Path.GetFileName(path) + " class matches its code behind");

                string[] events = ["Click", "Toggled", "SelectionChanged", "Checked", "Unchecked", "Loaded",
                    "SizeChanged", "ActualThemeChanged", "ItemInvoked", "Tapped", "KeyDown", "PrimaryButtonClick"];
                foreach (var attribute in document.Descendants().Attributes().Where(attribute => events.Contains(attribute.Name.LocalName)))
                {
                    Check(Regex.IsMatch(code, @"\b" + Regex.Escape(attribute.Value) + @"\s*\("),
                        Path.GetFileName(path) + " connects " + attribute.Name.LocalName + " to " + attribute.Value);
                }
            }

            SettingsUiChecks.Run(Check);
            LaunchOptionsChecks.Run(Check);
            Console.WriteLine($"All {checks} WinUI source and selection checks passed. Native window rendering is checked by launching the app.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static string FindSourceRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "ChudPresence.Link.slnx"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Run from the repository or pass --source-root followed by the repository directory.");
    }
}
