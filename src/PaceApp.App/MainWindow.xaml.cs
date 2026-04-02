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
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = 24;
    }

    private void OnStateChanged(object? sender, EventArgs eventArgs)
    {
        if (WindowState == WindowState.Minimized && !ViewModel.CanCloseWindow)
        {
            Hide();
        }
    }
}