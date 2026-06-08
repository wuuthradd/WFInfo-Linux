using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace WFInfo.Linux.Views
{
    public partial class FilterSectionControl : UserControl
    {
        public string Header { get; set; }
        public string HeaderTooltip { get; set; }
        public string Prefix { get; set; }
        public string Param1Name { get; set; }
        public string Param2Name { get; set; }
        public string Param3Name { get; set; }
        public double Range1 { get; set; } = 1;
        public double Range2 { get; set; } = 1;
        public double Range3 { get; set; } = 1;

        public bool IsFilterEnabled
        {
            get => EnableCheck.IsChecked == true;
            set
            {
                EnableCheck.IsChecked = value;
                ContentPanel.IsVisible = value;
            }
        }

        public double Value1Max { get => Slider1Max.Value; set => Slider1Max.Value = value; }
        public double Value1Min { get => Slider1Min.Value; set => Slider1Min.Value = value; }
        public double Value2Max { get => Slider2Max.Value; set => Slider2Max.Value = value; }
        public double Value2Min { get => Slider2Min.Value; set => Slider2Min.Value = value; }
        public double Value3Max { get => Slider3Max.Value; set => Slider3Max.Value = value; }
        public double Value3Min { get => Slider3Min.Value; set => Slider3Min.Value = value; }

        public event EventHandler ValuesChanged;
        public event EventHandler EnabledChanged;

        private bool _suppressSync;

        public FilterSectionControl()
        {
            InitializeComponent();
        }

        protected override void OnInitialized()
        {
            base.OnInitialized();

            EnableCheck.Content = Header;
            ToolTip.SetTip(EnableCheck, HeaderTooltip);

            Label1Max.Text = $"{Prefix} {Param1Name} Max";
            Label1Min.Text = $"{Prefix} {Param1Name} Min";
            Label2Max.Text = $"{Prefix} {Param2Name} Max";
            Label2Min.Text = $"{Prefix} {Param2Name} Min";
            Label3Max.Text = $"{Prefix} {Param3Name} Max";
            Label3Min.Text = $"{Prefix} {Param3Name} Min";

            Slider1Max.Maximum = Slider1Min.Maximum = Range1;
            Slider2Max.Maximum = Slider2Min.Maximum = Range2;
            Slider3Max.Maximum = Slider3Min.Maximum = Range3;

            Slider1Max.Value = Range1;
            Slider2Max.Value = Range2;
            Slider3Max.Value = Range3;
        }

        public void SyncTextBoxes()
        {
            foreach (var slider in new[] { Slider1Max, Slider1Min, Slider2Max, Slider2Min, Slider3Max, Slider3Min })
                SyncSliderTextBox(slider);
        }

        private void SyncSliderTextBox(Slider slider)
        {
            if (slider.Parent is Grid grid)
            {
                var textBox = grid.Children.OfType<TextBox>().FirstOrDefault();
                if (textBox != null)
                {
                    textBox.Text = slider.Maximum > 2
                        ? ((int)Math.Round(slider.Value)).ToString()
                        : Math.Round(slider.Value, 3).ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        private void EnableCheck_Click(object sender, RoutedEventArgs e)
        {
            ContentPanel.IsVisible = EnableCheck.IsChecked == true;
            EnabledChanged?.Invoke(this, EventArgs.Empty);
            ValuesChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSync) return;
            _suppressSync = true;
            try
            {
                SyncSliderTextBox((Slider)sender);
                ValuesChanged?.Invoke(this, EventArgs.Empty);
            }
            finally { _suppressSync = false; }
        }

        private void ValueTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressSync) return;
            _suppressSync = true;
            try
            {
                var textBox = (TextBox)sender;
                if (textBox.Parent is Grid grid)
                {
                    var slider = grid.Children.OfType<Slider>().FirstOrDefault();
                    if (slider != null && double.TryParse(textBox.Text, NumberStyles.Any,
                            CultureInfo.InvariantCulture, out double val))
                    {
                        slider.Value = Math.Clamp(val, slider.Minimum, slider.Maximum);
                    }
                }
                ValuesChanged?.Invoke(this, EventArgs.Empty);
            }
            finally { _suppressSync = false; }
        }
    }
}