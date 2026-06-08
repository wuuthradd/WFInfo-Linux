using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Newtonsoft.Json.Linq;

namespace WFInfo.Linux.Views
{
    public partial class EquipmentWindow : Window
    {
        private readonly List<string> _types = new() { "Warframes", "Primary", "Secondary", "Melee", "Archwing", "Companion" };
        private readonly AvaloniaList<TreeNode> _displayList = new();
        private Dictionary<string, TreeNode> _primeTypes;
        private bool _showAllEqmt = false;
        private bool _hideVaulted = true;
        private string[] _searchText;
        private DispatcherTimer _searchTimer;

        public EquipmentWindow()
        {
            InitializeComponent();
            EquipmentTree.ItemsSource = _displayList;
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _searchTimer.Tick += (s, e) =>
            {
                _searchTimer.Stop();
                ReapplyFilters();
            };
            LoadEquipmentData();
        }

        private void LoadEquipmentData()
        {
            try
            {
                if (AppMain.dataBase?.equipmentData == null)
                {
                    AppMain.AddLog("Equipment data not loaded yet");
                    return;
                }

                _primeTypes = new Dictionary<string, TreeNode>();
                var equipmentData = AppMain.dataBase.equipmentData;

                foreach (var eqmt in equipmentData)
                {
                    string eqmtName = eqmt.Key;
                    if (!eqmtName.Contains("Prime")) continue;

                    var eqmtObj = eqmt.Value as JObject;
                    if (eqmtObj == null) continue;

                    string primeName = eqmtName.Substring(0, eqmtName.IndexOf("Prime") + 5);
                    string primeType = eqmtObj["type"]?.ToObject<string>() ?? "Unknown";
                    bool mastered = eqmtObj["mastered"]?.ToObject<bool>() ?? false;
                    bool vaulted = eqmtObj["vaulted"]?.ToObject<bool>() ?? false;

                    if (primeType.Contains("Sentinel") || primeType.Contains("Skin"))
                        primeType = "Companion";
                    else if (primeType.Contains("Arch"))
                        primeType = "Archwing";

                    if (!_primeTypes.ContainsKey(primeType))
                    {
                        var newType = new TreeNode(primeType, "", false, 0);
                        if (!_types.Contains(primeType))
                            _types.Add(primeType);
                        newType.SortNum = _types.IndexOf(primeType);
                        _primeTypes[primeType] = newType;
                    }

                    TreeNode typeNode = _primeTypes[primeType];
                    var primeNode = new TreeNode(primeName, vaulted ? "Vaulted" : "", mastered, 1);
                    primeNode.MakeClickable(eqmtName);

                    if (eqmtObj["parts"] is JObject parts)
                    {
                        foreach (var part in parts)
                        {
                            string partName = part.Key;
                            var partObj = part.Value as JObject;
                            if (partObj == null) continue;

                            // Shorten display name (remove "XXX Prime " prefix)
                            string displayName = partName;
                            int primeIdx = partName.IndexOf("Prime");
                            if (primeIdx >= 0 && primeIdx + 6 < partName.Length)
                                displayName = partName.Substring(primeIdx + 6);
                            if (displayName.Contains("Kubrow") && displayName.Contains("Blueprint"))
                                displayName = displayName.Substring(displayName.IndexOf(" Blueprint") + 1);

                            int owned = partObj["owned"]?.ToObject<int>() ?? 0;
                            int count = partObj["count"]?.ToObject<int>() ?? 1;
                            bool partVaulted = partObj["vaulted"]?.ToObject<bool>() ?? vaulted;

                            var partNode = new TreeNode(displayName, partVaulted ? "Vaulted" : "", false, 0);
                            partNode.MakeClickable(partName);

                            if (AppMain.dataBase.marketData != null &&
                                AppMain.dataBase.marketData.TryGetValue(partName, out JToken marketToken))
                            {
                                double plat = marketToken["plat"]?.ToObject<double>() ?? 0;
                                int ducat = marketToken["ducats"]?.ToObject<int>() ?? 0;
                                partNode.SetPrimePart(plat, ducat, owned, count);
                            }
                            else if (AppMain.dataBase.equipmentData.TryGetValue(partName, out JToken subJob))
                            {
                                double plat = 0;
                                double ducats = 0;
                                if (subJob["parts"] is JObject subParts && AppMain.dataBase.marketData != null)
                                {
                                    foreach (var subPart in subParts)
                                    {
                                        if (AppMain.dataBase.marketData.TryGetValue(subPart.Key, out JToken subMarket))
                                        {
                                            int temp = subPart.Value["count"]?.ToObject<int>() ?? 1;
                                            plat += temp * (subMarket["plat"]?.ToObject<double>() ?? 0);
                                            ducats += temp * (subMarket["ducats"]?.ToObject<int>() ?? 0);
                                        }
                                    }
                                }
                                partNode.SetPrimeEqmt(plat, ducats, owned, count);
                            }
                            else
                            {
                                AppMain.AddLog("COULDN'T FIND MARKET VALUES FOR: " + partName);
                                continue;
                            }

                            primeNode.AddChild(partNode);
                        }
                    }

                    if (primeNode.Children.Count > 0)
                    {
                        primeNode.GetSetInfo();
                        typeNode.AddChild(primeNode);
                    }
                    else
                    {
                        AppMain.AddLog("EQUIPMENT: Skipping " + primeName + ", no children (all parts missing from marketData)");
                    }
                }

                ReapplyFilters();
                int totalItems = _primeTypes.Values.Sum(t => t.Children.Count);
                AppMain.AddLog("Loaded " + totalItems + " equipment items in " + _primeTypes.Count + " categories");
            }
            catch (Exception ex)
            {
                AppMain.AddLog("Failed to load equipment data: " + ex.Message);
            }
        }

        private void ReapplyFilters()
        {
            if (_primeTypes == null) return;

            foreach (var kvp in _primeTypes)
                kvp.Value.ResetFilter();

            if (_hideVaulted)
                foreach (var kvp in _primeTypes)
                    kvp.Value.FilterOutVaulted(true);

            if (_searchText != null && _searchText.Length > 0)
                foreach (var kvp in _primeTypes)
                    kvp.Value.FilterSearchText(_searchText, false, true);

            RefreshTreeView();
        }

        private void RefreshTreeView()
        {
            if (_primeTypes == null || EquipmentTree == null) return;

            int sortIndex = SortBox?.SelectedIndex ?? 0;

            if (_showAllEqmt)
            {
                var allNodes = new List<TreeNode>();
                foreach (string typeName in _types)
                {
                    if (!_primeTypes.ContainsKey(typeName)) continue;
                    foreach (var eqmt in _primeTypes[typeName].ChildrenFiltered)
                        allNodes.Add(eqmt);
                }

                SortNodeListInPlace(allNodes, sortIndex);
                bool alt = false;
                foreach (var node in allNodes)
                {
                    alt = !alt;
                    node.Background_Color = alt ? TreeNode.BACK_D_HEX : TreeNode.BACK_U_HEX;
                }

                RefreshDisplayList(allNodes, 0);
            }
            else
            {
                var visibleTypes = new List<TreeNode>();
                foreach (string typeName in _types)
                {
                    if (!_primeTypes.ContainsKey(typeName)) continue;
                    var typeNode = _primeTypes[typeName];
                    typeNode.Sort(sortIndex, false);
                    typeNode.RecolorChildren();
                    if (typeNode.ChildrenFiltered.Count > 0)
                        visibleTypes.Add(typeNode);
                }

                RefreshDisplayList(visibleTypes, 0);
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
            if (_primeTypes == null || EquipmentTree == null) return;

            if (_showAllEqmt)
            {
                var allNodes = new List<TreeNode>();
                foreach (string typeName in _types)
                {
                    if (!_primeTypes.ContainsKey(typeName)) continue;
                    foreach (var eqmt in _primeTypes[typeName].ChildrenFiltered)
                        allNodes.Add(eqmt);
                }
                RefreshDisplayList(allNodes, 0);
            }
            else
            {
                var visibleTypes = new List<TreeNode>();
                foreach (string typeName in _types)
                {
                    if (!_primeTypes.ContainsKey(typeName)) continue;
                    var typeNode = _primeTypes[typeName];
                    if (typeNode.ChildrenFiltered.Count > 0)
                        visibleTypes.Add(typeNode);
                }
                RefreshDisplayList(visibleTypes, 0);
            }
        }

        private static void SortNodeListInPlace(List<TreeNode> nodes, int sortIndex)
        {
            if (nodes.Count <= 1) return;
            Comparison<TreeNode> cmp = sortIndex switch
            {
                1 => (a, b) => b.Plat_Val.CompareTo(a.Plat_Val),
                2 => (a, b) => { int r = a.Diff_Val.CompareTo(b.Diff_Val); return r != 0 ? r : a.Owned_Capped_Val.CompareTo(b.Owned_Capped_Val); },
                3 => (a, b) => b.Owned_Val.CompareTo(a.Owned_Val),
                4 => (a, b) => b.Owned_Plat_Val.CompareTo(a.Owned_Plat_Val),
                5 => (a, b) => b.Owned_Ducat_Val.CompareTo(a.Owned_Ducat_Val),
                _ => (a, b) => string.Compare(a.EqmtName_Sort, b.EqmtName_Sort, StringComparison.Ordinal)
            };
            nodes.Sort(cmp);
        }

        private void SingleClickExpand(object sender, SelectionChangedEventArgs e)
        {
            if (EquipmentTree.SelectedItem is TreeNode node)
            {
                EquipmentTree.SelectedItem = null;
                if (node.IsParentNode)
                {
                    node.IsExpanded = !node.IsExpanded;
                    RefreshTreeView();
                }
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
                try { BeginResizeDrag(WindowEdge.South, e); } catch (InvalidOperationException) { }
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

        private void VaultedClick(object sender, RoutedEventArgs e)
        {
            _hideVaulted = VaultedCheckBox.IsChecked == true;
            ReapplyFilters();
        }

        private void SortBoxChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshTreeView();
        }

        private void ToggleShowAllEqmt(object sender, RoutedEventArgs e)
        {
            _showAllEqmt = !_showAllEqmt;
            EqmtModeButton.Content = _showAllEqmt ? "All Equipment" : "Equipment Types";

            if (_primeTypes != null)
                foreach (var kvp in _primeTypes)
                    foreach (var kid in kvp.Value.Children)
                        kid.topLevel = _showAllEqmt;

            RefreshTreeView();
        }

        private void ExpandAll_Click(object sender, RoutedEventArgs e)
        {
            if (_primeTypes != null)
            {
                foreach (var kvp in _primeTypes)
                    kvp.Value.ChangeExpandedTo(true);
                RefreshTreeView();
            }
        }

        private void CollapseAll_Click(object sender, RoutedEventArgs e)
        {
            if (_primeTypes != null)
            {
                foreach (var kvp in _primeTypes)
                    kvp.Value.ChangeExpandedTo(false);
                RefreshTreeView();
            }
        }

        /// <summary>
        /// Reload owned counts from equipmentData in-place (matches WPF reloadItems).
        /// Called after master-it, verify-count save, and backup-clear.
        /// </summary>
        public void ReloadItems()
        {
            if (_primeTypes == null) return;

            foreach (var category in _primeTypes.Values)
            {
                foreach (var prime in category.Children)
                {
                    foreach (var part in prime.Children)
                    {
                        part.ReloadPartOwned(prime);
                    }
                    prime.GetSetInfo();
                }
            }
            RefreshTreeView();
        }

        private void RowBorder_Loaded(object sender, RoutedEventArgs e)
        {
            // Show ✓ immediately for already-mastered items (matches WPF)
            if (sender is Border border && border.DataContext is TreeNode node && node.Mastered)
                foreach (var btn in border.GetVisualDescendants().OfType<Button>())
                    if (btn.Classes.Contains("hover-btn-check"))
                        ShowBtn(btn, true);
        }

        private static void ShowBtn(Button btn, bool show)
        {
            btn.Opacity = show ? 1 : 0;
            btn.IsHitTestVisible = show;
        }

        private void RowBorder_PointerEntered(object sender, PointerEventArgs e)
        {
            if (sender is Border border && border.DataContext is TreeNode node)
            {
                bool isPrimeSet = node.ShowAll is byte b && b == 1;
                foreach (var btn in border.GetVisualDescendants().OfType<Button>())
                {
                    if (btn.Classes.Contains("hover-btn-check"))
                        ShowBtn(btn, isPrimeSet);
                    else if (btn.Classes.Contains("hover-btn"))
                        ShowBtn(btn, node.IsLeaf);
                }
            }
        }

        private void RowBorder_PointerExited(object sender, PointerEventArgs e)
        {
            if (sender is Border border && border.DataContext is TreeNode node)
                foreach (var btn in border.GetVisualDescendants().OfType<Button>())
                {
                    if (btn.Classes.Contains("hover-btn-check"))
                    {
                        if (!node.Mastered)
                            ShowBtn(btn, false);
                    }
                    else if (btn.Classes.Contains("hover-btn"))
                        ShowBtn(btn, false);
                }
        }

        /// <summary>
        /// Find the open EquipmentWindow (if any) and reload its data.
        /// </summary>
        public static void ReloadIfOpen()
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var win in desktop.Windows)
                {
                    if (win is EquipmentWindow eqmt)
                    {
                        eqmt.ReloadItems();
                        return;
                    }
                }
            }
        }
    }
}
