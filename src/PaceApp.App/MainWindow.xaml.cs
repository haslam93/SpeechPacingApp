using System.ComponentModel;
using System.Windows;
using PaceApp.App.ViewModels;

namespace PaceApp.App;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        StateChanged += OnStateChanged;
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    private void OnStateChanged(object? sender, EventArgs eventArgs)
    {
        if (WindowState == WindowState.Minimized && !ViewModel.CanCloseWindow)
        {
            Hide();
        }
    }
}