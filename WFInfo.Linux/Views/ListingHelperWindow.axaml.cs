using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Newtonsoft.Json.Linq;
using WFInfo.Models;

namespace WFInfo.Linux.Views
{
    public partial class ListingHelperWindow : Window
    {
        private readonly List<ScreenEntry> _screens = new();
        private int _pageIndex = 0;
        private bool _updating = false;
        private bool _posting = false;

        private readonly TextBlock[] _plats;
        private readonly TextBlock[] _amts;
        private readonly TextBlock[] _reps;
        private List<int> _comboIndexMap = new();

        public ListingHelperWindow()
        {
            InitializeComponent();
            _plats = new[] { Plat0, Plat1, Plat2, Plat3, Plat4 };
            _amts = new[] { Amt0, Amt1, Amt2, Amt3, Amt4 };
            _reps = new[] { Rep0, Rep1, Rep2, Rep3, Rep4 };
        }

        /// <summary>
        /// Called from App.axaml.cs on session end. Fetches market data and populates UI.
        /// </summary>
        public void LoadRewards(List<List<string>> rewards, short selectedIdx)
        {
            StatusText.Text = "Loading market data...";
            StatusText.Foreground = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromRgb(0xB1, 0xD0, 0xD9));
            RewardCombo.IsEnabled = false;
            ConfirmButton.IsEnabled = false;
            PriceBox.IsEnabled = false;

            Task.Run(async () =>
            {
                foreach (var screen in rewards)
                {
                    if (screen.Count == 0) continue;
                    try
                    {
                        var collection = await GetRewardCollection(screen, selectedIdx);
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            _screens.Add(new ScreenEntry { Status = "", Rewards = collection });
                            SetScreen(_screens.Count == 1 ? 0 : _pageIndex);
                            UpdateNavigation();
                        });
                    }
                    catch (Exception ex)
                    {
                        AppMain.AddLog($"AutoList: failed to load screen: {ex.Message}");
                    }
                }
            });
        }

        private async Task<RewardCollection> GetRewardCollection(List<string> primeNames, short selectedIdx)
        {
            var tasks = primeNames.Select(async primeName =>
            {
                try
                {
                    return await GetMarketListings(primeName);
                }
                catch (Exception ex)
                {
                    AppMain.AddLog($"AutoList: GetMarketListings failed for {primeName}: {ex.Message}");
                    return EmptyListings();
                }
            }).ToList();

            var results = await Task.WhenAll(tasks);
            var platinumValues = new List<short>(results.Length);
            var marketListings = new List<List<MarketListing>>(results.Length);

            foreach (var listings in results)
            {
                marketListings.Add(listings);
                platinumValues.Add(listings.Count > 0 ? listings[0].Platinum : (short)0);
            }

            return new RewardCollection(primeNames, platinumValues, marketListings,
                (short)Math.Min(selectedIdx, primeNames.Count - 1));
        }

        private static readonly string[] _bannedKeywords = { "kuva", "exilus", "riven", "ayatan", "forma" };

        private static bool IsItemBanned(string item)
        {
            string lower = item.ToLower(CultureInfo.InvariantCulture);
            return _bannedKeywords.Any(k => lower.Contains(k));
        }

        private async Task<List<MarketListing>> GetMarketListings(string primeName)
        {
            if (IsItemBanned(primeName))
                return EmptyListings();

            var results = await AppMain.dataBase.GetTopListings(primeName);
            if (results == null) return EmptyListings();

            var listings = new List<MarketListing>();
            var sellOrders = results["data"]?["sell"];
            if (sellOrders != null)
            {
                foreach (var item in sellOrders)
                {
                    listings.Add(new MarketListing(
                        item.Value<short>("platinum"),
                        item.Value<short>("quantity"),
                        item["user"]?.Value<short>("reputation") ?? 0
                    ));
                }
            }

            // Pad to 5 entries
            while (listings.Count < 5)
                listings.Add(new MarketListing(0, 0, 0));
            return listings;
        }

        private static List<MarketListing> EmptyListings()
        {
            var list = new List<MarketListing>(5);
            for (int i = 0; i < 5; i++)
                list.Add(new MarketListing(0, 0, 0));
            return list;
        }

        private void SetScreen(int index)
        {
            if (index < 0 || index >= _screens.Count) return;
            _pageIndex = index;
            var screen = _screens[index];
            _updating = true;

            _comboIndexMap.Clear();
            var filtered = new List<string>();
            for (int i = 0; i < screen.Rewards.PrimeNames.Count; i++)
            {
                if (!string.IsNullOrEmpty(screen.Rewards.PrimeNames[i]))
                {
                    _comboIndexMap.Add(i);
                    filtered.Add(screen.Rewards.PrimeNames[i]);
                }
            }
            RewardCombo.ItemsSource = filtered;
            int origIdx = Math.Min(screen.Rewards.RewardIndex, screen.Rewards.PrimeNames.Count - 1);
            int filteredIdx = _comboIndexMap.IndexOf(origIdx);
            if (filteredIdx < 0) filteredIdx = 0;
            RewardCombo.SelectedIndex = Math.Min(filteredIdx, filtered.Count - 1);

            UpdateStatus(screen.Status);
            SetListings(RewardCombo.SelectedIndex);
            _updating = false;
        }

        private void SetListings(int comboIndex)
        {
            if (_pageIndex >= _screens.Count) return;
            var screen = _screens[_pageIndex];
            int index = comboIndex >= 0 && comboIndex < _comboIndexMap.Count
                ? _comboIndexMap[comboIndex] : comboIndex;
            if (index < 0 || index >= screen.Rewards.MarketListings.Count) return;

            var listings = screen.Rewards.MarketListings[index];
            PriceBox.Text = screen.Rewards.PlatinumValues[index].ToString(CultureInfo.InvariantCulture);

            for (int i = 0; i < 5 && i < listings.Count; i++)
            {
                _plats[i].Text = listings[i].Platinum.ToString();
                _amts[i].Text = listings[i].Amount.ToString();
                _reps[i].Text = listings[i].Reputation.ToString();
            }

            bool banned = IsItemBanned(screen.Rewards.PrimeNames[index]);
            ConfirmButton.IsEnabled = !banned && screen.Status != "successful" && !_posting;
            PriceBox.IsEnabled = !banned && screen.Status != "successful";
            if (banned)
                StatusText.Text = "Cannot list this item";
        }

        private void UpdateStatus(string status)
        {
            switch (status)
            {
                case "successful":
                    StatusText.Text = "Listing already successfully posted";
                    StatusText.Foreground = new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.FromRgb(0, 200, 0));
                    ConfirmButton.IsEnabled = false;
                    RewardCombo.IsEnabled = false;
                    PriceBox.IsEnabled = false;
                    ListingsBorder.IsVisible = false;
                    break;
                case "":
                    StatusText.Text = "";
                    ConfirmButton.IsEnabled = !_posting;
                    RewardCombo.IsEnabled = true;
                    PriceBox.IsEnabled = true;
                    ListingsBorder.IsVisible = true;
                    break;
                default:
                    StatusText.Text = status;
                    StatusText.Foreground = new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.FromRgb(255, 80, 80));
                    ConfirmButton.IsEnabled = !_posting;
                    RewardCombo.IsEnabled = true;
                    PriceBox.IsEnabled = true;
                    ListingsBorder.IsVisible = true;
                    break;
            }
        }

        private void UpdateNavigation()
        {
            PageText.Text = $"{_pageIndex + 1} / {_screens.Count}";
            BackButton.IsEnabled = _pageIndex > 0;
            NextButton.IsEnabled = _pageIndex < _screens.Count - 1;
        }

        // ── Event handlers ──

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                var pos = e.GetPosition((Avalonia.Visual)sender);
                if (pos.Y > 22) return;
                try { BeginMoveDrag(e); }
                catch (InvalidOperationException) { }
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void RewardCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updating || RewardCombo.SelectedIndex < 0) return;
            SetListings(RewardCombo.SelectedIndex);
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (_posting) return;
            if (_pageIndex >= _screens.Count) return;
            var screen = _screens[_pageIndex];

            string primeItem = RewardCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(primeItem)) return;

            if (!int.TryParse(Regex.Replace(PriceBox.Text ?? "", "[^0-9]", ""),
                    out int platinum) || platinum <= 0)
            {
                StatusText.Text = "Invalid price";
                return;
            }

            _posting = true;
            ConfirmButton.IsEnabled = false;
            ConfirmButton.Content = "...";

            ShowLoading();

            Task.Run(async () =>
            {
                try
                {
                    bool success = await PlaceListing(primeItem, platinum);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _posting = false;
                        screen.Status = success ? "successful" : "Failed to post listing";
                        UpdateStatus(screen.Status);
                        ConfirmButton.Content = "Confirm Listing";
                        ShowFinished();
                    });
                }
                catch (Exception ex)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _posting = false;
                        screen.Status = ex.Message;
                        UpdateStatus(screen.Status);
                        ConfirmButton.Content = "Confirm Listing";
                        ConfirmButton.IsEnabled = true;
                        ShowFinished();
                    });
                }
            });
        }

        private async Task<bool> PlaceListing(string primeItem, int platinum)
        {
            var existing = await AppMain.dataBase.GetCurrentListing(primeItem);
            if (existing == null)
                return await AppMain.dataBase.ListItem(primeItem, platinum, 1);
            else
            {
                string listingId = (string)existing["id"];
                int quantity = (int)existing["quantity"];
                return await AppMain.dataBase.UpdateListing(listingId, platinum, quantity + 1);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_screens.Count <= 1)
            {
                // Last screen, close the window
                Close();
                return;
            }

            if (_pageIndex == 0)
            {
                SetScreen(1);
                _screens.RemoveAt(0);
                _pageIndex = 0;
            }
            else
            {
                _screens.RemoveAt(_pageIndex);
                --_pageIndex;
                SetScreen(_pageIndex);
            }
            UpdateNavigation();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_pageIndex > 0)
            {
                _pageIndex--;
                SetScreen(_pageIndex);
                UpdateNavigation();
            }
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_pageIndex < _screens.Count - 1)
            {
                _pageIndex++;
                SetScreen(_pageIndex);
                UpdateNavigation();
            }
        }

        private void ShowLoading()
        {
            CancelButton.Content = "loading";
            NextButton.IsEnabled = false;
            BackButton.IsEnabled = false;
        }

        private void ShowFinished()
        {
            CancelButton.Content = "Cancel";
            UpdateNavigation();
        }

        private void PriceBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updating) return;
            string text = PriceBox.Text;
            if (string.IsNullOrEmpty(text)) return;
            string cleaned = Regex.Replace(text, "[^0-9]", "");
            if (cleaned != text)
            {
                _updating = true;
                PriceBox.Text = cleaned;
                PriceBox.CaretIndex = cleaned.Length;
                _updating = false;
            }
        }

        private class ScreenEntry
        {
            public string Status { get; set; } = "";
            public RewardCollection Rewards { get; set; }
        }
    }
}