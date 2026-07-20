using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;


namespace WFInfo.Linux.Views
{
    public partial class EditOrderWindow : Window
    {
        private readonly OrderViewModel _vm;
        private bool _updating;
        private bool _visible;
        private int _maxRank = int.MaxValue;
        private bool _isBulkTradable;
        private bool _bulkMode;
        private DispatcherTimer _platToggleTimer;
        private List<(StackPanel unitRow, StackPanel batchRow)> _platTogglePairs = new();
        private bool _platToggleState;

        private const int MaxPlatinum = 900000;
        private const int MaxQuantity = 9999;

        private static readonly SolidColorBrush SellPlatFg = new(Color.FromRgb(0xCB, 0x4A, 0x9E));
        private static readonly SolidColorBrush BuyPlatFg = new(Color.FromRgb(0x20, 0x9E, 0x70));

        private static readonly SolidColorBrush RepGoodFg = new(Color.FromRgb(0x00, 0xA9, 0x6C));
        private static readonly SolidColorBrush RepNeutralFg = new(Color.FromRgb(0x73, 0x90, 0x98));
        private static readonly SolidColorBrush RepBadFg = new(Color.FromRgb(0xA9, 0x41, 0x00));
        private static readonly SolidColorBrush DimFg = new(Color.FromRgb(0x88, 0x88, 0x88));
        private static readonly SolidColorBrush LightFg = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
        private static readonly SolidColorBrush WfmTextFg = new(Color.FromRgb(0xA4, 0xA9, 0xAA));

        private static readonly SolidColorBrush ActiveBg = new(Color.FromRgb(0x1B, 0xB1, 0x94));
        private static readonly SolidColorBrush ActiveHoverBg = new(Color.FromRgb(0x28, 0xC8, 0xA8));
        private static readonly SolidColorBrush InactiveBg = new(Color.FromRgb(0x41, 0x8F, 0xA5));
        private static readonly SolidColorBrush InactiveHoverBg = new(Color.FromRgb(0x55, 0xA5, 0xB9));
        private static readonly SolidColorBrush BtnTextFg = new(Color.FromRgb(0x16, 0x1E, 0x21));
        private static readonly SolidColorBrush SuccessFg = new(Color.FromRgb(0, 200, 0));
        private static readonly SolidColorBrush ErrorFg = new(Color.FromRgb(255, 80, 80));

        public string OrderId => _vm.OrderId;
        public event Action<OrderViewModel> OrderUpdated;

        public EditOrderWindow(OrderViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            _visible = vm.Visible;



            ItemNameText.Text = vm.ItemName;
            TitleText.Text = $"Edit Order - {vm.TypeBadge.ToUpper()}";
            TopOrdersHeader.Text = vm.IsSell ? "Top 5 sell orders" : "Top 5 buy orders";

            _updating = true;
            QuantityBox.Text = vm.Quantity.ToString();
            if (vm.Rank.HasValue)
            {
                RankRow.IsVisible = true;
                RankBox.Text = vm.Rank.Value.ToString();
            }
            if (vm.AvailableSubtypes != null && vm.AvailableSubtypes.Length > 0)
            {
                SubtypeRow.IsVisible = true;
                SubtypeBox.ItemsSource = vm.AvailableSubtypes.Select(s => char.ToUpper(s[0]) + s.Substring(1)).ToList();
                if (!string.IsNullOrEmpty(vm.Subtype))
                {
                    int idx = Array.FindIndex(vm.AvailableSubtypes, s => s.Equals(vm.Subtype, StringComparison.OrdinalIgnoreCase));
                    SubtypeBox.SelectedIndex = idx >= 0 ? idx : 0;
                }
                else
                    SubtypeBox.SelectedIndex = 0;
            }

            _isBulkTradable = vm.BulkTradable;
            if (vm.BulkTradable)
            {
                PerTradeRow.IsVisible = true;
                if (vm.PerTrade.HasValue && vm.PerTrade.Value > 1)
                {
                    _bulkMode = true;
                    BulkToggle.IsChecked = true;
                    BatchPriceBox.Text = vm.Platinum.ToString();
                    BatchSizeBox.Text = vm.PerTrade.Value.ToString();
                    int unitPrice = vm.Platinum / vm.PerTrade.Value;
                    PriceBox.Text = unitPrice.ToString();
                    EditUnitPriceLabel.Text = unitPrice.ToString();
                    PriceUnitPanel.IsVisible = false;
                    PriceBatchPanel.IsVisible = true;
                }
                else
                {
                    PriceBox.Text = vm.Platinum.ToString();
                }
            }
            else
            {
                PriceBox.Text = vm.Platinum.ToString();
            }
            _updating = false;

            UpdateVisibilityButtons();
            LoadItemInfoThenListings();
        }

        private void UpdateVisibilityButtons()
        {
            VisibleBtn.Background = _visible ? ActiveBg : InactiveBg;
            HiddenBtn.Background = _visible ? InactiveBg : ActiveBg;
        }

        private void VisBtn_PointerEntered(object sender, PointerEventArgs e)
        {
            if (sender is not Button btn) return;
            bool isActive = (btn == VisibleBtn && _visible) || (btn == HiddenBtn && !_visible);
            btn.Background = isActive ? ActiveHoverBg : InactiveHoverBg;
        }

        private void VisBtn_PointerExited(object sender, PointerEventArgs e)
        {
            UpdateVisibilityButtons();
        }

        private void LoadItemInfoThenListings()
        {
            if (string.IsNullOrEmpty(_vm.UrlSlug)) return;

            Task.Run(async () =>
            {
                try
                {
                    var itemData = await AppMain.dataBase.GetItemInfoBySlug(_vm.UrlSlug);
                    if (itemData != null)
                    {
                        int mr = itemData.Value<int?>("maxRank") ?? -1;
                        if (mr >= 0)
                            _maxRank = mr;
                    }
                }
                catch (Exception ex)
                {
                    AppMain.AddLog($"EditOrder: failed to load item info: {ex.Message}");
                }

                try
                {
                    int? rank = _vm.Rank;
                    int? rankLt = null;
                    if (rank.HasValue && _maxRank < int.MaxValue && rank.Value != _maxRank)
                    {
                        if (_vm.IsSell)
                            rank = 0;
                        else
                        {
                            rank = null;
                            rankLt = _maxRank;
                        }
                    }
                    string sub = _vm.Subtype;
                    var results = await AppMain.dataBase.GetTopListingsBySlug(_vm.UrlSlug, rank, rankLt, sub);
                    Dispatcher.UIThread.Post(() => PopulateTopListings(results));
                }
                catch (Exception ex)
                {
                    AppMain.AddLog($"EditOrder: failed to load top listings: {ex.Message}");
                }
            });
        }

        private void SubtypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_updating)
                LoadTopListings();
        }

        private string GetSelectedSubtype()
        {
            if (!SubtypeRow.IsVisible || _vm.AvailableSubtypes == null || SubtypeBox.SelectedIndex < 0)
                return null;
            return _vm.AvailableSubtypes[SubtypeBox.SelectedIndex];
        }

        private void LoadTopListings()
        {
            if (string.IsNullOrEmpty(_vm.UrlSlug)) return;

            int? rank = null;
            int? rankLt = null;
            if (RankRow.IsVisible && int.TryParse(RankBox.Text, out int r))
            {
                // Maxed = show max rank orders. WTS: else rank 0. WTB: else rankLt=maxRank.
                if (_maxRank < int.MaxValue && r == _maxRank)
                    rank = r;
                else if (_vm.IsSell)
                    rank = 0;
                else
                    rankLt = _maxRank;
            }

            string subtype = GetSelectedSubtype();

            Task.Run(async () =>
            {
                try
                {
                    var results = await AppMain.dataBase.GetTopListingsBySlug(_vm.UrlSlug, rank, rankLt, subtype);
                    Dispatcher.UIThread.Post(() => PopulateTopListings(results));
                }
                catch (Exception ex)
                {
                    AppMain.AddLog($"EditOrder: failed to load top listings: {ex.Message}");
                }
            });
        }

        private void PopulateTopListings(Newtonsoft.Json.Linq.JObject results)
        {
            if (results == null) return;

            var orders = results["data"]?[_vm.IsSell ? "sell" : "buy"];
            TopOrdersPanel.Children.Clear();
            TopOrdersPanel.RowDefinitions.Clear();
            TopOrdersPanel.ColumnDefinitions.Clear();
            MaxRankHeader.IsVisible = false;

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
            var platPerBatchIcon = Application.Current.FindResource("IconPlatPerBatch") as StreamGeometry;
            var cubesIcon = Application.Current.FindResource("IconCubes") as StreamGeometry;
            var smileIcon = Application.Current.FindResource("IconSmile") as StreamGeometry;
            var mehIcon = Application.Current.FindResource("IconMeh") as StreamGeometry;
            var frownIcon = Application.Current.FindResource("IconFrown") as StreamGeometry;
            var platBrush = _vm.IsSell ? SellPlatFg : BuyPlatFg;
            bool hasMaxRank = false;
            _platTogglePairs.Clear();
            StopPlatToggleTimer();

            // Check if any order has rank info
            bool hasRankColumn = false;
            foreach (var item in orders)
            {
                var mr = item.Value<int?>("rank") ?? item.Value<int?>("mod_rank");
                if (mr.HasValue) { hasRankColumn = true; break; }
            }

            bool hasSubtypeColumn = _vm.AvailableSubtypes != null && _vm.AvailableSubtypes.Length > 0;

            // Equal columns: Rep | Plat | (Rank or Subtype) | Qty
            int midCols = (hasRankColumn ? 1 : 0) + (hasSubtypeColumn ? 1 : 0);
            int colCount = 3 + midCols;
            for (int c = 0; c < colCount; c++)
                TopOrdersPanel.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

            int subtypeCol = hasRankColumn ? 3 : 2;
            int qtyCol = 2 + midCols;
            int rowIdx = 0;

            // Find longest plat string to align icons
            int maxPlatLen = 0;
            int scanCount = 0;
            foreach (var o in orders)
            {
                if (scanCount >= 5) break;
                int p = o.Value<int?>("platinum") ?? 0;
                int len = p.ToString("N0").Length;
                if (len > maxPlatLen) maxPlatLen = len;
                scanCount++;
            }
            double platTextWidth = maxPlatLen * 7.0;

            foreach (var item in orders)
            {
                if (rowIdx >= 5) break;

                int rep = item["user"]?.Value<int?>("reputation") ?? 0;
                int plat = item.Value<int?>("platinum") ?? 0;
                int qty = item.Value<int?>("quantity") ?? 1;
                var modRank = item.Value<int?>("rank") ?? item.Value<int?>("mod_rank");

                TopOrdersPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                // 1. Reputation (number + smiley/meh icon)
                var repFg = rep >= 5 ? RepGoodFg : (rep < -5 ? RepBadFg : RepNeutralFg);
                var repPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 2,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 2)
                };
                repPanel.Children.Add(new TextBlock
                {
                    Text = rep.ToString(), Foreground = repFg, FontSize = 11
                });
                var faceIcon = rep >= 5 ? smileIcon : (rep < -5 ? frownIcon : mehIcon);
                if (faceIcon != null)
                    repPanel.Children.Add(new PathIcon
                    {
                        Data = faceIcon, Foreground = repFg,
                        Width = 11, Height = 11
                    });
                Grid.SetRow(repPanel, rowIdx);
                Grid.SetColumn(repPanel, 0);
                TopOrdersPanel.Children.Add(repPanel);

                // 2. Platinum (API value is batch price for bulk orders)
                int orderPerTrade = item.Value<int?>("perTrade") ?? 0;
                bool isBulkOrder = _isBulkTradable && orderPerTrade > 0;
                int batchPrice = plat;
                int unitPrice = isBulkOrder ? plat / orderPerTrade : plat;
                var platContainer = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = hasRankColumn ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2)
                };

                // Unit price row
                var unitRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                unitRow.Children.Add(new TextBlock
                {
                    Text = unitPrice.ToString("N0"), FontWeight = FontWeight.Bold,
                    Foreground = platBrush, FontSize = 11,
                    MinWidth = platTextWidth,
                    TextAlignment = TextAlignment.Right
                });
                var unitIcon = isBulkOrder ? platPerBatchIcon : platIcon;
                if (unitIcon != null)
                    unitRow.Children.Add(new PathIcon
                    {
                        Data = unitIcon, Foreground = platBrush,
                        Width = 10, Height = 10
                    });
                platContainer.Children.Add(unitRow);

                if (isBulkOrder)
                {
                    // Batch price row
                    var batchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, IsVisible = false };
                    batchRow.Children.Add(new TextBlock
                    {
                        Text = batchPrice.ToString("N0"), FontWeight = FontWeight.Bold,
                        Foreground = WfmTextFg, FontSize = 11,
                        MinWidth = platTextWidth,
                        TextAlignment = TextAlignment.Right
                    });
                    if (platIcon != null)
                        batchRow.Children.Add(new PathIcon
                        {
                            Data = platIcon, Foreground = WfmTextFg,
                            Width = 10, Height = 10
                        });
                    batchRow.Children.Add(new TextBlock
                    {
                        Text = "/ trade", Foreground = WfmTextFg, FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0)
                    });
                    platContainer.Children.Add(batchRow);
                    _platTogglePairs.Add((unitRow, batchRow));
                }

                Grid.SetRow(platContainer, rowIdx);
                Grid.SetColumn(platContainer, 1);
                TopOrdersPanel.Children.Add(platContainer);

                // 3. Rank "X of Y"
                if (hasRankColumn && modRank.HasValue)
                {
                    int maxR = _maxRank < int.MaxValue ? _maxRank : modRank.Value;
                    if (modRank.Value == maxR) hasMaxRank = true;
                    var rankText = new TextBlock
                    {
                        Text = $"{modRank.Value} of {maxR}",
                        Foreground = LightFg, FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 2)
                    };
                    Grid.SetRow(rankText, rowIdx);
                    Grid.SetColumn(rankText, 2);
                    TopOrdersPanel.Children.Add(rankText);
                }

                // 3b. Subtype
                if (hasSubtypeColumn)
                {
                    string sub = item.Value<string>("subtype");
                    if (!string.IsNullOrEmpty(sub))
                    {
                        string display = char.ToUpper(sub[0]) + sub.Substring(1);
                        var subtypeText = new TextBlock
                        {
                            Text = display,
                            Foreground = LightFg, FontSize = 11,
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 2)
                        };
                        Grid.SetRow(subtypeText, rowIdx);
                        Grid.SetColumn(subtypeText, subtypeCol);
                        TopOrdersPanel.Children.Add(subtypeText);
                    }
                }

                // 4. Quantity
                var qtyPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 2,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 2)
                };
                qtyPanel.Children.Add(new TextBlock
                {
                    Text = qty.ToString(), Foreground = LightFg, FontSize = 11
                });
                if (cubesIcon != null)
                    qtyPanel.Children.Add(new PathIcon
                    {
                        Data = cubesIcon, Foreground = LightFg,
                        Width = 10, Height = 10
                    });
                Grid.SetRow(qtyPanel, rowIdx);
                Grid.SetColumn(qtyPanel, qtyCol);
                TopOrdersPanel.Children.Add(qtyPanel);

                rowIdx++;
            }

            if (hasMaxRank)
                MaxRankHeader.IsVisible = true;

            if (_platTogglePairs.Count > 0)
                StartPlatToggleTimer();
        }

        private void StartPlatToggleTimer()
        {
            StopPlatToggleTimer();
            _platToggleState = false;
            _platToggleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _platToggleTimer.Tick += (_, _) =>
            {
                _platToggleState = !_platToggleState;
                foreach (var (unitRow, batchRow) in _platTogglePairs)
                {
                    unitRow.IsVisible = !_platToggleState;
                    batchRow.IsVisible = _platToggleState;
                }
            };
            _platToggleTimer.Start();
        }

        private void StopPlatToggleTimer()
        {
            _platToggleTimer?.Stop();
            _platToggleTimer = null;
        }

        private void Visible_Click(object sender, RoutedEventArgs e)
        {
            _visible = true;
            UpdateVisibilityButtons();
        }

        private void Hidden_Click(object sender, RoutedEventArgs e)
        {
            _visible = false;
            UpdateVisibilityButtons();
        }



        private void AdjustField(TextBox box, int delta, int min, int max)
        {
            int val = int.TryParse(box.Text, out int v) ? v : min;
            val = Math.Clamp(val + delta, min, max);
            _updating = true;
            box.Text = val.ToString();
            _updating = false;
        }

        private void RankPlus_Click(object sender, RoutedEventArgs e) => AdjustField(RankBox, 1, 0, _maxRank);
        private void RankMinus_Click(object sender, RoutedEventArgs e) => AdjustField(RankBox, -1, 0, _maxRank);
        private void PricePlus_Click(object sender, RoutedEventArgs e) => AdjustField(PriceBox, 1, 1, MaxPlatinum);
        private void PriceMinus_Click(object sender, RoutedEventArgs e) => AdjustField(PriceBox, -1, 1, MaxPlatinum);
        private void QtyPlus_Click(object sender, RoutedEventArgs e) => AdjustField(QuantityBox, 1, 1, MaxQuantity);
        private void QtyMinus_Click(object sender, RoutedEventArgs e) => AdjustField(QuantityBox, -1, 1, MaxQuantity);
        private void BulkToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            _bulkMode = BulkToggle.IsChecked == true;
            PriceUnitPanel.IsVisible = !_bulkMode;
            PriceBatchPanel.IsVisible = _bulkMode;
            if (_bulkMode)
            {
                // Switching to bulk
                if (!string.IsNullOrEmpty(PriceBox.Text))
                {
                    BatchPriceBox.Text = PriceBox.Text;
                    BatchSizeBox.Text = "1";
                }
            }
            else
            {
                // Switching to non-bulk
                if (int.TryParse(BatchPriceBox.Text, out int bp) && bp > 0
                    && int.TryParse(BatchSizeBox.Text, out int bs) && bs > 0)
                {
                    PriceBox.Text = (bp / bs).ToString();
                }
            }
        }

        private void BatchPricePlus_Click(object sender, RoutedEventArgs e) => AdjustField(BatchPriceBox, 1, 1, MaxPlatinum);
        private void BatchPriceMinus_Click(object sender, RoutedEventArgs e) => AdjustField(BatchPriceBox, -1, 1, MaxPlatinum);
        private void BatchSizePlus_Click(object sender, RoutedEventArgs e) => AdjustField(BatchSizeBox, 1, 1, 6);
        private void BatchSizeMinus_Click(object sender, RoutedEventArgs e) => AdjustField(BatchSizeBox, -1, 1, 6);

        private void BatchField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updating) return;
            if (!int.TryParse(BatchPriceBox.Text, out int batchPrice) || batchPrice < 0)
            {
                EditUnitPriceLabel.Text = "0";
                return;
            }
            if (!int.TryParse(BatchSizeBox.Text, out int batchSize) || batchSize < 1)
            {
                EditUnitPriceLabel.Text = "0";
                return;
            }
            EditUnitPriceLabel.Text = (batchPrice / batchSize).ToString();
        }

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

        private void Update_Click(object sender, RoutedEventArgs e)
        {
            int plat;
            int perTrade;

            if (_bulkMode)
            {
                if (!int.TryParse(BatchSizeBox.Text, out int batchSize) || batchSize < 1 || batchSize > 6)
                {
                    StatusText.Text = "Batch size must be 1-6";
                    StatusText.Foreground = ErrorFg;
                    return;
                }
                if (!int.TryParse(BatchPriceBox.Text, out int batchPrice) || batchPrice <= 0)
                {
                    StatusText.Text = "Invalid batch price";
                    StatusText.Foreground = ErrorFg;
                    return;
                }
                plat = batchPrice;
                perTrade = batchSize;
            }
            else
            {
                if (!int.TryParse(PriceBox.Text, out int unitPrice) || unitPrice <= 0)
                {
                    StatusText.Text = "Invalid price";
                    StatusText.Foreground = ErrorFg;
                    return;
                }
                plat = unitPrice;
                perTrade = 1; // clear bulk mode on server
            }

            if (!int.TryParse(QuantityBox.Text, out int qty) || qty <= 0)
            {
                StatusText.Text = "Invalid quantity";
                StatusText.Foreground = ErrorFg;
                return;
            }

            if (_bulkMode && perTrade > 1 && qty % perTrade != 0)
            {
                StatusText.Text = $"Quantity must be a multiple of {perTrade}";
                StatusText.Foreground = ErrorFg;
                return;
            }

            int? rank = null;
            if (RankRow.IsVisible && !string.IsNullOrEmpty(RankBox.Text))
            {
                if (int.TryParse(RankBox.Text, out int r))
                    rank = r;
            }

            string subtype = null;
            if (SubtypeRow.IsVisible && _vm.AvailableSubtypes != null && SubtypeBox.SelectedIndex >= 0)
                subtype = _vm.AvailableSubtypes[SubtypeBox.SelectedIndex];

            UpdateBtn.IsEnabled = false;
            UpdateBtn.Content = "...";
            StatusText.Text = "";

            Task.Run(async () =>
            {
                bool success = await AppMain.dataBase.UpdateListing(_vm.OrderId, plat, qty, _visible, rank, _isBulkTradable ? perTrade : null, subtype);
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateBtn.IsEnabled = true;
                    UpdateBtn.Content = "Update";
                    LoadTopListings();
                    if (success)
                    {
                        StatusText.Text = "Updated successfully";
                        StatusText.Foreground = SuccessFg;

                        var updated = new OrderViewModel
                        {
                            Platinum = plat,
                            Quantity = qty,
                            Visible = _visible,
                            Rank = rank ?? _vm.Rank,
                            PerTrade = perTrade,
                            Subtype = subtype ?? _vm.Subtype
                        };
                        OrderUpdated?.Invoke(updated);
                    }
                    else
                    {
                        StatusText.Text = "Update failed";
                        StatusText.Foreground = ErrorFg;
                    }
                });
            });
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

        protected override void OnClosed(EventArgs e)
        {
            StopPlatToggleTimer();
            base.OnClosed(e);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}