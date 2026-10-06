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
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
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
