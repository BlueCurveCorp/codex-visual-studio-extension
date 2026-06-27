using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexVsix;

public partial class UserInputPromptWindow : Window
{
    public UserInputPromptWindow()
    {
        this.InitializeComponent();
        Loaded += this.OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Control? firstInput = FindFirstInput(this);
        if (firstInput is null)
        {
            return;
        }

        _ = firstInput.Focus();
        _ = Keyboard.Focus(firstInput);
    }

    private static Control? FindFirstInput(DependencyObject parent)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is Control control
                && control.IsVisible
                && control.IsEnabled
                && (control is TextBox || control is ComboBox))
            {
                return control;
            }

            Control? nested = FindFirstInput(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
