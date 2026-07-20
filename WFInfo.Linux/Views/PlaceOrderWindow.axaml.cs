using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace WFInfo.Linux.Views
{
    public partial class PlaceOrderWindow : Window
    {
        private static readonly SolidColorBrush ActiveSell = new(Color.FromRgb(0xCB, 0x4A, 0x9E));
        private static readonly SolidColorBrush ActiveBuy = new(Color.FromRgb(0x20, 0x9E, 0x70));
        private static readonly SolidColorBrush ActiveVis = new(Color.FromRgb(0x1B, 0xB1, 0x94));
        private static readonly SolidColorBrush Inactive = new(Color.FromRgb(0x41, 0x8F, 0xA5));
        private static readonly SolidColorBrush ActiveFg = new(Color.FromRgb(0x16, 0x1E, 0x21));
        private static readonly SolidColorBrush SellHover = new(Color.FromRgb(0xDB, 0x60, 0xB2));
        private static readonly SolidColorBrush BuyHover = new(Color.FromRgb(0x30, 0xB8, 0x88));
        private static readonly SolidColorBrush ActiveHoverVis = new(Color.FromRgb(0x28, 0xC8, 0xA8));
        private static readonly SolidColorBrush InactiveHover = new(Color.FromRgb(0x55, 0xA5, 0xB9));

        private string _orderType = "sell";
        private bool _visible = true;
        private bool _bulkMode;
        private WFInfo.Data.WfmItemInfo _selectedItem;
        private int _maxRank;
        private string[] _subtypes;
        private CancellationTokenSource _searchCts;

        public event Action OrderCreated;

        public PlaceOrderWindow()
        {
            InitializeComponent();
            UpdateTypeButtons();
            UpdateVisButtons();
        }

        private void TypeSell_Click(object sender, RoutedEventArgs e)
        {
            _orderType = "sell";
            UpdateTypeButtons();
        }

        private void TypeBuy_Click(object sender, RoutedEventArgs e)
        {
            _orderType = "buy";
            UpdateTypeButtons();
        }

        private void UpdateTypeButtons()
        {
            SellBtn.Background = _orderType == "sell" ? ActiveSell : Inactive;
            SellBtn.Foreground = ActiveFg;
            BuyBtn.Background = _orderType == "buy" ? ActiveBuy : Inactive;
            BuyBtn.Foreground = ActiveFg;
        }

        private void Visible_Click(object sender, RoutedEventArgs e)
        {
            _visible = true;
            UpdateVisButtons();
        }

        private void Hidden_Click(object sender, RoutedEventArgs e)
        {
            _visible = false;
            UpdateVisButtons();
        }

        private void UpdateVisButtons()
        {
            VisibleBtn.Background = _visible ? ActiveVis : Inactive;
            VisibleBtn.Foreground = ActiveFg;
            HiddenBtn.Background = !_visible ? ActiveVis : Inactive;
            HiddenBtn.Foreground = ActiveFg;
        }

        private void TypeBtn_PointerEntered(object sender, PointerEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn == SellBtn)
                btn.Background = _orderType == "sell" ? SellHover : InactiveHover;
            else
                btn.Background = _orderType == "buy" ? BuyHover : InactiveHover;
        }

        private void TypeBtn_PointerExited(object sender, PointerEventArgs e)
        {
            UpdateTypeButtons();
        }

        private void VisBtn_PointerEntered(object sender, PointerEventArgs e)
        {
            if (sender is not Button btn) return;
            bool isActive = (btn == VisibleBtn && _visible) || (btn == HiddenBtn && !_visible);
            btn.Background = isActive ? ActiveHoverVis : InactiveHover;
        }

        private void VisBtn_PointerExited(object sender, PointerEventArgs e)
        {
            UpdateVisButtons();
        }

        private void ItemSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;
            string query = ItemSearch.Text;

            if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            {
                ItemSearch.ItemsSource = null;
                return;
            }

            Task.Run(() =>
            {
                if (token.IsCancellationRequested) return;
                var results = AppMain.dataBase.SearchItems(query, 6);
                if (token.IsCancellationRequested) return;
                var names = results.Select(r => r.Name).ToList();
                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    ItemSearch.ItemsSource = names;
                });
            }, token);
        }

        private void ItemSearch_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string selected = ItemSearch.SelectedItem as string;
            if (string.IsNullOrEmpty(selected)) return;

            var results = AppMain.dataBase.SearchItems(selected, 1);
            _selectedItem = results.FirstOrDefault();
            if (_selectedItem == null) return;

            if (_selectedItem.MaxRank.HasValue && _selectedItem.MaxRank.Value > 0)
            {
                _maxRank = _selectedItem.MaxRank.Value;
                RankBox.Text = "";
                RankBox.Watermark = $"0-{_maxRank}";
                RankPanel.IsVisible = true;
            }
            else
            {
                RankPanel.IsVisible = false;
            }

            _subtypes = _selectedItem.Subtypes;
            if (_subtypes != null && _subtypes.Length > 0)
            {
                SubtypeBox.ItemsSource = _subtypes.Select(s => char.ToUpper(s[0]) + s.Substring(1)).ToList();
                SubtypeBox.SelectedIndex = 0;
                SubtypePanel.IsVisible = true;
            }
            else
            {
                SubtypePanel.IsVisible = false;
            }

            if (_selectedItem.BulkTradable)
            {
                _bulkMode = false;
                BulkToggle.IsChecked = false;
                BulkPanel.IsVisible = true;
                UpdateBulkLayout();
            }
            else
            {
                BulkPanel.IsVisible = false;
                _bulkMode = false;
                UpdateBulkLayout();
            }

            ErrorText.IsVisible = false;
        }

        private void BulkToggle_Changed(object sender, RoutedEventArgs e)
        {
            _bulkMode = BulkToggle.IsChecked == true;
            UpdateBulkLayout();
        }

        private void UpdateBulkLayout()
        {
            PriceUnitPanel.IsVisible = !_bulkMode;
            PriceBatchPanel.IsVisible = _bulkMode;
        }

        private void BatchField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!int.TryParse(BatchPriceBox.Text, out int batchPrice) || batchPrice < 0)
            {
                UnitPriceLabel.Text = "0";
                return;
            }
            if (!int.TryParse(BatchSizeBox.Text, out int batchSize) || batchSize < 1)
            {
                UnitPriceLabel.Text = "0";
                return;
            }
            UnitPriceLabel.Text = (batchPrice / batchSize).ToString();
        }

        private void AdjustField(TextBox box, int delta, int min, int max)
        {
            int val = int.TryParse(box.Text, out int v) ? v : min;
            val = Math.Clamp(val + delta, min, max);
            box.Text = val.ToString();
        }

        private void RankPlus_Click(object sender, RoutedEventArgs e) => AdjustField(RankBox, 1, 0, _maxRank);
        private void RankMinus_Click(object sender, RoutedEventArgs e) => AdjustField(RankBox, -1, 0, _maxRank);
        private void PricePlus_Click(object sender, RoutedEventArgs e) => AdjustField(PriceBox, 1, 1, 900000);
        private void PriceMinus_Click(object sender, RoutedEventArgs e) => AdjustField(PriceBox, -1, 1, 900000);
        private void QtyPlus_Click(object sender, RoutedEventArgs e) => AdjustField(QuantityBox, 1, 1, 9999);
        private void QtyMinus_Click(object sender, RoutedEventArgs e) => AdjustField(QuantityBox, -1, 1, 9999);
        private void BatchPricePlus_Click(object sender, RoutedEventArgs e) => AdjustField(BatchPriceBox, 1, 1, 900000);
        private void BatchPriceMinus_Click(object sender, RoutedEventArgs e) => AdjustField(BatchPriceBox, -1, 1, 900000);
        private void BatchSizePlus_Click(object sender, RoutedEventArgs e) => AdjustField(BatchSizeBox, 1, 1, 6);
        private void BatchSizeMinus_Click(object sender, RoutedEventArgs e) => AdjustField(BatchSizeBox, -1, 1, 6);

        private async void Post_Click(object sender, RoutedEventArgs e)
        {
            ErrorText.IsVisible = false;

            if (_selectedItem == null)
            {
                ShowError("Please select an item.");
                return;
            }

            int plat;
            int? perTrade = null;

            if (_bulkMode)
            {
                if (!int.TryParse(BatchSizeBox.Text, out int batchSize) || batchSize < 1 || batchSize > 6)
                {
                    ShowError("Batch size must be between 1 and 6.");
                    return;
                }
                if (!int.TryParse(BatchPriceBox.Text, out int batchPrice) || batchPrice < 1 || batchPrice > 900000)
                {
                    ShowError("Price per batch must be between 1 and 900,000.");
                    return;
                }
                plat = batchPrice;
                perTrade = batchSize;
            }
            else
            {
                if (!int.TryParse(PriceBox.Text, out int unitPrice) || unitPrice < 1 || unitPrice > 900000)
                {
                    ShowError("Price must be between 1 and 900,000.");
                    return;
                }
                plat = unitPrice;
                if (_selectedItem.BulkTradable)
                    perTrade = 1;
            }

            if (!int.TryParse(QuantityBox.Text, out int qty) || qty < 1 || qty > 9999)
            {
                ShowError("Quantity must be between 1 and 9,999.");
                return;
            }

            if (_bulkMode && perTrade.HasValue && qty % perTrade.Value != 0)
            {
                ShowError($"Quantity must be a multiple of batch size ({perTrade.Value}).");
                return;
            }

            int? rank = null;
            if (_selectedItem.MaxRank.HasValue && _selectedItem.MaxRank.Value > 0)
            {
                string rankText = RankBox.Text?.Trim();
                if (string.IsNullOrEmpty(rankText))
                    rank = 0;
                else if (!int.TryParse(rankText, out int r) || r < 0 || r > _maxRank)
                {
                    ShowError($"Rank must be between 0 and {_maxRank}.");
                    return;
                }
                else
                    rank = r;
            }

            string subtype = null;
            if (_subtypes != null && _subtypes.Length > 0 && SubtypeBox.SelectedIndex >= 0)
                subtype = _subtypes[SubtypeBox.SelectedIndex];

            PostBtn.IsEnabled = false;
            PostBtn.Content = "Posting...";

            string error = await Task.Run(() =>
                AppMain.dataBase.CreateOrder(_selectedItem.Id, _orderType, plat, qty, _visible, rank, perTrade, subtype));

            PostBtn.IsEnabled = true;
            PostBtn.Content = "Post";

            if (error == null)
            {
                OrderCreated?.Invoke();
                Close();
            }
            else
            {
                ShowError(error);
            }
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.IsVisible = true;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                var pos = e.GetPosition((Visual)sender);
                if (pos.Y > 23) return;
                try { BeginMoveDrag(e); }
                catch (InvalidOperationException) { }
            }
        }
    }
}
