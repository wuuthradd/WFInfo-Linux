using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace WFInfo.Linux.Views;

public partial class RewardWindow : Window
{
    private static RewardWindow _instance;
    private readonly List<RewardPanel> _panels = new();

    internal static readonly Bitmap PlatIconBitmap;
    internal static readonly Bitmap DucatIconBitmap;

    static RewardWindow()
    {
        using var platStream = AssetLoader.Open(new Uri("avares://WFInfo.Linux/Resources/plat.png"));
        PlatIconBitmap = new Bitmap(platStream);
        using var ducatStream = AssetLoader.Open(new Uri("avares://WFInfo.Linux/Resources/ducat_w.png"));
        DucatIconBitmap = new Bitmap(ducatStream);
    }

    public RewardWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _instance = null;
    }

    public static RewardWindow Instance
    {
        get
        {
            if (_instance == null)
                _instance = new RewardWindow();
            return _instance;
        }
    }

    public void LoadPart(int partNumber, string name, string plat, string setPlat,
        string ducats, string volume, bool vaulted, bool mastered,
        string owned, bool hideInfo, string highlight = null)
    {
        // First part of a new scan, clear previous results
        if (partNumber == 0)
        {
            _panels.Clear();
            RewardPanels.Items.Clear();
        }

        while (_panels.Count <= partNumber)
        {
            var panel = new RewardPanel();
            _panels.Add(panel);
            RewardPanels.Items.Add(panel);
        }

        _panels[partNumber].SetData(name, plat, setPlat, ducats, volume,
            vaulted, mastered, owned, hideInfo, highlight);

        var pinPos = IsVisible ? Position : (PixelPoint?)null;
        Width = 250 * (partNumber + 1) + 2; // 250px per panel, 2px for border
        if (pinPos.HasValue)
            Position = pinPos.Value;
    }

    /// <summary>
    /// Call once after all LoadPart calls are done to show and raise the window.
    /// </summary>
    public void FinalizeDisplay()
    {
        Show();
        if (!Topmost)
            Topmost = true;
    }

    public void DismissRewards()
    {
        _panels.Clear();
        RewardPanels.Items.Clear();
        Topmost = false;
        Hide();
    }

    public static void DismissIfOpen()
    {
        if (_instance != null && _instance.IsVisible)
            _instance.DismissRewards();
    }

    private void Minimize_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DismissRewards();
    }

    private void OnPointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}

internal class RewardPanel : Border
{
    private readonly TextBlock _ownedText;
    private readonly TextBlock _vaultedText;
    private readonly TextBlock _nameText;
    private readonly TextBlock _platText;
    private readonly TextBlock _ducatText;
    private readonly Image _platImage;
    private readonly Image _ducatImage;
    private readonly TextBlock _volumeText;
    private readonly TextBlock _setPlatText;

    public RewardPanel()
    {
        Width = 250;
        BorderBrush = new SolidColorBrush(Color.Parse("#FF646464"));
        BorderThickness = new Thickness(0, 0, 1, 0);

        var grid = new Grid();

        _ownedText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#FF828C96")),
            FontSize = 13,
            Margin = new Thickness(10, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        grid.Children.Add(_ownedText);

        _vaultedText = new TextBlock
        {
            Text = "VAULTED",
            Foreground = new SolidColorBrush(Color.Parse("#FF828C96")),
            FontSize = 13,
            Margin = new Thickness(0, 7, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false
        };
        grid.Children.Add(_vaultedText);

        _nameText = new TextBlock
        {
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 20,
            Margin = new Thickness(15, 35, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            MaxWidth = 225,
            Height = 60
        };
        grid.Children.Add(_nameText);

        _platImage = new Image
        {
            Source = RewardWindow.PlatIconBitmap,
            Width = 26, Height = 22,
            Margin = new Thickness(45, 102, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        grid.Children.Add(_platImage);

        _platText = new TextBlock
        {
            FontSize = 18,
            Margin = new Thickness(76, 100, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        grid.Children.Add(_platText);

        _ducatImage = new Image
        {
            Source = RewardWindow.DucatIconBitmap,
            Width = 23, Height = 22,
            Margin = new Thickness(148, 102, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        grid.Children.Add(_ducatImage);

        _ducatText = new TextBlock
        {
            FontSize = 18,
            Margin = new Thickness(176, 100, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        grid.Children.Add(_ducatText);

        _volumeText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#FF9AAEB8")),
            FontSize = 13,
            Margin = new Thickness(0, 140, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        grid.Children.Add(_volumeText);

        _setPlatText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#FF9AAEB8")),
            FontSize = 16,
            Margin = new Thickness(0, 163, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        grid.Children.Add(_setPlatText);

        Child = grid;
    }

    private static readonly IBrush DefaultFg = new SolidColorBrush(Colors.White);
    private static readonly IBrush MutedFg = new SolidColorBrush(Color.Parse("#FF828C96"));
    private static readonly IBrush HlPlat = new SolidColorBrush(Color.Parse("#FF00FF00"));
    private static readonly IBrush HlDucat = new SolidColorBrush(Color.Parse("#FFFFD700"));
    private static readonly IBrush HlOwned = new SolidColorBrush(Color.Parse("#FF00FFD7"));

    public void SetData(string name, string plat, string setPlat, string ducats,
        string volume, bool vaulted, bool mastered, string owned, bool hideInfo,
        string highlight = null)
    {
        _nameText.Text = name;
        _nameText.Foreground = DefaultFg;
        _nameText.FontWeight = FontWeight.Normal;
        _platText.Foreground = DefaultFg;
        _platText.FontWeight = FontWeight.Normal;
        _ducatText.Foreground = DefaultFg;
        _ducatText.FontWeight = FontWeight.Normal;
        _ownedText.Foreground = MutedFg;
        _ownedText.FontWeight = FontWeight.Normal;

        if (hideInfo)
        {
            _platImage.IsVisible = false;
            _ducatImage.IsVisible = false;
            _platText.Text = "";
            _ducatText.Text = "";
            _volumeText.Text = "";
            _setPlatText.Text = "";
            _vaultedText.IsVisible = false;
            _ownedText.Text = "";
            return;
        }

        _platImage.IsVisible = true;
        _ducatImage.IsVisible = true;
        _platText.Text = plat;
        _ducatText.Text = ducats;
        _volumeText.Text = volume?.Length > 0 ? volume + " sold last 48hrs" : "";
        _setPlatText.Text = setPlat?.Length > 0 ? "Full set: " + setPlat + "p" : "";
        _vaultedText.IsVisible = vaulted;
        _ownedText.Text = owned?.Length > 0 ? (mastered ? "\u2713 " : "") + owned + " OWNED" : "";

        if (highlight == "plat")
        {
            _nameText.Foreground = HlPlat;
            _nameText.FontWeight = FontWeight.Bold;
            _platText.Foreground = HlPlat;
            _platText.FontWeight = FontWeight.Bold;
        }
        else if (highlight == "ducat")
        {
            _nameText.Foreground = HlDucat;
            _nameText.FontWeight = FontWeight.Bold;
            _ducatText.Foreground = HlDucat;
            _ducatText.FontWeight = FontWeight.Bold;
        }
        else if (highlight == "owned")
        {
            _nameText.Foreground = HlOwned;
            _nameText.FontWeight = FontWeight.Bold;
            _ownedText.Foreground = HlOwned;
            _ownedText.FontWeight = FontWeight.Bold;
        }
    }
}