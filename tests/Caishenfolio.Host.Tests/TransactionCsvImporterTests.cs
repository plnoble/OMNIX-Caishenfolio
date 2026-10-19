using Caishenfolio.Host.Data;
using Caishenfolio.Host.Portfolio;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tests;

public class TransactionCsvImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_csv_tests", Guid.NewGuid().ToString("N"));

    private const string Account = "acct_main";

    private const string Statement = """
        日期,账户,类型,标的,名称,数量,价格,货币,手续费,税费,金额,备注
        2026-01-02,华泰证券,入金,,,,,CNY,,,50000,银证转入
        2026-01-05,华泰证券,买入,SSE:600000,浦发银行,1000,10.05,CNY,5,,,首建仓
        2026-02-09,华泰证券,卖出,SSE:600000,浦发银行,400,12.00,CNY,4,6,,
        2026-03-10,华泰证券,分红,SSE:600000,浦发银行,,,CNY,,20,320,每10股3元
        """;

    [Fact]
    public void ImportsAStatementAndReplaysToTheExpectedPosition()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        var preview = TransactionCsvImporter.Preview(Statement, Account, store);

        Assert.Equal(4, preview.Importable);
        Assert.Equal(0, preview.Invalid);
        Assert.Equal(0, preview.Duplicates);

        Assert.Equal(4, TransactionCsvImporter.Commit(preview, store));

        var state = store.LoadState();
        var position = Assert.Single(state.Positions);
        Assert.Equal(600m, position.Quantity);
        Assert.Equal(10.055m, position.AverageCost.Amount);
        Assert.Equal(300m, position.Dividends.Amount);
        // 50 000 - 10 055 + (4 800 - 4 - 6) + 300
        Assert.Equal(45_035m, Assert.Single(state.CashBalances).Amount);
    }

    [Fact]
    public void ReimportingTheSameFileChangesNothing()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        TransactionCsvImporter.Commit(TransactionCsvImporter.Preview(Statement, Account, store), store);

        var second = TransactionCsvImporter.Preview(Statement, Account, store);

        Assert.Equal(4, second.Duplicates);
        Assert.Equal(0, second.Importable);
        Assert.Equal(0, TransactionCsvImporter.Commit(second, store));
        Assert.Equal(4, store.ListTransactions().Count);
    }

    [Fact]
    public void DetectsDuplicateRowsWithinOneFile()
    {
        const string csv = """
            日期,类型,标的,数量,价格,货币
            2026-01-05,买入,SSE:600000,1000,10.05,CNY
            2026-01-05,买入,SSE:600000,1000,10.05,CNY
            """;

        var preview = TransactionCsvImporter.Preview(csv, Account);

        Assert.Equal(1, preview.Importable);
        Assert.Equal(1, preview.Duplicates);
    }

    [Fact]
    public void BadRowsAreReportedWithLineNumbersAndBlockTheCommit()
    {
        const string csv = """
            日期,类型,标的,数量,价格,货币
            2026-01-05,买入,SSE:600000,1000,10.05,CNY
            2026-01-06,乱七八糟,SSE:600000,100,10,CNY
            not-a-date,买入,SSE:600000,100,10,CNY
            2026-01-08,买入,600000,100,10,CNY
            """;

        using var store = PortfolioStore.UnderStateRoot(_root);
        var preview = TransactionCsvImporter.Preview(csv, Account, store);

        Assert.Equal(1, preview.Importable);
        Assert.Equal(3, preview.Invalid);
        Assert.True(preview.HasErrors);

        var errors = preview.Rows.Where(r => r.Error is not null).ToArray();
        Assert.Equal([3, 4, 5], errors.Select(r => r.LineNumber));
        Assert.Contains("交易类型", errors[0].Error);
        Assert.Contains("日期", errors[1].Error);
        Assert.Contains("交易所:代码", errors[2].Error);

        // Nothing is written until the caller explicitly accepts a partial import.
        Assert.Throws<LedgerException>(() => TransactionCsvImporter.Commit(preview, store));
        Assert.Empty(store.ListTransactions());

        Assert.Equal(1, TransactionCsvImporter.Commit(preview, store, skipInvalidRows: true));
    }

    [Fact]
    public void AcceptsEnglishHeadersTabsAndQuotedFields()
    {
        var csv = "date\taccount\ttype\tsymbol\tqty\tprice\tccy\tfee\tnote\n"
                  + "2026/1/5\tIBKR\tbuy\tNASDAQ:AAPL\t10\t180.25\tUSD\t1\t\"bought, then held\"\n";

        var preview = TransactionCsvImporter.Preview(csv, Account);
        var txn = Assert.Single(preview.Transactions);

        Assert.Equal(TransactionKind.Buy, txn.Kind);
        Assert.Equal(new DateOnly(2026, 1, 5), txn.TradeDate);
        Assert.Equal("NASDAQ:AAPL", txn.Symbol);
        Assert.Equal(180.25m, txn.Price);
        Assert.Equal("USD", txn.Currency);
        Assert.Equal("bought, then held", txn.Note);
        Assert.Equal("IBKR", txn.AccountId);
    }

    [Fact]
    public void MapsAccountNamesToIdsWhenAMapIsProvided()
    {
        var csv = "日期,账户,类型,标的,数量,价格,货币\n2026-01-05,华泰证券,买入,SSE:600000,100,10,CNY\n";
        var map = new Dictionary<string, string> { ["华泰证券"] = "acct_huatai" };

        var txn = Assert.Single(TransactionCsvImporter.Preview(csv, Account, accountNameToId: map).Transactions);

        Assert.Equal("acct_huatai", txn.AccountId);
    }

    [Fact]
    public void InfersCurrencyFromTheVenueWhenTheColumnIsMissing()
    {
        var csv = "日期,类型,标的,数量,价格\n"
                  + "2026-01-05,买入,HKEX:00700,100,320\n"
                  + "2026-01-06,买入,TSE:7203,100,2800\n";

        var transactions = TransactionCsvImporter.Preview(csv, Account).Transactions;

        Assert.Equal("HKD", transactions[0].Currency);
        Assert.Equal("JPY", transactions[1].Currency);
    }

    [Fact]
    public void DerivesUnitPriceWhenTheStatementOnlyHasATotal()
    {
        var csv = "日期,类型,标的,数量,金额,货币\n2026-01-05,买入,SSE:600000,1000,10050,CNY\n";

        var txn = Assert.Single(TransactionCsvImporter.Preview(csv, Account).Transactions);

        Assert.Equal(10.05m, txn.Price);
    }

    [Fact]
    public void NormalizesNegativeAmountsAndThousandSeparators()
    {
        // Statements write outflows as negatives; direction lives in the kind, not the sign.
        var csv = "日期,类型,货币,金额\n2026-01-05,出金,CNY,\"-1,234.56\"\n";

        var txn = Assert.Single(TransactionCsvImporter.Preview(csv, Account).Transactions);

        Assert.Equal(TransactionKind.Withdraw, txn.Kind);
        Assert.Equal(1234.56m, txn.CashAmount);
    }

    [Fact]
    public void ImportOrderDoesNotDependOnWhenTheImportRan()
    {
        var csv = "日期,类型,标的,数量,价格,货币\n"
                  + "2026-02-09,卖出,SSE:600000,400,12,CNY\n"
                  + "2026-01-05,买入,SSE:600000,1000,10,CNY\n";

        var state = PositionCalculator.Replay(TransactionCsvImporter.Preview(csv, Account).Transactions);

        // The sell listed first must still replay after the buy.
        var position = Assert.Single(state.Positions);
        Assert.Equal(600m, position.Quantity);
        Assert.Equal(800m, position.RealizedPnl.Amount);
    }

    [Fact]
    public void RejectsAFileWithoutTheRequiredColumns()
    {
        var preview = TransactionCsvImporter.Preview("foo,bar\n1,2\n", Account);

        Assert.Empty(preview.Rows);
        Assert.Contains(preview.Warnings, w => w.Contains("表头缺少必需列"));
    }

    [Fact]
    public void HandlesEmptyInputAndBomWithoutThrowing()
    {
        Assert.Contains(TransactionCsvImporter.Preview("", Account).Warnings, w => w.Contains("为空"));

        var withBom = "﻿日期,类型,标的,数量,价格,货币\n2026-01-05,买入,SSE:600000,100,10,CNY\n";
        Assert.Single(TransactionCsvImporter.Preview(withBom, Account).Transactions);
    }

    [Fact]
    public void TheShippedTemplateImportsCleanly()
    {
        var preview = TransactionCsvImporter.Preview(TransactionCsvImporter.BuildTemplate(), Account);

        Assert.Equal(0, preview.Invalid);
        // One example row per common kind, including an exchange with its counter leg.
        Assert.Equal(7, preview.Importable);
    }

    [Fact]
    public void ExportedTransactionsCanBeImportedBack()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        TransactionCsvImporter.Commit(TransactionCsvImporter.Preview(Statement, Account, store), store);
        var original = store.ListTransactions();

        var exported = PortfolioReportExporter.TransactionsCsv(original);
        var reimported = TransactionCsvImporter.Preview(exported, Account).Transactions;

        Assert.Equal(original.Count, reimported.Count);
        var before = PositionCalculator.Replay(original).Positions.Single();
        var after = PositionCalculator.Replay(reimported).Positions.Single();
        Assert.Equal(before.Quantity, after.Quantity);
        Assert.Equal(before.CostBasis.Amount, after.CostBasis.Amount);
        Assert.Equal(before.RealizedPnl.Amount, after.RealizedPnl.Amount);
    }


    [Fact]
    public void ExportedExchangeRowsRoundTripWithTheirCounterLeg()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        var exchange = LedgerTransaction.FxExchange(
            Account, new DateOnly(2026, 1, 20), 1000m, "CNY", 155_000m, "JPY", fee: 2m, note: "按回单");
        store.AddTransaction(exchange);

        var csv = PortfolioReportExporter.TransactionsCsv(store.ListTransactions());
        var preview = TransactionCsvImporter.Preview(csv, Account);

        var row = Assert.Single(preview.Rows);
        var rebuilt = row.Transaction;
        Assert.NotNull(rebuilt);
        Assert.Equal(TransactionKind.FxExchange, rebuilt!.Kind);
        Assert.Equal(1000m, rebuilt.CashAmount);
        Assert.Equal("CNY", rebuilt.Currency);
        Assert.Equal("JPY", rebuilt.CounterCurrency);
        Assert.Equal(155_000m, rebuilt.CounterAmount);
        Assert.Equal(2m, rebuilt.Fee);
        Assert.False(row.Duplicate); // not yet in the target book
    }

    [Fact]
    public void ReimportingAnExportOfRecordedRowsIsANoOp()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        store.AddTransactions(
        [
            LedgerTransaction.Deposit(Account, new DateOnly(2026, 1, 2), 50_000m, "CNY", "工资转入"),
            LedgerTransaction.Buy(Account, "SSE:600000", new DateOnly(2026, 1, 5), 1000m, 10.05m, "CNY", 5m),
        ]);

        var csv = PortfolioReportExporter.TransactionsCsv(store.ListTransactions());
        var preview = TransactionCsvImporter.Preview(csv, Account, store);

        // Every exported row already lives in the book (matched by content, not by id).
        Assert.Equal(0, preview.Importable);
        Assert.Equal(2, preview.Duplicates);
        Assert.Equal(0, preview.Invalid);
    }

    [Fact]
    public void AReversalPairCannotBeResurrectedThroughImport()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        var buy = LedgerTransaction.Buy(Account, "SSE:600000", new DateOnly(2026, 1, 5), 100m, 10m, "CNY", 5m);
        store.AddTransaction(buy);
        store.AddTransaction(LedgerTransaction.Reversal(buy, "撤单"));

        var csv = PortfolioReportExporter.TransactionsCsv(store.ListTransactions());
        var preview = TransactionCsvImporter.Preview(csv, Account, store);

        // The original row is refused because it is reversed; the mirror row is refused by kind.
        Assert.Equal(0, preview.Importable);
        Assert.Equal(2, preview.Invalid);
        Assert.Contains(preview.Rows, r => r.Error is not null && r.Error.Contains("已冲正"));
        Assert.Contains(preview.Rows, r => r.Error is not null && r.Error.Contains("冲正记录不能通过 CSV 导入"));
    }


    [Fact]
    public void ACrossAccountExchangeRoundTripsThroughCsvWithBothBalancesIntact()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        var bank = store.SaveAccount(Host.Portfolio.Account.Create("银行", AccountKind.Bank, "CNY"));
        var broker = store.SaveAccount(Host.Portfolio.Account.Create("券商", AccountKind.Securities, "USD"));

        // Bank sells 7 000 CNY; the USD lands in the broker account.
        store.AddTransaction(LedgerTransaction.FxExchange(
            bank.Id, new DateOnly(2026, 1, 5), 7_000m, "CNY", 1_000m, "USD",
            counterAccountId: broker.Id));

        var csv = PortfolioReportExporter.TransactionsCsv(
            store.ListTransactions(),
            store.ListAccounts(includeArchived: true).ToDictionary(a => a.Id, a => a, StringComparer.Ordinal));
        var preview = TransactionCsvImporter.Preview(
            csv, bank.Id, store, "CNY",
            store.ListAccounts(includeArchived: true).ToDictionary(a => a.Name, a => a.Id, StringComparer.Ordinal));

        // The exported row must not import as a duplicate of itself (same economic content).
        Assert.Equal(0, preview.Invalid);
        var row = Assert.Single(preview.Rows);
        Assert.True(row.Duplicate);

        // And a FRESH store importing the same file must land both legs in the right accounts.
        using var freshRoot = PortfolioStore.UnderStateRoot(
            Path.Combine(_root, "fresh", Guid.NewGuid().ToString("N")));
        var freshBank = freshRoot.SaveAccount(Host.Portfolio.Account.Create("银行", AccountKind.Bank, "CNY"));
        var freshBroker = freshRoot.SaveAccount(Host.Portfolio.Account.Create("券商", AccountKind.Securities, "USD"));
        var freshPreview = TransactionCsvImporter.Preview(
            csv, freshBank.Id, freshRoot, "CNY",
            freshRoot.ListAccounts(includeArchived: true).ToDictionary(a => a.Name, a => a.Id, StringComparer.Ordinal));
        TransactionCsvImporter.Commit(freshPreview, freshRoot);

        var state = freshRoot.LoadState();
        // Both legs land in the right accounts — the counter account survived the round trip.
        var bankCash = state.CashBalances.Single(b => b.AccountId == freshBank.Id);
        Assert.Equal(-7_000m, bankCash.Amount);
        Assert.Equal("CNY", bankCash.Currency);
        var brokerCash = state.CashBalances.Single(b => b.AccountId == freshBroker.Id);
        Assert.Equal(1_000m, brokerCash.Amount);
        Assert.Equal("USD", brokerCash.Currency);
    }

    /// <summary>
    /// WP-01 export completeness: the two new kinds leave the book under their own names, with the
    /// destination account spelled out, and they are refused on import with a reason — never
    /// silently re-imported as some other kind, which would quietly change what the ledger says.
    /// </summary>
    [Fact]
    public void TheNewKindsExportUnderTheirOwnNamesAndAreRefusedOnImportWithAReason()
    {
        using var store = PortfolioStore.UnderStateRoot(_root);
        var source = store.SaveAccount(Host.Portfolio.Account.Create("银行", AccountKind.Bank, "CNY"));
        var destination = store.SaveAccount(Host.Portfolio.Account.Create("券商", AccountKind.Bank, "CNY"));
        var balance = store.SaveAccount(
            Host.Portfolio.Account.Create("券商总览", AccountKind.Securities, "CNY", mode: AccountValuationMode.Balance));

        var transfer = LedgerTransaction.Transfer(
            source.Id, destination.Id, new DateOnly(2025, 6, 20), 30_000m, "CNY", "搬钱");
        var assertion = LedgerTransaction.BalanceAssertion(
            balance.Id, new DateOnly(2025, 6, 30), 195_000m, "CNY", "月底总览");
        store.AddTransactions([transfer, assertion]);
        store.AddTransaction(LedgerTransaction.Reversal(assertion, "金额记错"));

        var accounts = store.ListAccounts(includeArchived: true).ToDictionary(a => a.Id, a => a, StringComparer.Ordinal);
        var csv = PortfolioReportExporter.TransactionsCsv(store.ListTransactions(), accounts);

        Assert.Contains("账户间转移", csv, StringComparison.Ordinal);
        Assert.Contains("当天资产金额", csv, StringComparison.Ordinal);
        Assert.Contains("冲正·当天资产金额", csv, StringComparison.Ordinal);
        Assert.Contains("券商", csv, StringComparison.Ordinal); // the destination is named, not an id
        Assert.Contains("30000", csv, StringComparison.Ordinal);

        // The transfer row carries one amount and no counter currency: it must not read as two.
        var transferRow = csv.Split('\n')
            .Single(line => line.Contains("账户间转移", StringComparison.Ordinal))
            .Split(',');
        Assert.Equal("30000", transferRow[10]);
        Assert.Equal("", transferRow[11]);
        Assert.Equal("", transferRow[12]);
        Assert.Equal("券商", transferRow[13]);

        var preview = TransactionCsvImporter.Preview(csv, source.Id, store, "CNY",
            accounts.Values.ToDictionary(a => a.Name, a => a.Id, StringComparer.Ordinal));
        Assert.Equal(0, preview.Importable);
        Assert.All(
            preview.Rows.Where(r => r.Transaction is null),
            r => Assert.False(string.IsNullOrWhiteSpace(r.Error)));
        Assert.Contains(preview.Rows, r => r.Error is not null && r.Error.Contains("账户间转移", StringComparison.Ordinal));
        Assert.Contains(preview.Rows, r => r.Error is not null && r.Error.Contains("当天资产金额", StringComparison.Ordinal));
        Assert.Contains(preview.Rows, r => r.Error is not null && r.Error.Contains("冲正记录不能通过 CSV 导入", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
