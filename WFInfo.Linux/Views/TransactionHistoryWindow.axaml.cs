using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Newtonsoft.Json.Linq;

namespace WFInfo.Linux.Views
{
    public partial class TransactionHistoryWindow : Window
    {
        private static TransactionHistoryWindow _instance;
        private static readonly SolidColorBrush SellBadgeColor = new(Color.FromRgb(0xCB, 0x4A, 0x9E));
        private static readonly SolidColorBrush BuyBadgeColor = new(Color.FromRgb(0x20, 0x9E, 0x70));
        private static readonly SolidColorBrush SellBadgeBg = new(Color.FromArgb(0x40, 0xCB, 0x4A, 0x9E));
        private static readonly SolidColorBrush BuyBadgeBg = new(Color.FromArgb(0x40, 0x20, 0x9E, 0x70));

        public static void ReloadIfOpen()
        {
            if (_instance is { IsVisible: true })
                _instance.LoadTransactions();
        }

        public TransactionHistoryWindow()
        {
            _instance = this;
            InitializeComponent();
            LoadTransactions();
        }

        public void LoadTransactions()
        {
            LoadingOverlay.IsVisible = true;

            Task.Run(async () =>
            {
                try
                {
                    var transactions = await AppMain.dataBase.GetMyTransactionData();
                    Dispatcher.UIThread.Post(() => Populate(transactions));
                }
                catch (Exception ex)
                {
                    AppMain.AddLog($"TransactionHistory: {ex.Message}");
                    Dispatcher.UIThread.Post(() => LoadingOverlay.IsVisible = false);
                }
            });
        }

        private void Populate(JArray transactions)
        {
            LoadingOverlay.IsVisible = false;
            TransactionList.Children.Clear();

            if (transactions == null || transactions.Count == 0)
            {
                TransactionList.Children.Add(new TextBlock
                {
                    Text = "No transactions found",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 20, 0, 0)
                });
                return;
            }

            int platReceived = 0, platSpent = 0, itemsSold = 0, itemsBought = 0;
            foreach (var tx in transactions)
            {
                string type = tx.Value<string>("order_type") ?? tx.Value<string>("type") ?? "";
                int plat = tx.Value<int?>("platinum") ?? 0;
                int qty = tx.Value<int?>("quantity") ?? 1;
                if (type == "sell")
                {
                    platReceived += plat * qty;
                    itemsSold += qty;
                }
                else if (type == "buy")
                {
                    platSpent += plat * qty;
                    itemsBought += qty;
                }
            }
            PlatReceived.Text = platReceived.ToString("N0");
            PlatSpent.Text = platSpent.ToString("N0");
            ItemsSold.Text = itemsSold.ToString();
            ItemsBought.Text = itemsBought.ToString();

            var grouped = transactions
                .GroupBy(tx =>
                {
                    var ts = tx.Value<string>("closing_date") ?? tx.Value<string>("closed_date")
                          ?? tx.Value<string>("createdAt") ?? "";
                    return DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                        ? dt.Date : DateTime.MinValue;
                })
                .OrderByDescending(g => g.Key);

            foreach (var group in grouped)
            {
                string dateLabel;
                int daysAgo = (DateTime.Today - group.Key).Days;
                if (daysAgo == 0)
                    dateLabel = DateTime.Today.ToString("MMM dd", CultureInfo.InvariantCulture).ToUpper() + ",  TODAY";
                else if (daysAgo == 1)
                    dateLabel = group.Key.ToString("MMM dd", CultureInfo.InvariantCulture).ToUpper() + ",  YESTERDAY";
                else
                    dateLabel = group.Key.ToString("MMM dd", CultureInfo.InvariantCulture).ToUpper() + $",  {daysAgo} DAYS AGO";

                TransactionList.Children.Add(new TextBlock
                {
                    Text = dateLabel,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontSize = 13,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 12, 0, 6)
                });

                var sellTxs = group.Where(t => (t.Value<string>("order_type") ?? t.Value<string>("type") ?? "") == "sell").ToList();
                var buyTxs = group.Where(t => (t.Value<string>("order_type") ?? t.Value<string>("type") ?? "") == "buy").ToList();

                var twoColGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,8,*")
                };

                var sellCol = new StackPanel { Spacing = 4 };
                var buyCol = new StackPanel { Spacing = 4 };

                foreach (var tx in sellTxs)
                    sellCol.Children.Add(CreateTransactionRow(tx));
                if (sellTxs.Count == 0)
                    sellCol.Children.Add(new TextBlock
                    {
                        Text = "Nothing",
                        Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                        FontSize = 13,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 8, 0, 0)
                    });

                foreach (var tx in buyTxs)
                    buyCol.Children.Add(CreateTransactionRow(tx));
                if (buyTxs.Count == 0)
                    buyCol.Children.Add(new TextBlock
                    {
                        Text = "Nothing",
                        Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                        FontSize = 13,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 8, 0, 0)
                    });

                Grid.SetColumn(sellCol, 0);
                Grid.SetColumn(buyCol, 2);
                twoColGrid.Children.Add(sellCol);
                twoColGrid.Children.Add(buyCol);
                TransactionList.Children.Add(twoColGrid);
            }
        }

        private string ResolveTxItemName(JToken tx)
        {
            string itemName = tx["item"]?["i18n"]?["en"]?.Value<string>("name");
            if (string.IsNullOrEmpty(itemName))
                itemName = tx["item"]?["en"]?.Value<string>("item_name");
            if (string.IsNullOrEmpty(itemName))
            {
                string urlName = tx["item"]?.Value<string>("url_name");
                if (!string.IsNullOrEmpty(urlName))
                    itemName = urlName.Replace("_", " ");
            }
            if (string.IsNullOrEmpty(itemName))
            {
                string itemId = tx["item"]?.Value<string>("id") ?? tx.Value<string>("itemId");
                if (!string.IsNullOrEmpty(itemId))
                    itemName = AppMain.dataBase.ItemIdToDisplayName(itemId);
            }
            itemName ??= "Unknown Item";
            if (itemName.Contains(" "))
                itemName = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(itemName);
            return itemName;
        }

        private static readonly SolidColorBrush SepBrush = new(Color.FromRgb(0x55, 0x55, 0x55));
        private static readonly SolidColorBrush InfoBrush = new(Color.FromRgb(0xB1, 0xD0, 0xD9));
        private static readonly SolidColorBrush DimBrush = new(Color.FromRgb(0xA4, 0xA9, 0xAA));
        private static readonly SolidColorBrush VaultedBrush = new(Color.FromRgb(0xD6, 0x9F, 0x00));

        private void AddSeparator(StackPanel panel)
        {
            panel.Children.Add(new TextBlock { Text = "|", Foreground = SepBrush, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        }

        private Border CreateTransactionRow(JToken tx)
        {
            string type = tx.Value<string>("order_type") ?? tx.Value<string>("type") ?? "sell";
            int plat = tx.Value<int?>("platinum") ?? 0;
            int qty = tx.Value<int?>("quantity") ?? 1;
            string itemName = ResolveTxItemName(tx);
            string txId = tx.Value<string>("id") ?? "";

            int? rank = tx.Value<int?>("rank") ?? tx.Value<int?>("mod_rank")
                        ?? tx["item"]?.Value<int?>("rank") ?? tx["item"]?.Value<int?>("mod_rank");
            string subtype = tx.Value<string>("subtype") ?? tx["item"]?.Value<string>("subtype");

            string itemId = tx["item"]?.Value<string>("id") ?? tx.Value<string>("itemId");
            bool vaulted = false;
            if (!string.IsNullOrEmpty(itemId))
                vaulted = AppMain.dataBase.GetItemInfoById(itemId)?.Vaulted ?? false;

            string badge = type == "sell" ? "wts" : "wtb";
            var badgeClr = type == "sell" ? SellBadgeColor : BuyBadgeColor;
            var badgeBg = type == "sell" ? SellBadgeBg : BuyBadgeBg;

            var nameText = new TextBlock
            {
                Text = itemName,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0xC0, 0xDE)),
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 3)
            };

            var badgeBorder = new Border
            {
                Background = badgeBg,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = badge,
                    FontSize = 11,
                    FontWeight = FontWeight.Bold,
                    Foreground = badgeClr
                }
            };

            var infoRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            infoRow.Children.Add(badgeBorder);
            var qtyPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            qtyPanel.Children.Add(new TextBlock { Text = qty.ToString(), Foreground = InfoBrush, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            qtyPanel.Children.Add(new PathIcon
            {
                Width = 12, Height = 12,
                Data = (Geometry)Application.Current.FindResource("IconCubes"),
                Foreground = DimBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            infoRow.Children.Add(qtyPanel);

            if (rank.HasValue)
            {
                AddSeparator(infoRow);
                int? maxRank = null;
                if (!string.IsNullOrEmpty(itemId))
                    maxRank = AppMain.dataBase.GetItemInfoById(itemId)?.MaxRank;
                string rankText = maxRank.HasValue ? $"Rank: {rank.Value} of {maxRank.Value}" : $"Rank: {rank.Value}";
                infoRow.Children.Add(new TextBlock
                {
                    Text = rankText,
                    Foreground = InfoBrush,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            if (!string.IsNullOrEmpty(subtype))
            {
                AddSeparator(infoRow);
                infoRow.Children.Add(new TextBlock
                {
                    Text = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(subtype),
                    Foreground = InfoBrush,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            if (vaulted)
            {
                AddSeparator(infoRow);
                infoRow.Children.Add(new TextBlock
                {
                    Text = "Vaulted",
                    Foreground = VaultedBrush,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var priceText = new TextBlock
            {
                Text = $"{plat:N0} Platinum each",
                Foreground = badgeClr,
                FontWeight = FontWeight.Bold,
                FontSize = 14
            };

            var trashIcon = new PathIcon
            {
                Width = 12,
                Height = 12,
                Data = (Geometry)Application.Current.FindResource("IconTrash"),
                [!PathIcon.ForegroundProperty] = new Avalonia.Data.Binding("Foreground") { RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.FindAncestor) { AncestorType = typeof(Button) } }
            };
            var deleteBtn = new Button
            {
                Content = trashIcon,
                Tag = txId,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 1),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 28,
                MinHeight = 28,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            deleteBtn.Classes.Add("deleteBtn");

            var leftContent = new StackPanel { Spacing = 3 };
            leftContent.Children.Add(nameText);
            leftContent.Children.Add(infoRow);
            leftContent.Children.Add(priceText);

            var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(leftContent, 0);
            Grid.SetColumn(deleteBtn, 1);
            rowGrid.Children.Add(leftContent);
            rowGrid.Children.Add(deleteBtn);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8),
                Margin = new Thickness(0, 2),
                Child = rowGrid
            };

            deleteBtn.Click += (s, e) =>
            {
                deleteBtn.IsEnabled = false;
                ToolTip.SetTip(deleteBtn, null);
                Task.Run(async () =>
                {
                    string error = await AppMain.dataBase.DeleteClosedOrder(txId);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (error == null)
                            LoadTransactions();
                        else
                        {
                            deleteBtn.IsEnabled = true;
                            ToolTip.SetTip(deleteBtn, error);
                        }
                    });
                });
            };

            return border;
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
            _instance = null;
            base.OnClosed(e);
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => LoadTransactions();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}