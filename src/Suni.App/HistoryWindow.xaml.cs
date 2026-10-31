using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Suni.App.ViewModels;

namespace Suni.App;

public partial class HistoryWindow : Window
{
    private readonly HistoryViewModel _vm;

    public HistoryWindow(HistoryViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;
        viewModel.Owner = this;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    /// <summary>Nháy đúp vào bản dịch để sửa.</summary>
    private void OnTranslationMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: HistoryLine line })
        {
            _vm.BeginEditCommand.Execute(line);
            e.Handled = true;
        }
    }

    private async void OnEditKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: HistoryLine line } box)
            return;
        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                var caret = box.CaretIndex;
                box.Text = box.Text.Insert(caret, Environment.NewLine);
                box.CaretIndex = caret + Environment.NewLine.Length;
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                await _vm.SaveEditCommand.ExecuteAsync(line);
                break;
            case Key.Escape:
                e.Handled = true;
                _vm.CancelEditCommand.Execute(line);
                break;
        }
    }

    /// <summary>Ô sửa vừa hiện → đặt con trỏ vào cuối để gõ ngay.</summary>
    private void OnEditBoxVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
            Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
            });
    }
}
