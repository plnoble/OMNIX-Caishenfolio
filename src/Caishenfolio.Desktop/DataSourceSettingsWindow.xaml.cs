using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Caishenfolio.Host.MarketData;
using Microsoft.Win32;

namespace Caishenfolio.Desktop;

public partial class DataSourceSettingsWindow : Window
{
    private readonly MarketCredentialsStore _store;
    private readonly string _defaultCachePath;

    public DataSourceSettingsWindow(MarketCredentialsStore store)
    {
        InitializeComponent();
        _store = store;
        _defaultCachePath = Path.Combine(
            Path.GetDirectoryName(store.FilePath) ?? ".",
            "bars_cache.db");
        PathText.Text = $"配置文件：{_store.FilePath}";
        LoadUi(_store.Load());
    }

    private void LoadUi(MarketCredentials creds)
    {
        SelectProvider(creds.MarketProvider);
        TrustEnvCheck.IsChecked = creds.HttpTrustEnv;
        TushareTokenBox.Password = creds.TushareToken ?? "";
        TushareTokenPlainBox.Text = creds.TushareToken ?? "";
        AlphaKeyBox.Password = creds.AlphavantageApiKey ?? "";
        AlphaKeyPlainBox.Text = creds.AlphavantageApiKey ?? "";
        BarsCachePathBox.Text = string.IsNullOrWhiteSpace(creds.BarsCachePath)
            ? _defaultCachePath
            : creds.BarsCachePath;
        BarsCacheMaxMbBox.Text = string.IsNullOrWhiteSpace(creds.BarsCacheMaxMb)
            ? "512"
            : creds.BarsCacheMaxMb;
    }

    private void SelectProvider(string provider)
    {
        var target = string.IsNullOrWhiteSpace(provider) ? "auto" : provider.Trim().ToLowerInvariant();
        foreach (var item in ProviderCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, target, StringComparison.OrdinalIgnoreCase))
            {
                ProviderCombo.SelectedItem = item;
                return;
            }
        }

        ProviderCombo.SelectedIndex = 0;
    }

    private string SelectedProvider()
    {
        if (ProviderCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag;
        }

        return "auto";
    }

    private void ShowTushareCheck_OnChanged(object sender, RoutedEventArgs e)
    {
        var show = ShowTushareCheck.IsChecked == true;
        TushareTokenPlainBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TushareTokenBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show)
        {
            TushareTokenPlainBox.Text = TushareTokenBox.Password;
        }
        else
        {
            TushareTokenBox.Password = TushareTokenPlainBox.Text;
        }
    }

    private void ShowAlphaCheck_OnChanged(object sender, RoutedEventArgs e)
    {
        var show = ShowAlphaCheck.IsChecked == true;
        AlphaKeyPlainBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AlphaKeyBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show)
        {
            AlphaKeyPlainBox.Text = AlphaKeyBox.Password;
        }
        else
        {
            AlphaKeyBox.Password = AlphaKeyPlainBox.Text;
        }
    }

    private void BrowseCachePath_OnClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "选择本地 K 线缓存文件位置",
            FileName = "bars_cache.db",
            Filter = "SQLite 数据库 (*.db)|*.db|所有文件 (*.*)|*.*",
            OverwritePrompt = false,
            AddExtension = true,
            DefaultExt = ".db",
        };
        var current = BarsCachePathBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                dlg.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(current));
                dlg.FileName = Path.GetFileName(current);
            }
            catch
            {
                // ignore invalid path
            }
        }

        if (dlg.ShowDialog(this) == true)
        {
            BarsCachePathBox.Text = dlg.FileName;
        }
    }

    private void ResetCachePath_OnClick(object sender, RoutedEventArgs e)
    {
        BarsCachePathBox.Text = _defaultCachePath;
        BarsCacheMaxMbBox.Text = "512";
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        var tushare = ShowTushareCheck.IsChecked == true
            ? TushareTokenPlainBox.Text
            : TushareTokenBox.Password;
        var alpha = ShowAlphaCheck.IsChecked == true
            ? AlphaKeyPlainBox.Text
            : AlphaKeyBox.Password;

        var cachePath = BarsCachePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            cachePath = _defaultCachePath;
        }

        try
        {
            cachePath = Path.GetFullPath(cachePath);
            var dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"缓存路径无效：{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var maxMb = BarsCacheMaxMbBox.Text.Trim();
        if (!int.TryParse(maxMb, out var mb) || mb < 64)
        {
            MessageBox.Show(this, "最大占用请填写不小于 64 的整数（MB）。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var creds = new MarketCredentials
        {
            MarketProvider = SelectedProvider(),
            HttpTrustEnv = TrustEnvCheck.IsChecked == true,
            TushareToken = tushare?.Trim() ?? "",
            AlphavantageApiKey = alpha?.Trim() ?? "",
            BarsCachePath = cachePath,
            BarsCacheMaxMb = mb.ToString(),
        };
        _store.Save(creds);
        MessageBox.Show(
            this,
            "已保存到本机。\n请回到主窗口：停止核心 → 再启动分析核心，新数据源/缓存路径才会生效。\n\n" +
            $"K线缓存：{cachePath}\n上限：{mb} MB",
            "保存成功",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenDocsButton_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var path in EnumerateProviderDocs())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return;
        }

        MessageBox.Show(
            this,
            "未找到 docs/MARKET_PROVIDERS.md。\n可在仓库 docs 目录查看数据源说明。",
            "说明文档",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static IEnumerable<string> EnumerateProviderDocs()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "docs", "MARKET_PROVIDERS.md");
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            yield return Path.Combine(dir.FullName, "docs", "MARKET_PROVIDERS.md");
            dir = dir.Parent;
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
