using System.IO;
using System.Windows;

namespace ClipInspect.Views;

/// <summary>バージョン情報（版・© ja1bus・プライバシー・MIT License。仕様書 §15.3・§35・§36）。</summary>
public partial class VersionWindow : Window
{
    public VersionWindow()
    {
        InitializeComponent();
        VersionText.Text = $"ClipInspect {MainWindow.AppVersion}";
        LicenseText.Text = Read("ClipInspect.LICENSE");
    }

    /// <summary>exe に埋め込んだ全文を読む（ファイルを開かない）。</summary>
    public static string Read(string name)
    {
        using var s = typeof(VersionWindow).Assembly.GetManifestResourceStream(name);
        if (s is null) return $"（{name} が見つかりません）";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private void Which_Changed(object sender, RoutedEventArgs e)
    {
        if (LicenseText is null) return;
        LicenseText.Text = Read(ShowNotices.IsChecked == true ? "ClipInspect.THIRD-PARTY-NOTICES.md" : "ClipInspect.LICENSE");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
