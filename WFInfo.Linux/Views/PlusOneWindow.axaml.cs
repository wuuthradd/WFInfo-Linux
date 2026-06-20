using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using WFInfo.Services;

namespace WFInfo.Linux.Views
{
    public partial class PlusOneWindow : Window
    {
        private static readonly string _reviewMarkerPath =
            Path.Combine(PlatformPaths.AppDataPath, "review_posted");
        private int _sliderCounter;
        private bool _slidersWired;
        private bool _easterEggTriggered;

        public PlusOneWindow()
        {
            InitializeComponent();

            // Check if already reviewed
            if (File.Exists(_reviewMarkerPath))
                Processed();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            if (_slidersWired) return;
            _slidersWired = true;

            var grid = (this.Content as Border)?.Child as Grid;
            if (grid != null)
            {
                foreach (var child in grid.Children)
                {
                    if (child is Slider slider)
                        slider.ValueChanged += Slider_ValueChanged;
                }
            }
        }

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                try { BeginMoveDrag(e); }
                catch (InvalidOperationException) { }
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        // Clear placeholder text on focus
        private void CommentBox_GotFocus(object sender, GotFocusEventArgs e)
        {
            if (CommentBox.Text != null && CommentBox.Text.Contains("Optional comment field"))
                CommentBox.Text = "";
        }

        // Slider easter egg trigger
        private void CommentBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_easterEggTriggered && CommentBox.Text != null &&
                (CommentBox.Text.Contains("Give me sliders") || CommentBox.Text.Contains("more sliders")))
            {
                _easterEggTriggered = true;
                Height += 58;
            }
        }

        private async void Post_Click(object sender, RoutedEventArgs e)
        {
            PostButton.IsEnabled = false;
            var message = CommentBox.Text == "Optional comment field" ? "" : CommentBox.Text;

            try
            {
                await Task.Run(async () =>
                {
                    await AppMain.dataBase.PostReview(message ?? "");
                });
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"PostReview failed: {ex.Message}");
            }

            try { File.WriteAllText(_reviewMarkerPath, "true"); } catch { }

            Processed();
        }

        // Easter egg: each slider drag grows the window
        private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_sliderCounter >= 9) return;
            Height += 40;
            _sliderCounter++;
        }

        private void Processed()
        {
            CommentBox.Text = "Review submitted, thank you";
            CommentBox.IsEnabled = false;
            PostButton.Content = "Thank you!";
            PostButton.IsEnabled = false;
        }
    }
}