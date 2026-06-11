using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Newtonsoft.Json.Linq;
using WFInfo.Services;

namespace WFInfo.Linux.Views
{
    public partial class VerifyCountWindow : Window
    {
        private List<InventoryItem> _latestSnap = new();
        private static Avalonia.PixelPoint? _lastPosition;

        public VerifyCountWindow()
        {
            InitializeComponent();
        }

        public void ShowVerifyCount(List<InventoryItem> items)
        {
            _latestSnap = items;
            BackupButton.IsVisible = true;
            if (_lastPosition.HasValue)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = _lastPosition.Value;
            }
            Show();
            Activate();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            _lastPosition = Position;
            base.OnClosing(e);
        }

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                var pos = e.GetPosition((Avalonia.Visual)sender);
                if (pos.Y > 25) return;
                try { BeginMoveDrag(e); }
                catch (InvalidOperationException) { }
            }
        }

        private void SaveClick(object sender, RoutedEventArgs e)
        {
            bool saveFailed = false;
            foreach (var item in _latestSnap)
            {
                if (!item.Name.Contains("Prime")) continue;
                string[] nameParts = item.Name.Split(new[] { "Prime" }, 2, StringSplitOptions.None);
                string primeName = nameParts[0] + "Prime";
                string partName = primeName + (nameParts[1].Length > 10 && !nameParts[1].Contains("Kubrow")
                    ? nameParts[1].Replace(" Blueprint", "") : nameParts[1]);

                AppMain.AddLog($"Saving count \"{item.Count}\" for part \"{partName}\"");
                try
                {
                    AppMain.dataBase.equipmentData[primeName]["parts"][partName]["owned"] = item.Count;
                }
                catch (Exception ex)
                {
                    AppMain.AddLog($"FAILED to save count. Count: {item.Count}, Name: {item.Name}, Error: {ex.Message}");
                    saveFailed = true;
                }
            }
            AppMain.dataBase.SaveAllJSONs();

            if (saveFailed)
                AppMain.StatusUpdate("Failed to save one or more items", 2);
            else
                AppMain.StatusUpdate("Item counts saved", 0);

            EquipmentWindow.ReloadIfOpen();
            Close();
        }

        private void BackupClick(object sender, RoutedEventArgs e)
        {
            string itemPath = Path.Combine(PlatformPaths.AppDataPath, "eqmt_data.json");
            string backupPath = itemPath + ".bak";

            if (File.Exists(backupPath)) File.Delete(backupPath);
            if (File.Exists(itemPath)) File.Copy(itemPath, backupPath);

            foreach (var prime in AppMain.dataBase.equipmentData)
            {
                if (!prime.Key.Contains("Prime")) continue;
                string primeName = prime.Key.Substring(0, prime.Key.IndexOf("Prime") + 5);
                if (prime.Value["parts"] is JObject parts)
                {
                    foreach (var part in parts)
                        AppMain.dataBase.equipmentData[primeName]["parts"][part.Key]["owned"] = 0;
                }
            }
            BackupButton.IsVisible = false;
            AppMain.dataBase.SaveAllJSONs();
            EquipmentWindow.ReloadIfOpen();
            AppMain.StatusUpdate("Inventory backed up and cleared", 0);
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}