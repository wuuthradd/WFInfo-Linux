using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Newtonsoft.Json.Linq;

namespace WFInfo.Linux.Views
{
    public partial class AutoCountWindow : Window
    {
        private ObservableCollection<AutoCountItem> _items = new();

        public AutoCountWindow()
        {
            InitializeComponent();
            RewardList.ItemsSource = _items;
        }

        public void AddRewards(List<List<string>> rewards, short selectedIdx)
        {
            foreach (var screen in rewards)
            {
                if (screen.Count == 0) continue;
                int idx = Math.Min(selectedIdx, screen.Count - 1);
                _items.Add(new AutoCountItem(screen, idx));
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

        private void Close_Click(object sender, RoutedEventArgs e) => Hide();

        private void ResizeGrip_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                try { BeginResizeDrag(WindowEdge.South, e); } catch (InvalidOperationException) { }
            }
        }

        private void AddItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AutoCountItem item)
            {
                IncrementOwned(item);
                _items.Remove(item);
            }
        }

        private void DismissItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AutoCountItem item)
            {
                _items.Remove(item);
            }
        }

        private void AddAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items)
                IncrementOwned(item, save: false);

            AppMain.dataBase?.SaveAllJSONs();
            EquipmentWindow.ReloadIfOpen();
            _items.Clear();
        }

        private void DismissAll_Click(object sender, RoutedEventArgs e)
        {
            _items.Clear();
        }

        private static void IncrementOwned(AutoCountItem item, bool save = true)
        {
            string partName = item.SelectedPart;
            if (string.IsNullOrEmpty(partName) || !partName.Contains("Prime"))
            {
                AppMain.AddLog($"AutoCount: skipping invalid part \"{partName}\"");
                return;
            }

            try
            {
                // Derive prime set name and part key
                string[] parts = partName.Split(new[] { "Prime" }, 2, StringSplitOptions.None);
                string primeName = parts[0] + "Prime";
                string partKey = primeName + (parts[1].Length > 10 && !parts[1].Contains("Kubrow")
                    ? parts[1].Replace(" Blueprint", "") : parts[1]);

                var eqmt = AppMain.dataBase.equipmentData[primeName];
                if (eqmt?["parts"]?[partKey] is JObject partObj)
                {
                    int owned = partObj["owned"]?.ToObject<int>() ?? 0;
                    partObj["owned"] = owned + 1;
                    AppMain.AddLog($"AutoCount: {partKey} owned {owned} → {owned + 1}");

                    if (save)
                    {
                        AppMain.dataBase.SaveAllJSONs();
                        EquipmentWindow.ReloadIfOpen();
                    }
                }
                else
                {
                    AppMain.AddLog($"AutoCount: part \"{partKey}\" not found in equipment data");
                }
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"AutoCount: failed for \"{partName}\": {ex.Message}");
                AppMain.StatusUpdate("Failed to save item count, check logs", 2);
                AppMain.SpawnErrorPopup(DateTime.UtcNow);
            }
        }
    }

    public class AutoCountItem : System.ComponentModel.INotifyPropertyChanged
    {
        public List<string> Options { get; }
        private string _selectedPart;
        public string SelectedPart
        {
            get => _selectedPart;
            set
            {
                if (_selectedPart == value) return;
                _selectedPart = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectedPart)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        public AutoCountItem(List<string> options, int selectedIndex)
        {
            Options = new List<string>(options);
            selectedIndex = Math.Min(selectedIndex, options.Count - 1);
            _selectedPart = selectedIndex >= 0 && selectedIndex < options.Count
                ? options[selectedIndex] : null;
        }
    }
}