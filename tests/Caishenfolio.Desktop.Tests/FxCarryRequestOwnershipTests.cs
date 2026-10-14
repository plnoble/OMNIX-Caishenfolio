using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Caishenfolio.Desktop.Wealth;
using Caishenfolio.Host.Python;

namespace Caishenfolio.Desktop.Tests;

/// <summary>
/// R7-02 long-term regression: every carry response — success, failure, late, out-of-order —
/// belongs to the request that issued it. The tests drive the real awaitable core
/// (FxCarryView.LoadCarryAsync) on an unshown control with a queued in-memory HTTP handler:
/// response data only carries markers, no network, no sleeps, no "wait long enough" — an old
/// request is awaited to a real end before asserting the newer one's state survived.
/// </summary>
public class FxCarryRequestOwnershipTests
{
    private const string MillionColumnHeader = "每 100 万年化利差（按当前利率）";

    [Fact]
    public void SequentialRangeChangesKeepRowsHeaderHelpAndStatusOnOneRange()
    {
        WpfTestHost.Run(async () =>
        {
            var setup = NewBoundView();
            SelectYears(setup.View, 1);

            foreach (var years in new[] { 1, 5, 10, 1 })
            {
                SelectYears(setup.View, years);
                var task = setup.View.LoadCarryAsync();
                setup.Handler.Complete(setup.Handler.Requests.Count - 1, years, years + 1);
                await task;

                Assert.Equal($"{years} 年高低", setup.Header.Text);
                var row = setup.Rows();
                Assert.NotNull(row);
                Assert.Equal($"{years} ~ {years + 1}", row!.RangeText);
                Assert.Contains($"区间 {years} 年", setup.Status.Text);
                Assert.Contains($"所选 {years} 年区间", setup.Help.Text);
                Assert.DoesNotContain("过去一年", setup.Help.Text);
            }

            Assert.Equal(new[] { 365, 1825, 3650, 365 }, setup.Handler.Requests.Select(DaysOf).ToArray());
            Assert.Equal(MillionColumnHeader, setup.MillionHeader());
        });
    }

    [Fact]
    public void FailureAfterASuccessKeepsTheLastSuccessfulRange()
    {
        WpfTestHost.Run(async () =>
        {
            var setup = NewBoundView();

            var ok = setup.View.LoadCarryAsync();
            setup.Handler.Complete(0, 1, 2);
            await ok;
            Assert.Contains("区间 1 年", setup.Status.Text);

            SelectYears(setup.View, 5);
            var failed = setup.View.LoadCarryAsync();
            setup.Handler.Fail(1);
            await failed;

            Assert.Contains("仍显示上次成功的 1 年结果", setup.Status.Text);
            Assert.Equal("1 年高低", setup.Header.Text); // not the requested 5 years
            Assert.Equal("1 ~ 2", setup.Rows()!.RangeText);
            Assert.Contains("所选 1 年区间", setup.Help.Text);
            Assert.Equal(1825, DaysOf(setup.Handler.Requests[1]));
        });
    }

    [Fact]
    public void FirstRequestFailureNeverClaimsAPreviousSuccess()
    {
        WpfTestHost.Run(async () =>
        {
            var setup = NewBoundView();

            var task = setup.View.LoadCarryAsync();
            setup.Handler.Fail(0);
            await task;

            Assert.Contains("尚无成功结果", setup.Status.Text);
            Assert.DoesNotContain("上次成功", setup.Status.Text);
            Assert.DoesNotContain("1 年结果", setup.Status.Text);
            Assert.Null(setup.Rows()); // no rows pretending a success happened
            Assert.Equal("1 年高低", setup.Header.Text);
        });
    }

    [Fact]
    public void LateOldSuccessDoesNotOverrideTheNewerSelection()
    {
        WpfTestHost.Run(async () =>
        {
            var setup = NewBoundView();

            // Recheck-8 probe trap, now deterministic: the old 5-year request is awaited to a
            // REAL end before asserting the 10-year state survived — no timeout guessing.
            SelectYears(setup.View, 5);
            var slowFive = setup.View.LoadCarryAsync();
            SelectYears(setup.View, 10);
            var fastTen = setup.View.LoadCarryAsync();

            setup.Handler.Complete(1, 10, 20);
            await fastTen;
            Assert.Contains("区间 10 年", setup.Status.Text);

            setup.Handler.Complete(0, 5, 6);
            await slowFive; // old request truly finished here

            Assert.Equal("10 年高低", setup.Header.Text);
            Assert.Equal("10 ~ 20", setup.Rows()!.RangeText);
            Assert.Contains("区间 10 年", setup.Status.Text);
            Assert.Contains("所选 10 年区间", setup.Help.Text);
            Assert.Equal(new[] { 1825, 3650 }, setup.Handler.Requests.Select(DaysOf).ToArray());
        });
    }

    [Fact]
    public void LateOldFailureDoesNotOverrideTheNewerResult()
    {
        WpfTestHost.Run(async () =>
        {
            var setup = NewBoundView();

            SelectYears(setup.View, 5);
            var slowFive = setup.View.LoadCarryAsync();
            SelectYears(setup.View, 10);
            var fastTen = setup.View.LoadCarryAsync();

            setup.Handler.Complete(1, 10, 20);
            await fastTen;

            setup.Handler.Fail(0);
            await slowFive; // old failure truly finished; must not touch the new state

            Assert.Equal("10 年高低", setup.Header.Text);
            Assert.Equal("10 ~ 20", setup.Rows()!.RangeText);
            Assert.Contains("区间 10 年", setup.Status.Text);
            Assert.DoesNotContain("查询失败", setup.Status.Text);
            Assert.Contains("所选 10 年区间", setup.Help.Text);
        });
    }

    [Fact]
    public void FinishedOldRequestDoesNotReleaseTheBusyWindowOfTheRunningRequest()
    {
        WpfTestHost.Run(async () =>
        {
            var setup = NewBoundView();
            var loadButton = (Button)setup.View.FindName("LoadButton");
            var combo = (ComboBox)setup.View.FindName("FxRangeCombo");

            var old = setup.View.LoadCarryAsync();
            SelectYears(setup.View, 10);
            var current = setup.View.LoadCarryAsync();
            Assert.False(loadButton.IsEnabled);

            setup.Handler.Complete(0, 5, 6);
            await old; // old finally ran; the current request is still in flight
            Assert.False(loadButton.IsEnabled, "old finally must not re-enable while the newer request runs");
            Assert.False(combo.IsEnabled, "old finally must not re-open range selection mid-request");

            setup.Handler.Complete(1, 10, 20);
            await current;
            Assert.True(loadButton.IsEnabled);
            Assert.True(combo.IsEnabled);
            Assert.Equal("10 ~ 20", setup.Rows()!.RangeText);
        });
    }

    [Fact]
    public void AnnualizedColumnHeaderIsConstantAcrossInitialNoCoreAndEveryRange()
    {
        WpfTestHost.Run(async () =>
        {
            var handler = new QueuedCarryHandler();
            using var http = new HttpClient(handler);
            var client = new AnalyticsCoreClient(new Uri("http://127.0.0.1:1/"), http);
            var view = new FxCarryView();
            var status = (TextBlock)view.FindName("StatusText");
            var help = (TextBlock)view.FindName("FxPercentileHelp");
            var explanation = (TextBox)view.FindName("FxExplanationText");
            string Million() => ((DataGridTextColumn)view.FindName("FxCarryMillionColumn")).Header.ToString()!;

            // Initial: before any request the money column already speaks the current-rate unit.
            Assert.Equal(MillionColumnHeader, Million());
            Assert.DoesNotContain("过去一年", help.Text);
            Assert.DoesNotContain("过去一年", explanation.Text);

            // Core not ready: an explicit stop, and the column/headers stay consistent.
            view.Bind(() => (AnalyticsCoreClient?)null, () => "CNY");
            await view.LoadCarryAsync();
            Assert.Contains("尚未就绪", status.Text);
            Assert.Equal(MillionColumnHeader, Million());

            view.Bind(() => client, () => "CNY");
            foreach (var years in new[] { 5, 10 })
            {
                SelectYears(view, years);
                var task = view.LoadCarryAsync();
                handler.Complete(handler.Requests.Count - 1, years, years + 1);
                await task;
                Assert.Equal(MillionColumnHeader, Million());
                Assert.Contains($"所选 {years} 年区间", help.Text);
                Assert.DoesNotContain("过去一年", help.Text);
                Assert.DoesNotContain("过去一年", explanation.Text);
            }
        });
    }

    private static CarryTestSetup NewBoundView()
    {
        var handler = new QueuedCarryHandler();
        var http = new HttpClient(handler);
        var client = new AnalyticsCoreClient(new Uri("http://127.0.0.1:1/"), http);
        var view = new FxCarryView();
        view.Bind(() => client, () => "CNY");
        return new CarryTestSetup(handler, view);
    }

    private sealed record CarryTestSetup(QueuedCarryHandler Handler, FxCarryView View)
    {
        public TextBlock Status => (TextBlock)View.FindName("StatusText");
        public TextBlock Header => (TextBlock)View.FindName("FxRangeHeader");
        public TextBlock Help => (TextBlock)View.FindName("FxPercentileHelp");
        public CarryRow? Rows() => ((DataGrid)View.FindName("LegGrid")).Items.Cast<CarryRow>().FirstOrDefault();
        public string MillionHeader() => ((DataGridTextColumn)View.FindName("FxCarryMillionColumn")).Header.ToString()!;
    }

    private static void SelectYears(FxCarryView view, int years)
    {
        // The control is never shown, so SelectionChanged's IsLoaded guard makes this a pure
        // input to the next request — mirroring the page rule that loading disables the combo.
        var combo = (ComboBox)view.FindName("FxRangeCombo");
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>()
            .Single(item => item.Tag?.ToString() == years.ToString());
    }

    private static int DaysOf(string pathAndQuery)
    {
        var query = pathAndQuery[(pathAndQuery.IndexOf('?') + 1)..];
        var days = query.Split('&').Single(p => p.StartsWith("days=", StringComparison.Ordinal));
        return int.Parse(days["days=".Length..], CultureInfo.InvariantCulture);
    }

    /// <summary>Queued responses: requests park on a completion source the test resolves, so
    /// completion ORDER (not timing) is the experiment. Data values are range markers only.</summary>
    private sealed class QueuedCarryHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        private readonly List<TaskCompletionSource<HttpResponseMessage>> _pending = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(pending);
            return pending.Task;
        }

        public void Complete(int index, decimal low, decimal high) =>
            _pending[index].SetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new FxCarryResponse
                {
                    Ok = true,
                    BaseCurrency = "CNY",
                    Disclaimer = "fixture-only test data",
                    Legs =
                    [
                        new CarryLegDto
                        {
                            Pair = "CNY/USD",
                            Rate = low,
                            Low = low,
                            High = high,
                            Percentile = 50m,
                            Carry = 0.02m,
                        },
                    ],
                })),
            });

        public void Fail(int index) =>
            _pending[index].SetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
