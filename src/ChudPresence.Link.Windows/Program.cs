using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ChudPresence.Link.Windows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "Local\\ChudPresence.Link", out var firstInstance);
        if (!firstInstance)
        {
            ShowMessage("ChudPresence Link is already running. Open it from the system tray.");
            return;
        }
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(initialization =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App(args);
            });
        }
        catch (Exception error)
        {
            ShowMessage($"ChudPresence Link could not start.\n\n{error.Message}\n\nIf the address is in use, close the other copy or choose another port with --port.", true);
        }
    }

    internal static void ShowMessage(string message, bool error = false) =>
        MessageBox(0, message, "ChudPresence Link", error ? 0x10u : 0x40u);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint owner, string text, string caption, uint type);
}
