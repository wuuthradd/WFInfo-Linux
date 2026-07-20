using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace WFInfo.Linux.Views
{
    public partial class ColorblindWarningWindow : Window
    {
        public ColorblindWarningWindow()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void OpenGuide_Click(object sender, PointerPressedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("https://wfinfo.warframestat.us/#themeAdjuster") { UseShellExecute = true }); }
            catch { }
        }

        private void Window_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }
    }
}
