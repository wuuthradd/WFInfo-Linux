using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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
        private List<int> _comboIndexMap = new();



        private static readonly SolidColorBrush SellPlatFg = new(Color.FromRgb(0xCB, 0x4A, 0x9E));
        private static readonly SolidColorBrush RepGoodFg = new(Color.FromRgb(0x00, 0xA9, 0x6C));
        private static readonly SolidColorBrush RepNeutralFg = new(Color.FromRgb(0x73, 0x90, 0x98));
        private static readonly SolidColorBrush RepBadFg = new(Color.FromRgb(0xA9, 0x41, 0x00));
        private static readonly SolidColorBrush DimFg = new(Color.FromRgb(0x88, 0x88, 0x88));
        private static readonly SolidColorBrush LightFg = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
        private static readonly SolidColorBrush InfoBrush = new(Color.FromRgb(0xB1, 0xD0, 0xD9));
        private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(0, 200, 0));
        private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(255, 80, 80));

        public ListingHelperWindow()
        {
            InitializeComponent();
        }

        public void LoadRewards(List<List<string>> rewards, short selectedIdx)
        {
            foreach (var screen in rewards)
            {
                if (screen.Count == 0) continue;
                var platValues = new List<short>(new short[screen.Count]);
                var marketResults = new List<JObject>(new JObject[screen.Count]);
                var collection = new RewardCollection(screen, platValues, marketResults,
                    (short)Math.Min(selectedIdx, screen.Count - 1));
                _screens.Add(new ScreenEntry { Status = "", Rewards = collection });
            }

            if (_screens.Count > 0)
            {
                SetScreen(0);
                UpdateNavigation();
                LoadSelectedItemListings();
            }
        }

        private void LoadSelectedItemListings()
        {
            if (_pageIndex >= _screens.Count) return;
            var screen = _screens[_pageIndex];
            int comboIdx = RewardCombo.SelectedIndex;
            int rewardIdx = comboIdx >= 0 && comboIdx < _comboIndexMap.Count
                ? _comboIndexMap[comboIdx] : 0;
            if (rewardIdx < 0 || rewardIdx >= screen.Rewards.PrimeNames.Count) return;

            string primeName = screen.Rewards.PrimeNames[rewardIdx];
            if (IsItemBanned(primeName)) return;

            if (screen.Rewards.MarketResults[rewardIdx] != null) return;

            ConfirmButton.IsEnabled = false;

            Task.Run(async () =>
            {
                try
                {
                    var result = await AppMain.dataBase.GetTopListings(primeName);
                    short topPlat = 0;
                    var sellOrders = result?["data"]?["sell"];
                    if (sellOrders != null && sellOrders.HasValues)
                        topPlat = sellOrders.First.Value<short>("platinum");

                    Dispatcher.UIThread.Post(() =>
                    {
                        screen.Rewards.MarketResults[rewardIdx] = result;
                        screen.Rewards.PlatinumValues[rewardIdx] = topPlat;
                        SetListings(comboIdx);
                    });
                }
                catch (Exception ex)
                {
                    AppMain.AddLog($"AutoList: GetTopListings failed for {primeName}: {ex.Message}");
                    Dispatcher.UIThread.Post(() => ConfirmButton.IsEnabled = true);
                }
            });
        }

        private static readonly string[] _bannedKeywords = { "kuva", "exilus", "riven", "ayatan", "forma" };

        private static bool IsItemBanned(string item)
        {
            string lower = item.ToLower(CultureInfo.InvariantCulture);
            return _bannedKeywords.Any(k => lower.Contains(k));
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
            if (screen.ListedComboIndex >= 0 && screen.ListedComboIndex < filtered.Count)
            {
                RewardCombo.SelectedIndex = screen.ListedComboIndex;
            }
            else
            {
                int origIdx = Math.Min(screen.Rewards.RewardIndex, screen.Rewards.PrimeNames.Count - 1);
                int filteredIdx = _comboIndexMap.IndexOf(origIdx);
                if (filteredIdx < 0) filteredIdx = 0;
                RewardCombo.SelectedIndex = Math.Min(filteredIdx, filtered.Count - 1);
            }

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
            if (index < 0 || index >= screen.Rewards.MarketResults.Count) return;

            bool listed = screen.Status == "successful";

            if (listed && screen.ListedPrice > 0)
                PriceBox.Text = screen.ListedPrice.ToString(CultureInfo.InvariantCulture);
            else
                PriceBox.Text = screen.Rewards.PlatinumValues[index].ToString(CultureInfo.InvariantCulture);

            PopulateTopListings(screen.Rewards.MarketResults[index]);

            bool banned = IsItemBanned(screen.Rewards.PrimeNames[index]);
            ConfirmButton.IsEnabled = !banned && !listed && !_posting;
            PriceBox.IsEnabled = !banned && !listed;

            if (banned)
            {
                ErrorText.Text = "Cannot list this item";
                ErrorText.Foreground = ErrorBrush;
            }
            else if (!listed)
            {
                ErrorText.Text = "";
            }
        }

        private void PopulateTopListings(JObject results)
        {
            TopOrdersPanel.Children.Clear();
            TopOrdersPanel.RowDefinitions.Clear();
            TopOrdersPanel.ColumnDefinitions.Clear();

            var orders = results?["data"]?["sell"];
            if (orders == null || !orders.HasValues)
            {
                TopOrdersPanel.Children.Add(new TextBlock
                {
                    Text = "Orders not found",
                    Foreground = DimFg, FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8)
                });
                return;
            }

            var platIcon = Application.Current.FindResource("IconPlatinum") as StreamGeometry;
            var cubesIcon = Application.Current.FindResource("IconCubes") as StreamGeometry;
            var smileIcon = Application.Current.FindResource("IconSmile") as StreamGeometry;
            var mehIcon = Application.Current.FindResource("IconMeh") as StreamGeometry;
            var frownIcon = Application.Current.FindResource("IconFrown") as StreamGeometry;

            for (int c = 0; c < 3; c++)
                TopOrdersPanel.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

            int maxPlatLen = 0;
            int scanCount = 0;
            foreach (var o in orders)
            {
                if (scanCount >= 5) break;
                int p = o.Value<int?>("platinum") ?? 0;
                int len = p.ToString().Length;
                if (len > maxPlatLen) maxPlatLen = len;
                scanCount++;
            }
            double platTextWidth = maxPlatLen * 7.0;

            int rowIdx = 0;
            foreach (var item in orders)
            {
                if (rowIdx >= 5) break;

                int rep = item["user"]?.Value<int?>("reputation") ?? 0;
                int plat = item.Value<int?>("platinum") ?? 0;
                int qty = item.Value<int?>("quantity") ?? 1;

                TopOrdersPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                // Reputation
                var repFg = rep >= 5 ? RepGoodFg : (rep < -5 ? RepBadFg : RepNeutralFg);
                var repPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 2,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 2)
                };
                repPanel.Children.Add(new TextBlock { Text = rep.ToString(), Foreground = repFg, FontSize = 11 });
                var faceIcon = rep >= 5 ? smileIcon : (rep < -5 ? frownIcon : mehIcon);
                if (faceIcon != null)
                    repPanel.Children.Add(new PathIcon { Data = faceIcon, Foreground = repFg, Width = 11, Height = 11 });
                Grid.SetRow(repPanel, rowIdx);
                Grid.SetColumn(repPanel, 0);
                TopOrdersPanel.Children.Add(repPanel);

                // Platinum
                var platPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 2,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2)
                };
                platPanel.Children.Add(new TextBlock
                {
                    Text = plat.ToString(), FontWeight = FontWeight.Bold,
                    Foreground = SellPlatFg, FontSize = 11,
                    MinWidth = platTextWidth, TextAlignment = TextAlignment.Right
                });
                if (platIcon != null)
                    platPanel.Children.Add(new PathIcon { Data = platIcon, Foreground = SellPlatFg, Width = 10, Height = 10 });
                Grid.SetRow(platPanel, rowIdx);
                Grid.SetColumn(platPanel, 1);
                TopOrdersPanel.Children.Add(platPanel);

                // Quantity
                var qtyPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 2,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 2)
                };
                qtyPanel.Children.Add(new TextBlock { Text = qty.ToString(), Foreground = LightFg, FontSize = 11 });
                if (cubesIcon != null)
                    qtyPanel.Children.Add(new PathIcon { Data = cubesIcon, Foreground = LightFg, Width = 10, Height = 10 });
                Grid.SetRow(qtyPanel, rowIdx);
                Grid.SetColumn(qtyPanel, 2);
                TopOrdersPanel.Children.Add(qtyPanel);

                rowIdx++;
            }
        }



        private void UpdateStatus(string status)
        {
            switch (status)
            {
                case "successful":
                    ErrorText.Text = "Listed successfully";
                    ErrorText.Foreground = SuccessBrush;
                    ConfirmButton.IsEnabled = false;
                    RewardCombo.IsEnabled = false;
                    PriceBox.IsEnabled = false;
                    TopOrdersLabel.IsVisible = false;
                    ListingsBorder.IsVisible = false;
                    break;
                case "":
                    ErrorText.Text = "";
                    ConfirmButton.IsEnabled = !_posting;
                    RewardCombo.IsEnabled = true;
                    PriceBox.IsEnabled = true;
                    TopOrdersLabel.IsVisible = true;
                    ListingsBorder.IsVisible = true;
                    break;
                default:
                    ErrorText.Text = status;
                    ErrorText.Foreground = ErrorBrush;
                    ConfirmButton.IsEnabled = !_posting;
                    RewardCombo.IsEnabled = true;
                    PriceBox.IsEnabled = true;
                    TopOrdersLabel.IsVisible = true;
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

        private void AdjustField(TextBox box, int delta, int min, int max)
        {
            int val = int.TryParse(box.Text, out int v) ? v : min;
            val = Math.Clamp(val + delta, min, max);
            _updating = true;
            box.Text = val.ToString();
            _updating = false;
        }

        private void PricePlus_Click(object sender, RoutedEventArgs e) { if (PriceBox.IsEnabled) AdjustField(PriceBox, 1, 1, 900000); }
        private void PriceMinus_Click(object sender, RoutedEventArgs e) { if (PriceBox.IsEnabled) AdjustField(PriceBox, -1, 1, 900000); }


        private void NumberBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updating || sender is not TextBox tb) return;
            string text = tb.Text;
            if (string.IsNullOrEmpty(text)) return;
            string cleaned = Regex.Replace(text, "[^0-9]", "");
            if (cleaned != text)
            {
                _updating = true;
                tb.Text = cleaned;
                tb.CaretIndex = cleaned.Length;
                _updating = false;
            }
        }

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                var pos = e.GetPosition((Visual)sender);
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
            LoadSelectedItemListings();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (_posting) return;
            if (_pageIndex >= _screens.Count) return;
            var screen = _screens[_pageIndex];

            string primeItem = RewardCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(primeItem)) return;

            if (!int.TryParse(PriceBox.Text, out int platinum) || platinum <= 0)
            {
                ErrorText.Text = "Invalid price";
                ErrorText.Foreground = ErrorBrush;
                return;
            }

            int quantity = 1;

            _posting = true;
            ConfirmButton.IsEnabled = false;
            ConfirmButton.Content = "...";
            ShowLoading();

            Task.Run(async () =>
            {
                try
                {
                    bool success = await PlaceListing(primeItem, platinum, quantity);
                    Dispatcher.UIThread.Post(() =>
                    {
                        _posting = false;
                        screen.Status = success ? "successful" : "Failed to post listing";
                        if (success)
                        {
                            screen.ListedPrice = platinum;
                            screen.ListedComboIndex = RewardCombo.SelectedIndex;
                            MyListingsWindow.ReloadIfOpen();
                        }
                        UpdateStatus(screen.Status);
                        ConfirmButton.Content = "Confirm Listing";
                        ShowFinished();
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
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

        private async Task<bool> PlaceListing(string primeItem, int platinum, int quantity)
        {
            var existing = await AppMain.dataBase.GetCurrentListing(primeItem);
            if (existing == null)
                return await AppMain.dataBase.ListItem(primeItem, platinum, quantity);
            else
            {
                string listingId = (string)existing["id"];
                int existingQty = (int)existing["quantity"];
                return await AppMain.dataBase.UpdateListing(listingId, platinum, existingQty + quantity);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_screens.Count <= 1)
            {
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
                LoadSelectedItemListings();
            }
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_pageIndex < _screens.Count - 1)
            {
                _pageIndex++;
                SetScreen(_pageIndex);
                UpdateNavigation();
                LoadSelectedItemListings();
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
            CancelButton.Content = "Skip";
            UpdateNavigation();
        }

        private class ScreenEntry
        {
            public string Status { get; set; } = "";
            public int ListedPrice { get; set; } = 0;
            public int ListedComboIndex { get; set; } = -1;
            public RewardCollection Rewards { get; set; }
        }
    }
}