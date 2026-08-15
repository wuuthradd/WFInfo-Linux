using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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

        public UpdateDialogue()
        {
            InitializeComponent();
            _latestVersion = null;
        }

        public UpdateDialogue(string latestVersion, JArray releases) : this()
        {
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

                    ReleaseNotes.Children.Add(new TextBlock
                    {
                        Text = tagName,
                        FontSize = 13,
                        FontWeight = FontWeight.Bold,
                        Foreground = Brushes.White,
                        Margin = new Avalonia.Thickness(0, ReleaseNotes.Children.Count > 0 ? 6 : 0, 0, 2)
                    });

                    if (!string.IsNullOrEmpty(body))
                        RenderMarkdown(body);
                }
            }
        }

        private void RenderMarkdown(string markdown)
        {
            var lines = markdown.Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.TrimEnd('\r');

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Headings (## or ###)
                if (line.StartsWith('#'))
                {
                    string text = line.TrimStart('#').Trim();
                    ReleaseNotes.Children.Add(new TextBlock
                    {
                        Text = text,
                        FontSize = 13,
                        FontWeight = FontWeight.Bold,
                        Foreground = Brushes.White,
                        Margin = new Avalonia.Thickness(0, 4, 0, 2)
                    });
                    continue;
                }

                // List items (- or *)
                bool isList = Regex.IsMatch(line, @"^\s*[\-\*]\s+");
                if (isList)
                {
                    string text = Regex.Replace(line, @"^\s*[\-\*]\s+", "");
                    var panel = new DockPanel { Margin = new Avalonia.Thickness(4, 1, 0, 1) };
                    var bullet = new TextBlock
                    {
                        Text = "\u2022 ",
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFB1D0D9")),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
                    };
                    DockPanel.SetDock(bullet, Avalonia.Controls.Dock.Left);
                    panel.Children.Add(bullet);
                    var tb = new TextBlock
                    {
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFB1D0D9"))
                    };
                    AddInlines(tb, text);
                    panel.Children.Add(tb);
                    ReleaseNotes.Children.Add(panel);
                    continue;
                }

                // Regular paragraph
                var para = new TextBlock
                {
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFB1D0D9")),
                    Margin = new Avalonia.Thickness(0, 1, 0, 1)
                };
                AddInlines(para, line);
                ReleaseNotes.Children.Add(para);
            }
        }

        private static void AddInlines(TextBlock tb, string text)
        {
            int pos = 0;
            foreach (Match m in Regex.Matches(text, @"\*\*(.+?)\*\*|\*(.+?)\*|\[([^\]]+)\]\(([^)]+)\)"))
            {
                if (m.Index > pos)
                    tb.Inlines.Add(new Run(text[pos..m.Index]));

                if (m.Groups[1].Success)
                    tb.Inlines.Add(new Run(m.Groups[1].Value) { FontWeight = FontWeight.Bold });
                else if (m.Groups[2].Success)
                    tb.Inlines.Add(new Run(m.Groups[2].Value) { FontStyle = FontStyle.Italic });
                else if (m.Groups[3].Success)
                    tb.Inlines.Add(new Run(m.Groups[3].Value));

                pos = m.Index + m.Length;
            }

            if (pos < text.Length)
                tb.Inlines.Add(new Run(text[pos..]));
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


    }
}