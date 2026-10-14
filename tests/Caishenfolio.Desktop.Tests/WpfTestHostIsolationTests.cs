using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Caishenfolio.Desktop.Tests;

public class WpfTestHostIsolationTests
{
    [Fact]
    public void ResourceHostNeverStartsTheDesktopAfterPumpingStartup()
    {
        WpfTestHost.Run(async () =>
        {
            // Application's queued startup has higher priority. Do not assert before
            // it gets a chance to run: that would preserve the original false safety.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var app = Application.Current;
            Assert.NotNull(app);
            Assert.IsType<Application>(app);
            Assert.Null(app.StartupUri);
            Assert.Null(app.MainWindow);
            Assert.Empty(app.Windows.Cast<Window>());
            Assert.IsType<SolidColorBrush>(app.FindResource("BrushAccent"));
        });
    }
}
