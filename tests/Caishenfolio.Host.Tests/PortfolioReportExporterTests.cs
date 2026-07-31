using Caishenfolio.Host;
using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Tests;

public class PortfolioReportExporterTests
{
    private static readonly DateOnly Day1 = new(2026, 1, 5);
    private static readonly DateOnly AsOf = new(2026, 7, 31);

    private static PortfolioValuation Valuation(bool withJapanQuote = true)
    {
        var ledger = PositionCalculator.Replay(
        [
            LedgerTransaction.OpeningCash("acct", Day1, 5_000m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "SSE:600000", Day1, 1000m, 10m, "CNY"),
            LedgerTransaction.OpeningPosition("acct", "TSE:7203", Day1, 100m, 2800m, "JPY"),
        ]);

        var quotes = new Dictionary<string, PriceQuote>
        {
            ["SSE:600000"] = PriceQuote.Of("SSE:600000", 12m, "CNY", AsOf, "fixture"),
        };
        if (withJapanQuote)
        {
            quotes["TSE:7203"] = PriceQuote.Of("TSE:7203", 3000m, "JPY", AsOf, "fixture");
        }

        var fx = new FxConverter([FxRate.Of("USD", "CNY", 7.2m, Day1), FxRate.Of("USD", "JPY", 150m, Day1)]);
        return ValuationEngine.Value(ledger, quotes, fx, "CNY", AsOf);
    }

    [Fact]
    public void PositionsCsvCarriesValuesAndTheDisclaimer()
    {
        var csv = PortfolioReportExporter.PositionsCsv(Valuation());
        var rows = DelimitedText.Parse(csv);

        Assert.Contains("账户", rows[0]);
        Assert.Contains(rows, r => r.Length > 1 && r[1] == "SSE:600000");
        Assert.Contains(rows, r => r.Length > 0 && r[0] == "总资产");
        Assert.Contains(rows, r => r.Length > 0 && r[0] == ProductInfo.ResearchDisclaimer);
    }

    [Fact]
    public void UnpricedHoldingExportsEmptyCellsNotZeros()
    {
        var csv = PortfolioReportExporter.PositionsCsv(Valuation(withJapanQuote: false));
        var rows = DelimitedText.Parse(csv);

        var toyota = rows.Single(r => r.Length > 1 && r[1] == "TSE:7203");

        // Market-value columns are blank so a spreadsheet SUM cannot swallow the gap as zero.
        Assert.Equal("", toyota[10]);
        Assert.Equal("", toyota[11]);
        Assert.Equal("缺价格", toyota[^1]);
        Assert.Contains(rows, r => r.Length > 0 && r[0].Contains("估值不完整"));
    }

    [Fact]
    public void AllocationCsvCoversEveryDimension()
    {
        var csv = PortfolioReportExporter.AllocationCsv(Valuation());
        var rows = DelimitedText.Parse(csv);

        var dimensions = rows.Skip(1).Where(r => r.Length > 1).Select(r => r[0]).ToHashSet();
        Assert.Equal(new[] { "品种", "市场", "货币", "账户" }.ToHashSet(), dimensions);
        Assert.Contains(rows, r => r.Length > 1 && r[1] == "日股");
        Assert.Contains(rows, r => r.Length > 3 && r[3].EndsWith('%'));
    }

    [Fact]
    public void FieldsWithCommasSurviveTheRoundTrip()
    {
        var text = DelimitedText.Write([["a,b", "plain", "say \"hi\"", "line\nbreak"]]);
        var parsed = DelimitedText.Parse(text);

        Assert.Equal(["a,b", "plain", "say \"hi\"", "line\nbreak"], Assert.Single(parsed));
    }
}
