using System.Windows;
using System.Windows.Controls;
using Caishenfolio.Desktop.Wealth;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Desktop.Tests;

/// <summary>
/// R7-01 long-term regression: field parsing is selected by the CURRENT transaction type, so
/// stale text left over from a previous type can neither block the new one nor sneak in.
/// The tests drive the real BuildTransaction on an unshown LedgerView (the state the
/// production type-switch handler leaves behind: old text kept, boxes disabled) and assert
/// the constructed transaction's actual kind, quantities, amounts and currencies — not just
/// "no exception". No PortfolioViewModel, therefore never the real ledger.
/// </summary>
public class LedgerEntryTypeBoundaryTests
{
    [Fact]
    public void DepositToBuyIgnoresStaleAmountButStillRejectsRequiredTradeFields()
    {
        WpfTestHost.Run(async () =>
        {
            await Task.CompletedTask;
            var view = NewLedger();

            // Type a broken amount while the type is Deposit, then switch to Buy: the
            // production handler leaves the old text in the (now disabled) amount box.
            SelectKind(view, "Deposit");
            Set(view, "EntryAmount", "not-a-number");
            SelectKind(view, "Buy");
            Set(view, "EntrySymbol", "NASDAQ:AAPL");
            Set(view, "EntryQuantity", "1");
            Set(view, "EntryPrice", "100");
            ((TextBox)view.FindName("EntryAmount")).IsEnabled = false;

            var tx = view.BuildTransaction("review-only-no-store");
            Assert.Equal(TransactionKind.Buy, tx.Kind);
            Assert.Equal("NASDAQ:AAPL", tx.Symbol);
            Assert.Equal(1m, tx.Quantity);
            Assert.Equal(100m, tx.Price);
            Assert.Equal("CNY", tx.Currency);
            Assert.Equal(0m, tx.CashAmount); // the stale amount never entered the fact
            Assert.Equal(0m, tx.Fee);

            // Required fields of the CURRENT type still reject.
            Set(view, "EntryQuantity", "not-a-number");
            var quantityError = Assert.Throws<LedgerException>(() => view.BuildTransaction("review-only-no-store"));
            Assert.Contains("无法解析数字", quantityError.Message);

            Set(view, "EntryQuantity", "1");
            Set(view, "EntryPrice", "not-a-number");
            var priceError = Assert.Throws<LedgerException>(() => view.BuildTransaction("review-only-no-store"));
            Assert.Contains("无法解析数字", priceError.Message);
        });
    }

    [Fact]
    public void BuyToDepositIgnoresStaleTradeFieldsButStillRequiresAnAmount()
    {
        WpfTestHost.Run(async () =>
        {
            await Task.CompletedTask;
            var view = NewLedger();

            SelectKind(view, "Buy");
            Set(view, "EntrySymbol", "zzz-not-a-symbol");
            Set(view, "EntryQuantity", "not-a-number");
            Set(view, "EntryPrice", "not-a-number");
            Set(view, "EntryFee", "not-a-number");
            Set(view, "EntryTax", "not-a-number");
            SelectKind(view, "Deposit"); // stale trade text stays in place, per the production switch

            // Required amount missing / broken: still refused.
            Assert.Throws<LedgerException>(() => view.BuildTransaction("review-only-no-store"));
            Set(view, "EntryAmount", "not-a-number");
            Assert.Throws<LedgerException>(() => view.BuildTransaction("review-only-no-store"));

            // Stale symbol/quantity/price/fee/tax must not block a valid deposit.
            Set(view, "EntryAmount", "5000");
            var tx = view.BuildTransaction("review-only-no-store");
            Assert.Equal(TransactionKind.Deposit, tx.Kind);
            Assert.Equal(5000m, tx.CashAmount);
            Assert.Equal("CNY", tx.Currency);
            Assert.Equal("", tx.Symbol); // never resolved, never guessed
            Assert.Equal(0m, tx.Quantity);
            Assert.Equal(0m, tx.Price);
            Assert.Equal(0m, tx.Fee);
            Assert.Equal(0m, tx.Tax);
        });
    }

    [Fact]
    public void BuyToFxExchangeParsesOnlyTheReceiptFieldsAndKeepsBothAmountsRequired()
    {
        WpfTestHost.Run(async () =>
        {
            await Task.CompletedTask;
            var view = NewLedger();

            SelectKind(view, "Buy");
            Set(view, "EntrySymbol", "zzz-not-a-symbol");
            Set(view, "EntryQuantity", "not-a-number");
            Set(view, "EntryPrice", "not-a-number");
            Set(view, "EntryTax", "not-a-number");
            SelectKind(view, "FxExchange");
            SetCurrency(view, "EntryCurrency", "JPY");
            SetCurrency(view, "EntryCounterCurrency", "CNY");
            Set(view, "EntryAmount", "100");
            Set(view, "EntryCounterAmount", "2100");

            var tx = view.BuildTransaction("review-only-no-store");
            Assert.Equal(TransactionKind.FxExchange, tx.Kind);
            Assert.Equal(100m, tx.CashAmount); // the receipt's left side, as typed
            Assert.Equal("JPY", tx.Currency);
            Assert.Equal(2100m, tx.CounterAmount); // the receipt's right side, not rate-derived
            Assert.Equal("CNY", tx.CounterCurrency);
            Assert.Equal(0m, tx.Fee);
            Assert.Equal(0m, tx.Quantity); // stale trade fields never parsed into the fact
            Assert.Equal(0m, tx.Price);
            Assert.Equal("", tx.Symbol);

            // Receipt validation is not relaxed by the type switch.
            Set(view, "EntryCounterAmount", "not-a-number");
            Assert.Throws<LedgerException>(() => view.BuildTransaction("review-only-no-store"));
            Set(view, "EntryCounterAmount", "0");
            var zeroIn = Assert.Throws<LedgerException>(() => view.BuildTransaction("review-only-no-store"));
            Assert.Contains("到账金额必须大于 0", zeroIn.Message);

            Set(view, "EntryCounterAmount", "2100");
            Set(view, "EntryAmount", "0");
            var zeroOut = Assert.Throws<LedgerException>(() => view.BuildTransaction("review-only-no-store"));
            Assert.Contains("换出金额必须大于 0", zeroOut.Message);

            Set(view, "EntryAmount", "100");
            Set(view, "EntryFee", "not-a-number");
            var fee = Assert.Throws<LedgerException>(() => view.BuildTransaction("review-only-no-store"));
            Assert.Contains("无法解析数字", fee.Message);
        });
    }

    private static LedgerView NewLedger() => new();

    private static void SelectKind(LedgerView view, string tag)
    {
        // Unshown control: the SelectionChanged handler's IsLoaded guard skips the field
        // enable/clear pass, which is exactly the leftover state this suite pins down.
        var combo = (ComboBox)view.FindName("EntryKind");
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(item => (string?)item.Tag == tag);
    }

    private static void SetCurrency(LedgerView view, string comboName, string currency)
    {
        var combo = (ComboBox)view.FindName(comboName);
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(item => item.Content?.ToString() == currency);
    }

    private static void Set(LedgerView view, string name, string value) =>
        ((TextBox)view.FindName(name)).Text = value;
}
