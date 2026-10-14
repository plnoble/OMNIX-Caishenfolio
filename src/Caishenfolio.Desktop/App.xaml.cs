using System.IO;
using System.Windows;
using Caishenfolio.Host.MarketData;
using Caishenfolio.Host.Notifications;
using Caishenfolio.Host.Portfolio;
using Caishenfolio.Host.Python;

namespace Caishenfolio.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>Started by the scheduled task; runs the deadline check and exits.</summary>
    private const string NotifyFlag = "--notify";

    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Args.Any(arg => string.Equals(arg, NotifyFlag, StringComparison.OrdinalIgnoreCase)))
        {
            // No base.OnStartup and no window: StartupUri would otherwise open the whole app
            // behind the user's back every time the timer fired.
            RunHeadlessNotify();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }

    private static void RunHeadlessNotify()
    {
        var stateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Caishenfolio",
            "state");

        try
        {
            var store = PortfolioStore.UnderStateRoot(stateRoot);
            var settings = new NotificationSettingsStore(store).Load();

            // Layer 2 runs only when the user switched it on. Its failures are reported as
            // limitations inside the result, never as an exception past the deadline layer.
            Func<System.Threading.CancellationToken, Task<IReadOnlyList<PortfolioAlert>>>? extended = null;
            if (settings.BackgroundExtendedChecks)
            {
                extended = ct => BackgroundExtendedCheck.RunAsync(
                    store,
                    FindCoreRoot(),
                    stateRoot,
                    new MarketCredentialsStore(stateRoot),
                    fxRules: settings.FxAlertRules,
                    cancellationToken: ct);
            }

            var stateStore = new AlertNotificationStateStore(
                Path.Combine(stateRoot, "alert-state.json"));

            // Blocking rather than async void: the process must not exit while the HTTP posts
            // are still in flight.
            var result = HeadlessNotifier
                .RunAsync(store, settings, extendedCheck: extended, stateStore: stateStore)
                .GetAwaiter()
                .GetResult();

            HeadlessNotifier.AppendLog(HeadlessNotifier.DefaultLogPath(), result);
        }
        catch (Exception ex)
        {
            // A crash here is invisible — no console, no window — so it goes to the same log
            // the successful runs use, where the user can actually find it.
            TryLogFailure(ex);
        }
    }

    /// <summary>Walks up from the exe to find the repo's <c>python/</c> directory.</summary>
    private static string? FindCoreRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "python")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static void TryLogFailure(Exception ex)
    {
        try
        {
            var path = HeadlessNotifier.DefaultLogPath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  后台检查失败：{ex.Message}{Environment.NewLine}");
        }
        catch (Exception nested) when (nested is IOException or UnauthorizedAccessException)
        {
            // Nothing further can be done without a console to report to.
        }
    }
}
