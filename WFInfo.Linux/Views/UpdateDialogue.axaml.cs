using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.ApplicationLifetimes;
using Newtonsoft.Json.Linq;
using WFInfo.Settings;

namespace WFInfo.Linux.Views
{
    public partial class UpdateDialogue : Window
    {
        private const string ReleasesApiUrl = "https://api.github.com/repos/wuuthradd/WFInfo-Linux/releases";
        private const string ReleasesPageUrl = "https://github.com/wuuthradd/WFInfo-Linux/releases/latest";

        private static UpdateDialogue _current;
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
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            Padding = new Avalonia.Thickness(10, 0, 0, 0),
                            Foreground = Avalonia.Media.Brushes.LightGray
                        };
                        ParseMarkdownInto(note, body);
                        ReleaseNotes.Children.Add(note);
                    }
                }
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        private async void DownloadClick(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn != null)
            {
                btn.IsEnabled = false;
                btn.Content = "Updating...";
            }

            SetAllWindowsLocked(true);
            AppMain.StatusUpdate("Downloading update...", 0);

            string error = await Services.SelfUpdater.PerformUpdate(_latestVersion);
            if (error != null)
            {
                SetAllWindowsLocked(false);
                AppMain.StatusUpdate("Auto-update failed", 2);
                NewVersionText.Text = error;
                NewVersionText.Foreground = Avalonia.Media.Brushes.IndianRed;
                if (btn != null)
                {
                    btn.IsEnabled = true;
                    btn.Content = "Retry";
                }
            }
        }

        private void SetAllWindowsLocked(bool locked)
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var window in desktop.Windows)
                {
                    if (window == this) continue;
                    window.IsEnabled = !locked;
                }
            }
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

        public static async void CheckForUpdates(bool force = false, Action<bool> onComplete = null)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("WFInfo/" + AppMain.BuildVersion);

                var response = await client.GetAsync(ReleasesApiUrl);
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                    response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    AppMain.AddLog("Update check failed: GitHub API rate limit exceeded");
                    AppMain.StatusUpdate("Update check failed, GitHub rate limit exceeded", 1);
                    onComplete?.Invoke(false);
                    return;
                }
                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync();
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
                {
                    onComplete?.Invoke(false);
                    return;
                }

                if (latestVersion == AppMain.BuildVersion)
                {
                    AppMain.AddLog("Update check: already on latest version " + latestVersion);
                    if (force)
                        AppMain.StatusUpdate("Already on latest version " + latestVersion, 2);
                    onComplete?.Invoke(false);
                    return;
                }

                if (!force && latestVersion == ApplicationSettings.GlobalSettings.IgnoredUpdate)
                {
                    AppMain.AddLog("Update check: version " + latestVersion + " was skipped by user");
                    onComplete?.Invoke(false);
                    return;
                }

                if (!IsNewer(latestVersion, AppMain.BuildVersion))
                {
                    onComplete?.Invoke(false);
                    return;
                }

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_current != null)
                    {
                        _current.Activate();
                        return;
                    }
                    var dialogue = new UpdateDialogue(latestVersion, releases);
                    dialogue.Closed += (_, _) => _current = null;
                    _current = dialogue;
                    dialogue.Show();
                });
                onComplete?.Invoke(true);
            }
            catch (HttpRequestException ex)
            {
                AppMain.AddLog($"Update check failed: {ex.Message}");
                AppMain.StatusUpdate("Update check failed, check your internet connection", 1);
                onComplete?.Invoke(false);
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"Update check failed: {ex.Message}");
                AppMain.StatusUpdate("Update check failed", 1);
                onComplete?.Invoke(false);
            }
        }

        private static bool IsNewer(string remote, string local)
        {
            if (Version.TryParse(remote, out var rv) && Version.TryParse(local, out var lv))
                return rv > lv;
            return string.Compare(remote, local, StringComparison.Ordinal) > 0;
        }

        private static void ParseMarkdownInto(TextBlock textBlock, string markdown)
        {
            // Strip bold/italic markers
            string text = Regex.Replace(markdown, @"\*{1,3}(.+?)\*{1,3}", "$1");

            int lastIndex = 0;
            foreach (Match m in Regex.Matches(text, @"\[([^\]]+)\]\(([^)]+)\)"))
            {
                if (m.Index > lastIndex)
                    textBlock.Inlines.Add(new Run(text[lastIndex..m.Index]));

                string linkText = m.Groups[1].Value;
                string url = m.Groups[2].Value;
                var hyperlink = new Run(linkText)
                {
                    Foreground = Avalonia.Media.Brushes.CornflowerBlue,
                    TextDecorations = Avalonia.Media.TextDecorations.Underline
                };
                textBlock.Inlines.Add(hyperlink);

                lastIndex = m.Index + m.Length;
            }

            if (lastIndex < text.Length)
                textBlock.Inlines.Add(new Run(text[lastIndex..] + "\n"));
        }
    }
}