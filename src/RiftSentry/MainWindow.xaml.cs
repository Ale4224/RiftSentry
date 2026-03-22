using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using RiftSentry.ViewModels;

namespace RiftSentry;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ShowInTaskbar = false;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        if (e.NewValue is MainViewModel newVm)
        {
            newVm.PropertyChanged += OnVmPropertyChanged;
            ApplyInGameVisibility(newVm);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsInGame))
            return;
        if (sender is MainViewModel vm)
            Dispatcher.Invoke(() => ApplyInGameVisibility(vm));
    }

    private void ApplyInGameVisibility(MainViewModel vm)
    {
        if (vm.IsInGame)
        {
            if (!IsVisible)
                Show();
            ShowInTaskbar = true;
            return;
        }

        ShowInTaskbar = false;
        if (IsVisible)
            Hide();
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }
}