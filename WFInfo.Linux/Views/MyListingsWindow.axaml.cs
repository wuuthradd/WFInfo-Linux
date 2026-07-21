using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Newtonsoft.Json.Linq;


namespace WFInfo.Linux.Views
{
    public partial class MyListingsWindow : Window
    {
        private static MyListingsWindow _instance;
        private readonly ObservableCollection<OrderViewModel> _allOrders = new();
        private readonly ObservableCollection<OrderViewModel> _filteredOrders = new();

        public static void ReloadIfOpen()
        {
            if (_instance is { IsVisible: true })
                _instance.LoadOrders();
        }

        public static void DecrementOrderIfOpen(string orderId, int amount)
        {
            if (_instance is not { IsVisible: true }) return;
            foreach (var vm in _instance._allOrders)
            {
                if (vm.OrderId != orderId) continue;
                if (vm.Quantity > amount)
                    vm.Quantity -= amount;
                else
                {
                    _instance._allOrders.Remove(vm);
                    _instance._filteredOrders.Remove(vm);
                    _instance.CountText.Text = $"{_instance._allOrders.Count} orders";
                }
                break;
            }
        }

        public MyListingsWindow()
        {
            _instance = this;
            InitializeComponent();
            ListingsPanel.ItemsSource = _filteredOrders;
            UpdateSortButtons();
        }

        private void ShowApiError()
        {
            WfmErrorOverlay.IsVisible = true;
            LoadingOverlay.IsVisible = false;
            RefreshBtn.IsEnabled = false;
            PlaceOrderBtn.IsEnabled = false;
            HistoryBtn.IsEnabled = false;
            _editWindow?.Close();
            _historyWindow?.Close();
            _placeOrderWindow?.Close();
        }

        private void HideApiError()
        {
            WfmErrorOverlay.IsVisible = false;
            RefreshBtn.IsEnabled = true;
            PlaceOrderBtn.IsEnabled = true;
            HistoryBtn.IsEnabled = true;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property != WindowStateProperty) return;

            if (WindowState == WindowState.Minimized)
            {
                _editWindow?.Hide();
                _historyWindow?.Hide();
                _placeOrderWindow?.Hide();
            }
            else if (WindowState == WindowState.Normal)
            {
                if (_editWindow != null) { _editWindow.Show(); _editWindow.Activate(); }
                if (_historyWindow != null) { _historyWindow.Show(); _historyWindow.Activate(); }
                if (_placeOrderWindow != null) { _placeOrderWindow.Show(); _placeOrderWindow.Activate(); }
            }
        }

        public void LoadOrders()
        {
            HideApiError();
            LoadingOverlay.IsVisible = true;
            RefreshBtn.IsEnabled = false;

            Task.Run(async () =>
            {
                try
                {
                    var orders = await AppMain.dataBase.GetAllMyOrders();

                    if (orders == null)
                    {
                        Dispatcher.UIThread.Post(() => ShowApiError());
                        return;
                    }

                    // Wait for item names to be loaded if needed
                    for (int i = 0; i < 20 && !AppMain.dataBase.HasItemNames; i++)
                        await Task.Delay(500);

                    var vms = new List<OrderViewModel>();
                    foreach (var order in orders)
                    {
                        string itemId = (string)order["itemId"];
                        string name = AppMain.dataBase.ItemIdToDisplayName(itemId) ?? itemId;
                        string slug = AppMain.dataBase.ItemIdToUrlSlug(itemId);

                        DateTime.TryParse(order.Value<string>("createdAt"), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var createdAt);
                        DateTime.TryParse(order.Value<string>("updatedAt"), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var updatedAt);

                        var vm = new OrderViewModel
                        {
                            OrderId = (string)order["id"],
                            ItemId = itemId,
                            ItemName = name,
                            UrlSlug = slug,
                            Type = (string)order["type"] ?? "sell",
                            Platinum = order.Value<int>("platinum"),
                            Quantity = order.Value<int>("quantity"),
                            Visible = order.Value<bool?>("visible") ?? true,
                            Rank = order["rank"]?.Type == JTokenType.Null ? null : order.Value<int?>("rank"),
                            PerTrade = order["perTrade"]?.Type == JTokenType.Null ? null : order.Value<int?>("perTrade"),
                            Subtype = order.Value<string>("subtype"),
                            BulkTradable = AppMain.dataBase.GetItemInfoById(itemId)?.BulkTradable ?? false,
                            MaxRank = AppMain.dataBase.GetItemInfoById(itemId)?.MaxRank,
                            AvailableSubtypes = AppMain.dataBase.GetItemInfoById(itemId)?.Subtypes,
                            Vaulted = AppMain.dataBase.GetItemInfoById(itemId)?.Vaulted ?? false,
                            CreatedAt = createdAt,
                            UpdatedAt = updatedAt
                        };
                        vms.Add(vm);
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        _allOrders.Clear();
                        foreach (var vm in vms)
                            _allOrders.Add(vm);
                        ApplyFilter();
                        LoadingOverlay.IsVisible = false;
                        RefreshBtn.IsEnabled = true;
                        CountText.Text = $"{_allOrders.Count} orders";
                    });
                }
                catch (Exception ex)
                {
                    AppMain.AddLog($"MyListings: failed to load orders: {ex.Message}");
                    Dispatcher.UIThread.Post(() => ShowApiError());
                }
            });
        }

        private string _sortField = null;
        private bool _sortAsc = true;

        private static readonly SolidColorBrush SortActiveFg = new(Color.FromRgb(0xB1, 0xD0, 0xD9));
        private static readonly SolidColorBrush SortInactiveFg = new(Color.FromRgb(0x88, 0x88, 0x88));
        

        private void ApplyFilter()
        {
            _filteredOrders.Clear();
            string typeFilter = TypeFilter.SelectedIndex switch
            {
                1 => "sell",
                2 => "buy",
                _ => null
            };
            string search = SearchBox.Text?.Trim().ToLower(CultureInfo.InvariantCulture) ?? "";

            int.TryParse(MinPriceBox?.Text, out int minPrice);
            int maxPrice = int.MaxValue;
            if (MaxPriceBox != null && int.TryParse(MaxPriceBox.Text, out int mp) && mp > 0)
                maxPrice = mp;

            var filtered = new List<OrderViewModel>();
            foreach (var order in _allOrders)
            {
                if (typeFilter != null && order.Type != typeFilter) continue;
                if (search.Length > 0 && !(order.ItemName?.ToLower(CultureInfo.InvariantCulture).Contains(search) ?? false)) continue;
                if (order.Platinum < minPrice || order.Platinum > maxPrice) continue;
                filtered.Add(order);
            }

            IEnumerable<OrderViewModel> sorted = _sortField switch
            {
                "name" => _sortAsc ? filtered.OrderBy(v => v.ItemName, StringComparer.OrdinalIgnoreCase)
                                   : filtered.OrderByDescending(v => v.ItemName, StringComparer.OrdinalIgnoreCase),
                "price" => _sortAsc ? filtered.OrderBy(v => v.Platinum) : filtered.OrderByDescending(v => v.Platinum),
                "quantity" => _sortAsc ? filtered.OrderBy(v => v.Quantity) : filtered.OrderByDescending(v => v.Quantity),
                "updated" => _sortAsc ? filtered.OrderBy(v => v.UpdatedAt) : filtered.OrderByDescending(v => v.UpdatedAt),
                "created" => _sortAsc ? filtered.OrderBy(v => v.CreatedAt) : filtered.OrderByDescending(v => v.CreatedAt),
                _ => filtered.OrderBy(v => v.CreatedAt), // default: creation date ascending
            };

            foreach (var vm in sorted)
                _filteredOrders.Add(vm);

            EmptyOverlay.IsVisible = _filteredOrders.Count == 0 && _allOrders.Count > 0;

            CountText.Text = typeFilter != null || search.Length > 0 || minPrice > 0 || maxPrice < int.MaxValue
                ? $"{_filteredOrders.Count} / {_allOrders.Count} orders"
                : $"{_allOrders.Count} orders";
        }

        private void UpdateSortButtons()
        {
            foreach (var btn in new[] { SortName, SortPrice, SortQty, SortUpdated, SortCreated })
            {
                string tag = (string)btn.Tag;
                bool active = tag == _sortField;
                btn.Foreground = active ? SortActiveFg : SortInactiveFg;
                if (active)
                    btn.Content = btn.Content.ToString().TrimEnd(' ', '\u25b2', '\u25bc')
                                 + (_sortAsc ? " \u25b2" : " \u25bc");
                else
                    btn.Content = btn.Content.ToString().TrimEnd(' ', '\u25b2', '\u25bc');
            }
        }

        private void Sort_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string field) return;
            if (_sortField == field)
            {
                if (_sortAsc)
                    _sortAsc = false;
                else
                {
                    _sortField = null;
                    _sortAsc = true;
                }
            }
            else
            {
                _sortField = field;
                _sortAsc = true;
            }
            UpdateSortButtons();
            ApplyFilter();
        }

        private void TypeFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (SearchBox != null) ApplyFilter();
        }
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
        private void PriceFilter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

        private static readonly SolidColorBrush DimmedFg = new(Color.FromRgb(0x40, 0x86, 0x98));
        private static readonly SolidColorBrush NormalFg = new(Color.FromRgb(0xB1, 0xD0, 0xD9));

        private void VisibilityIcon_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is PathIcon pi && pi.Tag is OrderViewModel vm
                && pi.Parent is Avalonia.Controls.StackPanel sp
                && sp.Parent is Button btn)
            {
                UpdateVisibilityButton(btn, vm.Visible);
            }
        }

        private void UpdateVisibilityButton(Button btn, bool visible)
        {
            var sp = btn.Content as Avalonia.Controls.StackPanel;
            if (sp == null) return;
            var pi = sp.Children.OfType<PathIcon>().FirstOrDefault();
            var tb = sp.Children.OfType<TextBlock>().FirstOrDefault();
            if (pi != null)
            {
                pi.Data = (Geometry)this.FindResource(visible ? "IconEye" : "IconEyeSlash");
                pi.Foreground = visible ? NormalFg : DimmedFg;
            }
            if (tb != null)
                tb.Foreground = visible ? NormalFg : DimmedFg;
        }
        private TransactionHistoryWindow _historyWindow;
        private CancellationTokenSource _historyRefreshCts;
        private bool _historyRefreshedOnce;

        private void ScheduleHistoryRefresh()
        {
            if (_historyWindow is not { IsVisible: true }) return;

            if (!_historyRefreshedOnce)
            {
                _historyRefreshedOnce = true;
                _historyWindow.LoadTransactions();
                return;
            }

            _historyRefreshCts?.Cancel();
            _historyRefreshCts = new CancellationTokenSource();
            var token = _historyRefreshCts.Token;
            Task.Delay(3000, token).ContinueWith(_ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_historyWindow is { IsVisible: true })
                        _historyWindow.LoadTransactions();
                });
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        private void History_Click(object sender, RoutedEventArgs e)
        {
            if (_historyWindow != null && _historyWindow.IsVisible)
            {
                _historyWindow.Height = Height;
                _historyWindow.Position = new PixelPoint(Position.X + (int)Width, Position.Y);
                _historyWindow.Activate();
                return;
            }
            _historyWindow = new TransactionHistoryWindow { Height = Height };
            _historyRefreshedOnce = false;
            _historyWindow.Closed += (_, _) => { _historyWindow = null; _historyRefreshedOnce = false; };
            _historyWindow.Show();
            _historyWindow.Position = new PixelPoint(Position.X + (int)Width, Position.Y);
        }

        private void Trades_Click(object sender, RoutedEventArgs e) => App.ShowTradeDoneWindow();

        private void Refresh_Click(object sender, RoutedEventArgs e) => LoadOrders();

        private void Sold_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not OrderViewModel vm) return;
            btn.IsEnabled = false;
            var origContent = btn.Content;
            btn.Content = "...";

            int closeAmount = vm.PerTrade.HasValue && vm.PerTrade.Value > 1 ? vm.PerTrade.Value : 1;
            Task.Run(async () =>
            {
                bool success = await AppMain.dataBase.CloseOrder(vm.OrderId, closeAmount);

                Dispatcher.UIThread.Post(() =>
                {
                    if (success)
                    {
                        if (vm.Quantity > closeAmount)
                        {
                            vm.Quantity -= closeAmount;
                            btn.Content = origContent;
                            btn.IsEnabled = true;
                        }
                        else
                        {
                            _allOrders.Remove(vm);
                            _filteredOrders.Remove(vm);
                            CountText.Text = $"{_allOrders.Count} orders";
                        }
                        ScheduleHistoryRefresh();
                    }
                    else
                    {
                        ShowApiError();
                    }
                });
            });
        }

        private EditOrderWindow _editWindow;

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not OrderViewModel vm) return;

            if (_editWindow != null && _editWindow.IsVisible)
            {
                if (_editWindow.OrderId == vm.OrderId)
                {
                    _editWindow.Activate();
                    return;
                }
                _editWindow.Close();
            }

            _editWindow = new EditOrderWindow(vm);
            _editWindow.OrderUpdated += updatedVm =>
            {
                vm.Platinum = updatedVm.Platinum;
                vm.Quantity = updatedVm.Quantity;
                vm.Visible = updatedVm.Visible;
                vm.Rank = updatedVm.Rank;
                vm.PerTrade = updatedVm.PerTrade;
                vm.Subtype = updatedVm.Subtype;
            };
            _editWindow.Closed += (_, _) => _editWindow = null;
            _editWindow.Show();
        }

        private void PlusOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not OrderViewModel vm) return;
            btn.IsEnabled = false;
            var origContent = btn.Content;
            btn.Content = "...";

            int addAmount = vm.PerTrade.HasValue && vm.PerTrade.Value > 1 ? vm.PerTrade.Value : 1;
            Task.Run(async () =>
            {
                bool success = await AppMain.dataBase.UpdateListing(vm.OrderId, vm.Platinum, vm.Quantity + addAmount, vm.Visible);
                Dispatcher.UIThread.Post(() =>
                {
                    if (success)
                    {
                        vm.Quantity += addAmount;
                        btn.Content = origContent;
                        btn.IsEnabled = true;
                    }
                    else
                    {
                        ShowApiError();
                    }
                });
            });
        }

        private void Visibility_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not OrderViewModel vm) return;
            btn.IsEnabled = false;

            bool newVisible = !vm.Visible;
            Task.Run(async () =>
            {
                bool success = await AppMain.dataBase.UpdateListing(vm.OrderId, vm.Platinum, vm.Quantity, newVisible);
                Dispatcher.UIThread.Post(() =>
                {
                    if (success)
                    {
                        vm.Visible = newVisible;
                        UpdateVisibilityButton(btn, newVisible);
                        btn.IsEnabled = true;
                    }
                    else
                    {
                        ShowApiError();
                    }
                });
            });
        }

        private void DeleteAsk_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button trashBtn || trashBtn.Tag is not OrderViewModel vm) return;

            var buttonsStack = trashBtn.Parent?.Parent as StackPanel;
            var panel = buttonsStack?.Parent as Panel;
            if (buttonsStack == null || panel == null) return;

            buttonsStack.IsVisible = false;
            var label = new TextBlock
            {
                Text = "Are you sure?",
                Foreground = new SolidColorBrush(Color.Parse("#FFB1D0D9")),
                FontSize = 14,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            var yesBtn = new Button { Content = "Yes", Tag = vm };
            yesBtn.Classes.Add("confirmYes");

            var noBtn = new Button { Content = "No" };
            noBtn.Classes.Add("confirmNo");

            var row = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            row.Children.Add(label);
            row.Children.Add(yesBtn);
            row.Children.Add(noBtn);

            panel.Children.Add(row);

            yesBtn.Click += DeleteConfirm_Click;
            noBtn.Click += (_, _) =>
            {
                panel.Children.Remove(row);
                buttonsStack.IsVisible = true;
            };
        }

        private void DeleteConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button yesBtn || yesBtn.Tag is not OrderViewModel vm) return;

            var row = yesBtn.Parent as StackPanel;
            var panel = row?.Parent as Panel;
            yesBtn.IsEnabled = false;
            yesBtn.Content = "...";

            Task.Run(async () =>
            {
                bool success = await AppMain.dataBase.DeleteOrder(vm.OrderId);
                Dispatcher.UIThread.Post(() =>
                {
                    if (success)
                    {
                        if (_editWindow != null && _editWindow.OrderId == vm.OrderId)
                            _editWindow.Close();
                        _allOrders.Remove(vm);
                        _filteredOrders.Remove(vm);
                        CountText.Text = $"{_allOrders.Count} orders";
                    }
                    else
                    {
                        ShowApiError();
                    }
                });
            });
        }

        private void ItemName_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (sender is TextBlock tb && tb.Tag is OrderViewModel vm && !string.IsNullOrEmpty(vm.UrlSlug))
            {
                string type = vm.Type == "buy" ? "buy" : "sell";
                string url = $"https://warframe.market/items/{vm.UrlSlug}?type={type}";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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

        private static readonly SolidColorBrush SellBadgeColor = new(Color.FromRgb(0xCB, 0x4A, 0x9E));
        private static readonly SolidColorBrush BuyBadgeColor = new(Color.FromRgb(0x20, 0x9E, 0x70));
        private static readonly SolidColorBrush SellBadgeBg = new(Color.FromArgb(0x40, 0xCB, 0x4A, 0x9E));
        private static readonly SolidColorBrush BuyBadgeBg = new(Color.FromArgb(0x40, 0x20, 0x9E, 0x70));

        private void PriceRow_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not StackPanel sp || sp.Tag is not OrderViewModel vm) return;
            BuildPriceRow(sp, vm);

            vm.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName is nameof(OrderViewModel.Platinum)
                    or nameof(OrderViewModel.PerTrade)
                    or nameof(OrderViewModel.IsBulk))
                {
                    BuildPriceRow(sp, vm);
                }
            };
        }

        private void BuildPriceRow(StackPanel sp, OrderViewModel vm)
        {
            sp.Children.Clear();
            sp.Spacing = 2;

            var priceColor = vm.IsSell ? SellBadgeColor : BuyBadgeColor;
            var platIcon = (Geometry)this.FindResource("IconPlatinum");
            var platBatchIcon = (Geometry)this.FindResource("IconPlatPerBatch");
            var batchTextBrush = new SolidColorBrush(Color.FromRgb(0xA4, 0xA9, 0xAA)); // --color_text

            if (vm.IsBulk)
            {
                int perTrade = vm.PerTrade ?? 1;
                int batchPrice = vm.Platinum;
                double unitPrice = (double)vm.Platinum / perTrade;
                string unitStr = unitPrice % 1 == 0 ? ((int)unitPrice).ToString("N0") : unitPrice.ToString("N2");

                sp.Children.Add(new TextBlock
                {
                    Text = unitStr, FontWeight = FontWeight.Bold,
                    Foreground = priceColor, FontSize = 14,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                });
                sp.Children.Add(new PathIcon
                {
                    Width = 14, Height = 14, Data = platBatchIcon,
                    Foreground = priceColor,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0)
                });
                sp.Children.Add(new TextBlock
                {
                    Text = batchPrice.ToString("N0"), FontWeight = FontWeight.Bold,
                    Foreground = batchTextBrush, FontSize = 14,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0)
                });
                sp.Children.Add(new PathIcon
                {
                    Width = 13, Height = 13, Data = platIcon,
                    Foreground = batchTextBrush,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0)
                });
                sp.Children.Add(new TextBlock
                {
                    Text = "/ trade",
                    Foreground = batchTextBrush, FontSize = 14,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0)
                });
            }
            else
            {
                sp.Children.Add(new TextBlock
                {
                    Text = $"{vm.Platinum:N0} Platinum each",
                    FontWeight = FontWeight.Bold,
                    Foreground = priceColor, FontSize = 14,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                });
            }
        }

        private void PerTradeText_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBlock tb || tb.Tag is not OrderViewModel vm) return;
            tb.Text = $"{vm.PerTrade} per trade";

            vm.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(OrderViewModel.PerTrade))
                    tb.Text = $"{vm.PerTrade} per trade";
            };
        }

        private void TypeBadge_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Border border && border.Tag is OrderViewModel vm)
            {
                var color = vm.IsSell ? SellBadgeColor : BuyBadgeColor;
                var bg = vm.IsSell ? SellBadgeBg : BuyBadgeBg;
                border.Background = bg;
                border.BorderBrush = Brushes.Transparent;
                border.BorderThickness = new Thickness(0);
                if (border.Child is TextBlock tb)
                    tb.Foreground = color;
            }
        }

        private PlaceOrderWindow _placeOrderWindow;

        private void PlaceOrder_Click(object sender, RoutedEventArgs e)
        {
            if (_placeOrderWindow != null && _placeOrderWindow.IsVisible)
            {
                _placeOrderWindow.Activate();
                return;
            }
            _placeOrderWindow = new PlaceOrderWindow();
            _placeOrderWindow.OrderCreated += () => LoadOrders();
            _placeOrderWindow.Closed += (_, _) => _placeOrderWindow = null;
            _placeOrderWindow.Show();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void RetryWfm_Click(object sender, RoutedEventArgs e)
        {
            LoadOrders();
        }


        protected override void OnClosed(EventArgs e)
        {
            _instance = null;
            base.OnClosed(e);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _historyWindow?.Close();
            _editWindow?.Close();
            _placeOrderWindow?.Close();
            Close();
        }
    }
}