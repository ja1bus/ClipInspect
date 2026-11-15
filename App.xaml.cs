using System.Windows;
using System.Windows.Threading;

namespace ClipInspect;

/// <summary>起動。言語の設定・保存はない（仕様書 §35）。未処理の例外は画面に出して続行する（ログは作らない。§31）。</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // クリップボードの内容は例外のメッセージに含めない（含むのは型と API のエラーだけ）。
        MessageBox.Show($"予期しないエラーが起きました。\n{e.Exception.GetType().Name}: {e.Exception.Message}", "ClipInspect",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
