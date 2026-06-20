using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;

namespace WFInfo.Linux.Views
{
    public partial class SearchItWindow : Window
    {
        public bool IsInUse { get; set; } = false;

        public SearchItWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Launch search. Checks JWT login first.
        /// </summary>
        public void Start()
        {
            SearchField.Text = "";
            SearchField.Watermark = "Search for warframe.market items";

            if (!AppMain.dataBase.IsJwtLoggedIn())
            {
                SearchField.Watermark = "Please log in first";
                try
                {
                    var loginWindow = new LoginWindow();
                    loginWindow.Show();
                }
                catch (Exception ex)
                {
                    AppMain.AddLog($"SearchIt: Failed to open login: {ex.Message}");
                }
                return;
            }

            IsInUse = true;
            Show();
            Topmost = true;
            SearchField.Focus();
        }

        /// <summary>
        /// Reset search state and hide.
        /// </summary>
        public void Finish()
        {
            SearchField.Text = "";
            IsInUse = false;
            Hide();
        }

        private void SearchField_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Finish();
                return;
            }

            if (e.Key != Key.Enter) return;

            string text = SearchField.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                var closest = AppMain.dataBase.GetPartNameHuman(text, out _);
                if (closest == null)
                {
                    AppMain.StatusUpdate("No matching item found", 1);
                    return;
                }

                AppMain.AddLog($"Search-It: Opening listing for \"{closest}\"");
                var primeRewards = new List<List<string>> { new List<string> { closest } };
                App.ShowListingHelper(primeRewards, 0);
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"Search-It error: {ex.Message}");
                AppMain.StatusUpdate("Search failed", 1);
            }

            Finish();
        }
    }
}