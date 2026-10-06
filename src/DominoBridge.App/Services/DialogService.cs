using System.Windows;
using Microsoft.Win32;

namespace DominoBridge.App.Services;

public interface IDialogService
{
    string? PickExcelFile();
    bool Confirm(string title, string message);
    void Error(string message);
    void Info(string title, string message);
}

public sealed class WpfDialogService : IDialogService
{
    public string? PickExcelFile()
    {
        var dlg = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", Title = "Select Excel file" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void Error(string message) => MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);

    public void Info(string title, string message) => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
