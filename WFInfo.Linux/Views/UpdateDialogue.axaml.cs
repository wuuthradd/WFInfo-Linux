using System;
using System.Diagnostics;
using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Newtonsoft.Json.Linq;
using WFInfo.Settings;

namespace WFInfo.Linux.Views
{
    public partial class UpdateDialogue : Window
    {
        private const string ReleasesApiUrl = "https://api.github.com/repos/wuuthradd/WFInfo-Linux/releases";
        private const string ReleasesPageUrl = "https://github.com/wuuthradd/WFInfo-Linux/releases/latest";

        private readonly string _latestVersion;

        public UpdateDialogue(string latestVersion, JArray releases)
        {
            InitializeComponent();

            _latestVersion = latestVersion;
            NewVersionText.Text = $"WFInfo version {latestVersion} has been released!";
            OldVersionText.Text = $"You have version {AppMain.BuildVersion} installed.";

            foreach (JObject release in releases)
            {
                if (release["prerelease"]?.ToObject<bool>() == true)
                    continue;

                string tagName = release["tag_name"]?.ToString();
                string body = release["body"]?.ToString();

                if (tagName != null)
                {
                    string cleanTag = tagName.TrimStart('v');
                    if (cleanTag == AppMain.BuildVersion)
                        break;

                    var tag = new TextBlock
                    {
                        Text = tagName,
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        Foreground = Avalonia.Media.Brushes.White
                    };
                    ReleaseNotes.Children.Add(tag);

                    if (!string.IsNullOrEmpty(body))
                    {
                        var note = new TextBlock
                        {
                            Text = body + "\n",
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            Padding = new Avalonia.Thickness(10, 0, 0, 0),
                            Foreground = Avalonia.Media.Brushes.LightGray
                        };
                        ReleaseNotes.Children.Add(note);
                    }
                }
            }
        }

        private void DownloadClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(ReleasesPageUrl) { UseShellExecute = true });
            }
            catch { }
            Close();
        }

        private void SkipClick(object sender, RoutedEventArgs e)
        {
            ApplicationSettings.GlobalSettings.IgnoredUpdate = _latestVersion;
            ApplicationSettings.GlobalSettings.Save();
            Close();
        }

        private void CloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                try { BeginMoveDrag(e); } catch (InvalidOperationException) { }
        }

        public static async void CheckForUpdates()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("WFInfo/" + AppMain.BuildVersion);

                string json = await client.GetStringAsync(ReleasesApiUrl);
                var releases = JArray.Parse(json);

                string latestVersion = null;
                foreach (JObject release in releases)
                {
                    if (release["prerelease"]?.ToObject<bool>() == true)
                        continue;
                    string tag = release["tag_name"]?.ToString()?.TrimStart('v');
                    if (tag != null)
                    {
                        latestVersion = tag;
                        break;
                    }
                }

                if (latestVersion == null)
                    return;

                if (latestVersion == AppMain.BuildVersion)
                    return;

                if (latestVersion == ApplicationSettings.GlobalSettings.IgnoredUpdate)
                    return;

                if (!IsNewer(latestVersion, AppMain.BuildVersion))
                    return;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var dialogue = new UpdateDialogue(latestVersion, releases);
                    dialogue.Show();
                });
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"Update check failed: {ex.Message}");
            }
        }

        private static bool IsNewer(string remote, string local)
        {
            if (Version.TryParse(remote, out var rv) && Version.TryParse(local, out var lv))
                return rv > lv;
            return string.Compare(remote, local, StringComparison.Ordinal) > 0;
        }
    }
}