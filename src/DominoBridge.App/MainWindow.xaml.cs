using System.ComponentModel;
using System.Windows;
using DominoBridge.App.Services;
using DominoBridge.App.ViewModels;

namespace DominoBridge.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(new WpfDialogService());
        DataContext = _vm;
        _vm.LogLines.CollectionChanged += (_, _) =>
        {
            // Scroll only after WPF has finished processing the change (doing it inside the event corrupts the ListBox).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
            }), System.Windows.Threading.DispatcherPriority.Background);
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_vm.ConfirmClose()) { e.Cancel = true; return; }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}
