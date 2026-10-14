using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;

namespace Caishenfolio.Desktop.Tests;

/// <summary>
/// One STA thread owns a resource-only Application and pumps its dispatcher
/// for the whole test run: Application can only be created once per AppDomain and WPF
/// elements are thread-affine, so every WPF test marshals its body here instead of building
/// its own app. Tests run serially through a gate. No MainWindow, no window shown, no
/// PortfolioViewModel and therefore never the real ledger — the same "unshown control"
/// boundary the round-6/7/8 probes used, now as a regular test entry point.
/// </summary>
public static class WpfTestHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TaskCompletionSource<Dispatcher> Ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    static WpfTestHost()
    {
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                // Application queues startup even without Run(). Never construct the
                // production App or load its StartupUri / OnStartup in a unit test.
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                using var source = typeof(WpfTestHost).Assembly.GetManifestResourceStream("DesktopApp.xaml")
                    ?? throw new InvalidOperationException("Production App.xaml resource is missing.");
                var root = XDocument.Load(source).Root!;
                var resources = root.Element(root.Name.Namespace + "Application.Resources")
                    ?? throw new InvalidOperationException("Production Application.Resources is missing.");
                // Embed the current production file at build time; do not maintain a
                // second theme. Only its resources enter the test Application.
                var dictionary = new XElement(root.Name.Namespace + "ResourceDictionary",
                    root.Attributes().Where(a => a.IsNamespaceDeclaration), resources.Elements());
                using var reader = dictionary.CreateReader();
                app.Resources = (ResourceDictionary)XamlReader.Load(reader);
                Ready.SetResult(Dispatcher.CurrentDispatcher);
            }
            catch (Exception ex)
            {
                Ready.TrySetException(ex);
                return;
            }
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }


    /// <summary>
    /// Runs the async body on the host dispatcher and blocks the caller until it settles.
    /// Awaits inside the body capture the dispatcher context, so view continuations
    /// (ConfigureAwait(true)) run on this thread before the body's await completes —
    /// tests are deterministic, with no sleeps and no dispatcher pumping.
    /// </summary>
    public static void Run(Func<Task> body)
    {
        Gate.Wait();
        try
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatcher = Ready.Task.GetAwaiter().GetResult();
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body().ConfigureAwait(true);
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            });
            done.Task.GetAwaiter().GetResult();
        }
        finally
        {
            Gate.Release();
        }
    }
}
