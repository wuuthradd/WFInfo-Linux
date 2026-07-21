using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Newtonsoft.Json.Linq;
using WFInfo.Models;
using WFInfo.Settings;

namespace WFInfo.Linux.Views
{
    public partial class TradeDoneWindow : Window
    {
        private readonly List<TradeDoneEntry> _queue = new();
        private int _currentIndex = 0;

        public TradeDoneWindow()
        {
            InitializeComponent();
            DecrementCheckbox.IsChecked = ApplicationSettings.GlobalReadonlySettings.TradeDecrementInventory;
            Closing += (_, e) =>
            {
                e.Cancel = true;
                Hide();
            };
            UpdateDisplay();
        }

        public void AddEntry(TradeDoneEntry entry)
        {
            _queue.Add(entry);
            if (_queue.Count == 1)
            {
                _currentIndex = 0;
                UpdateDisplay();
            }
            else
            {
                // Only update page counter and nav buttons, don't reset current entry's fields
                PageText.Text = $"{_currentIndex + 1} of {_queue.Count}";
                NextBtn.IsEnabled = _currentIndex < _queue.Count - 1;
            }
            SizeToContent = SizeToContent.Height;
            Show();
            Activate();
        }

        private void UpdateDisplay()
        {
            if (_queue.Count == 0 || _currentIndex >= _queue.Count)
            {
                ItemNameText.Text = "-";
                PartnerText.Text = "-";
                CountText.Text = "-";
                MatchedOrderText.Text = "No trades in queue";
                OrderEditGrid.IsVisible = false;
                StatusText.Text = "";
                PageText.Text = "0 of 0";
                MarkSoldBtn.IsEnabled = false;
                SkipBtn.IsEnabled = false;
                BackBtn.IsEnabled = false;
                NextBtn.IsEnabled = false;
                return;
            }

            var entry = _queue[_currentIndex];
            ItemNameText.Text = entry.ItemName;
            SetPartnerDisplay(entry.Partner);
            CountText.Text = entry.Count.ToString();

            if (!string.IsNullOrEmpty(entry.MatchedOrderId))
            {
                string rankSuffix = entry.MatchedRank.HasValue ? $" (Rank {entry.MatchedRank.Value})" : "";
                MatchedOrderText.Text = $"{entry.MatchedItemName}{rankSuffix}";
                PriceBox.Text = entry.MatchedPlatinum.ToString();
                CountBox.Text = entry.Count.ToString();
                OrderEditGrid.IsVisible = true;
            }
            else
            {
                MatchedOrderText.Text = "No matching order found";
                OrderEditGrid.IsVisible = false;
            }

            StatusText.Text = entry.Status;
            StatusText.Foreground = entry.Status.Contains("Failed")
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFFF5252"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF4CAF50"));

            MarkSoldBtn.IsEnabled = !string.IsNullOrEmpty(entry.MatchedOrderId) && string.IsNullOrEmpty(entry.Status);
            SkipBtn.IsEnabled = true;
            PageText.Text = $"{_currentIndex + 1} of {_queue.Count}";
            BackBtn.IsEnabled = _currentIndex > 0;
            NextBtn.IsEnabled = _currentIndex < _queue.Count - 1;
        }

        private void SetPartnerDisplay(string partner)
        {
            if (string.IsNullOrEmpty(partner))
            {
                PartnerText.Text = "-";
                PlatformText.Text = "";
                return;
            }

            string name = partner;
            string platform = "";
            if (name.Length > 0)
            {
                char last = name[name.Length - 1];
                switch (last)
                {
                    case '\uE000': platform = "[PC]"; break;
                    case '\uE001': platform = "[PS]"; break;
                    case '\uE002': platform = "[XB]"; break;
                    case '\uE003': platform = "[SW]"; break;
                    case '\uE004': platform = "[X-Play]"; break;
                }
                if (platform.Length > 0)
                    name = name.Substring(0, name.Length - 1).TrimEnd();
            }

            PartnerText.Text = name;
            PlatformText.Text = platform;
        }

        private void MarkSold_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex >= _queue.Count) return;
            var entry = _queue[_currentIndex];
            if (string.IsNullOrEmpty(entry.MatchedOrderId)) return;

            if (!int.TryParse(PriceBox.Text, out int newPrice) || newPrice < 1)
            {
                StatusText.Text = "Invalid price";
                StatusText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFFF5252"));
                return;
            }
            if (!int.TryParse(CountBox.Text, out int newCount) || newCount < 1)
            {
                StatusText.Text = "Invalid count";
                StatusText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFFF5252"));
                return;
            }

            // Clamp count to order quantity so we close all instead of failing
            int closeCount = Math.Min(newCount, entry.MatchedQuantity);

            MarkSoldBtn.IsEnabled = false;
            MarkSoldBtn.Content = "...";

            bool priceChanged = newPrice != entry.MatchedPlatinum;

            int originalPrice = entry.MatchedPlatinum;
            int remainingQty = entry.MatchedQuantity - closeCount;

            Task.Run(async () =>
            {
                // If price changed, edit the order to the new price before closing
                if (priceChanged)
                {
                    bool edited = await AppMain.dataBase.UpdateListing(
                        entry.MatchedOrderId, newPrice, entry.MatchedQuantity,
                        rank: entry.MatchedRank);
                    if (!edited)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            entry.Status = "Failed to update price";
                            MarkSoldBtn.Content = "Mark Sold";
                            UpdateDisplay();
                        });
                        return;
                    }
                    await Task.Delay(100);
                }

                bool success = await AppMain.dataBase.CloseOrder(entry.MatchedOrderId, closeCount);

                // If price was changed and order still has remaining quantity, restore original price
                if (success && priceChanged && remainingQty > 0)
                {
                    await Task.Delay(100);
                    await AppMain.dataBase.UpdateListing(
                        entry.MatchedOrderId, originalPrice, remainingQty,
                        rank: entry.MatchedRank);
                }

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (success)
                    {
                        entry.Status = "Marked as sold";

                        if (DecrementCheckbox.IsChecked == true)
                            DecrementInventory(entry.ItemName, closeCount);

                        MyListingsWindow.DecrementOrderIfOpen(entry.MatchedOrderId, closeCount);
                        TransactionHistoryWindow.ReloadIfOpen();
                        AdvanceQueue();
                    }
                    else
                    {
                        entry.Status = "Failed to close order";
                        MarkSoldBtn.Content = "Mark Sold";
                        UpdateDisplay();
                    }
                });
            });
        }

        private static void DecrementInventory(string partName, int count)
        {
            if (string.IsNullOrEmpty(partName) || !partName.Contains("Prime"))
                return;

            try
            {
                string[] parts = partName.Split(new[] { "Prime" }, 2, StringSplitOptions.None);
                string primeName = parts[0] + "Prime";
                string partKey = primeName + (parts[1].Length > 10 && !parts[1].Contains("Kubrow")
                    ? parts[1].Replace(" Blueprint", "") : parts[1]);

                var eqmt = AppMain.dataBase.equipmentData[primeName];
                if (eqmt?["parts"]?[partKey] is JObject partObj)
                {
                    int owned = partObj["owned"]?.ToObject<int>() ?? 0;
                    int newOwned = Math.Max(0, owned - count);
                    partObj["owned"] = newOwned;
                    AppMain.dataBase.SaveAllJSONs();
                    EquipmentWindow.ReloadIfOpen();
                }
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"TradeDone: inventory decrement failed for \"{partName}\": {ex.Message}");
            }
        }

        private void Skip_Click(object sender, RoutedEventArgs e) => AdvanceQueue();

        private void AdvanceQueue()
        {
            if (_queue.Count == 0) return;
            _queue.RemoveAt(_currentIndex);
            if (_currentIndex >= _queue.Count)
                _currentIndex = Math.Max(0, _queue.Count - 1);
            MarkSoldBtn.Content = "Mark Sold";
            UpdateDisplay();
            SizeToContent = SizeToContent.Height;
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex > 0)
            {
                _currentIndex--;
                UpdateDisplay();
            }
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < _queue.Count - 1)
            {
                _currentIndex++;
                UpdateDisplay();
            }
        }

        private void PricePlus_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(PriceBox.Text, out int val))
                PriceBox.Text = (val + 1).ToString();
        }

        private void PriceMinus_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(PriceBox.Text, out int val) && val > 1)
                PriceBox.Text = (val - 1).ToString();
        }

        private void CountPlus_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(CountBox.Text, out int val))
                CountBox.Text = (val + 1).ToString();
        }

        private void CountMinus_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(CountBox.Text, out int val) && val > 1)
                CountBox.Text = (val - 1).ToString();
        }

        private void OnDecrementChanged(object sender, RoutedEventArgs e)
        {
            var settings = ApplicationSettings.GlobalSettings;
            settings.TradeDecrementInventory = DecrementCheckbox.IsChecked == true;
            settings.Save();
        }

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
        private void Close_Click(object sender, RoutedEventArgs e) => Hide();
    }
}