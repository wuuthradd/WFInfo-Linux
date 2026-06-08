using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace WFInfo.Linux.Views
{
    public partial class WelcomeDialogue : Window
    {
        public WelcomeDialogue()
        {
            InitializeComponent();
        }

        private void Grid_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                try { BeginMoveDrag(e); }
                catch (InvalidOperationException) { }
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}