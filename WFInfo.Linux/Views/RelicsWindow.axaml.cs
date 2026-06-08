using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Newtonsoft.Json.Linq;

namespace WFInfo.Linux.Views
{
    public class DepthToPaddingConverter : IValueConverter
    {
        public static readonly DepthToPaddingConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int depth = value is int d ? d : 0;
            return new Thickness(2 + depth * 16, 3, 14, 3);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public partial class RelicsWindow : Window
    {
        private List<TreeNode> _eraNodes = new List<TreeNode>();
        private readonly AvaloniaList<TreeNode> _displayList = new();
        private bool _hideVaulted = true;
        private bool _showAllRelics = false;
        private string[] _searchText;
        private DispatcherTimer _searchTimer;
        private int _lastSortIndex = -1;

        public RelicsWindow()
        {
            InitializeComponent();
            RelicTree.ItemsSource = _displayList;
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _searchTimer.Tick += (s, e) =>
            {
                _searchTimer.Stop();
                ReapplyFilters();
            };
            LoadRelicData();
        }

        private void LoadRelicData()
        {
            try
            {
                if (AppMain.dataBase?.relicData == null)
                {
                    AppMain.AddLog("Relic data not loaded yet");
                    return;
                }

                _eraNodes.Clear();
                _lastSortIndex = -1;
                var relicData = AppMain.dataBase.relicData;

                // Match WPF: fixed era order with SortNum for proper Name_Sort in "All Relics" mode
                string[] eraOrder = { "Lith", "Meso", "Neo", "Axi", "Vanguard" };
                int eraNum = 0;

                foreach (string eraName in eraOrder)
                {
                    if (!relicData.ContainsKey(eraName)) continue;

                    var eraNode = new TreeNode(eraName, "", false, 0);
                    eraNode.SortNum = eraNum++;

                    if (relicData[eraName] is JObject eraObj)
                    {
                        foreach (var relic in eraObj)
                        {
                            string relicName = relic.Key;
                            var relicObj = relic.Value as JObject;
                            if (relicObj == null) continue;

                            bool vaulted = relicObj["vaulted"]?.ToObject<bool>() ?? false;
                            var relicNode = new TreeNode(relicName, vaulted ? "vaulted" : "", false, 0);
                            relicNode.Era = eraName;

                            foreach (var kvp in relicObj)
                            {
                                if (kvp.Key == "vaulted") continue;

                                string partName = kvp.Value.ToString();
                                string rarity = kvp.Key;

                                if (AppMain.dataBase.marketData != null &&
                                    AppMain.dataBase.marketData.TryGetValue(partName, out JToken marketValues))
                                {
                                    var partNode = new TreeNode(partName, "", false, 0);
                                    partNode.SetPartText(
                                        marketValues["plat"]?.ToObject<double>() ?? 0,
                                        marketValues["ducats"]?.ToObject<int>() ?? 0,
                                        rarity);
                                    relicNode.AddChild(partNode);
                                }
                            }

                            relicNode.SetRelicText();
                            eraNode.AddChild(relicNode);
                        }
                    }

                    eraNode.SetEraText();
                    eraNode.ResetFilter();
                    eraNode.FilterOutVaulted();
                    eraNode.RecolorChildren();
                    _eraNodes.Add(eraNode);
                }

                ReapplyFilters();
                AppMain.AddLog("Loaded " + _eraNodes.Count + " relic eras");
            }
            catch (Exception ex)
            {
                AppMain.AddLog("Failed to load relic data: " + ex.Message);
            }
        }

        private void ReapplyFilters()
        {
            if (RelicTree == null) return;

            foreach (var era in _eraNodes)
                era.ResetFilter();

            if (_hideVaulted)
                foreach (var era in _eraNodes)
                    era.FilterOutVaulted(true);

            if (_searchText != null && _searchText.Length > 0)
                foreach (var era in _eraNodes)
                    era.FilterSearchText(_searchText, false, true);

            int sortIndex = SortBox?.SelectedIndex ?? 0;
            bool sortNeeded = sortIndex != _lastSortIndex;
            _lastSortIndex = sortIndex;

            foreach (var era in _eraNodes)
            {
                if (sortNeeded)
                    era.Sort(sortIndex, true);
                era.RecolorChildren();
            }

            if (_showAllRelics)
            {
                var allRelics = new List<TreeNode>();
                foreach (var era in _eraNodes)
                    foreach (var relic in era.ChildrenFiltered)
                        allRelics.Add(relic);

                Comparison<TreeNode> cmp = sortIndex switch
                {
                    1 => (a, b) => b.Intact_Val.CompareTo(a.Intact_Val),
                    2 => (a, b) => b.Radiant_Val.CompareTo(a.Radiant_Val),
                    3 => (a, b) => b.Bonus_Val.CompareTo(a.Bonus_Val),
                    _ => (a, b) => string.Compare(a.Name_Sort, b.Name_Sort, StringComparison.Ordinal)
                };
                allRelics.Sort(cmp);

                bool alt = false;
                foreach (var relic in allRelics)
                {
                    alt = !alt;
                    relic.Background_Color = alt ? TreeNode.BACK_D_HEX : TreeNode.BACK_U_HEX;
                }

                RefreshDisplayList(allRelics, 0);
            }
            else
            {
                RefreshDisplayList(
                    _eraNodes.Where(e => e.ChildrenFiltered.Count > 0), 0);
            }
        }

        private void RefreshDisplayList(IEnumerable<TreeNode> roots, int startDepth)
        {
            var temp = new List<TreeNode>();
            FlattenNodes(roots, startDepth, temp);
            _displayList.Clear();
            _displayList.AddRange(temp);
        }

        private void FlattenNodes(IEnumerable<TreeNode> nodes, int depth, List<TreeNode> result)
        {
            foreach (var node in nodes)
            {
                node.Depth = depth;
                result.Add(node);
                if (node.IsExpanded && node.ChildrenFiltered.Count > 0)
                    FlattenNodes(node.ChildrenFiltered, depth + 1, result);
            }
        }

        private void RebuildFlatList()
        {
            if (_showAllRelics)
            {
                int sortIndex = SortBox?.SelectedIndex ?? 0;
                var allRelics = new List<TreeNode>();
                foreach (var era in _eraNodes)
                    foreach (var relic in era.ChildrenFiltered)
                        allRelics.Add(relic);

                Comparison<TreeNode> cmp = sortIndex switch
                {
                    1 => (a, b) => b.Intact_Val.CompareTo(a.Intact_Val),
                    2 => (a, b) => b.Radiant_Val.CompareTo(a.Radiant_Val),
                    3 => (a, b) => b.Bonus_Val.CompareTo(a.Bonus_Val),
                    _ => (a, b) => string.Compare(a.Name_Sort, b.Name_Sort, StringComparison.Ordinal)
                };
                allRelics.Sort(cmp);

                bool alt = false;
                foreach (var relic in allRelics)
                {
                    alt = !alt;
                    relic.Background_Color = alt ? TreeNode.BACK_D_HEX : TreeNode.BACK_U_HEX;
                }

                RefreshDisplayList(allRelics, 0);
            }
            else
            {
                RefreshDisplayList(
                    _eraNodes.Where(e => e.ChildrenFiltered.Count > 0), 0);
            }
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

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void ResizeGrip_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                try { BeginResizeDrag(WindowEdge.SouthEast, e); } catch (InvalidOperationException) { }
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string text = SearchBox?.Text?.Trim();
            bool hasSearch = !string.IsNullOrEmpty(text);

            if (hasSearch || (_searchText != null && _searchText.Length > 0))
            {
                _searchText = hasSearch ? text.Split(' ', StringSplitOptions.RemoveEmptyEntries) : null;
                _searchTimer.Stop();
                _searchTimer.Start();
            }
        }

        private void SortBoxChanged(object sender, SelectionChangedEventArgs e)
        {
            ReapplyFilters();
        }

        private void VaultedClick(object sender, RoutedEventArgs e)
        {
            _hideVaulted = VaultedCheckBox.IsChecked == true;
            ReapplyFilters();
        }

        private void ExpandAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var era in _eraNodes)
                era.ChangeExpandedTo(true);
            RebuildFlatList();
        }

        private void CollapseAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var era in _eraNodes)
                era.ChangeExpandedTo(false);
            RebuildFlatList();
        }

        private void SingleClickExpand(object sender, SelectionChangedEventArgs e)
        {
            if (RelicTree.SelectedItem is TreeNode node)
            {
                RelicTree.SelectedItem = null;
                if (node.IsParentNode)
                {
                    node.IsExpanded = !node.IsExpanded;
                    RebuildFlatList();
                }
            }
        }

        private void ToggleAllRelics(object sender, RoutedEventArgs e)
        {
            _showAllRelics = !_showAllRelics;
            AllRelicsButton.Content = _showAllRelics ? "All Relics" : "Era Groups";

            foreach (var era in _eraNodes)
                foreach (var relic in era.Children)
                    relic.topLevel = _showAllRelics;

            ReapplyFilters();
        }
    }
}
