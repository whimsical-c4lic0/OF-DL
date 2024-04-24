using Avalonia.Controls;
using OF_DL.ViewModels;

namespace OF_DL;

public partial class MainWindow : Window
{

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}

