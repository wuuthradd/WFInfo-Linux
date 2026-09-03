using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WFInfo.Linux.Views
{
    public class OrderViewModel : INotifyPropertyChanged
    {
        public string OrderId { get; set; }
        public string ItemId { get; set; }
        public string UrlSlug { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        private string _itemName;
        public string ItemName
        {
            get => _itemName;
            set { _itemName = value; OnPropertyChanged(); }
        }

        private string _type;
        public string Type
        {
            get => _type;
            set { _type = value; OnPropertyChanged(); OnPropertyChanged(nameof(TypeBadge)); OnPropertyChanged(nameof(IsSell)); }
        }

        private int _platinum;
        public int Platinum
        {
            get => _platinum;
            set { _platinum = value; OnPropertyChanged(); }
        }

        private int _quantity;
        public int Quantity
        {
            get => _quantity;
            set { _quantity = value; OnPropertyChanged(); OnPropertyChanged(nameof(InfoText)); }
        }

        private bool _visible;
        public bool Visible
        {
            get => _visible;
            set { _visible = value; OnPropertyChanged(); OnPropertyChanged(nameof(VisibilityText)); }
        }

        private int? _rank;
        public int? Rank
        {
            get => _rank;
            set { _rank = value; OnPropertyChanged(); OnPropertyChanged(nameof(InfoText)); OnPropertyChanged(nameof(HasInfoText)); }
        }

        private int? _perTrade;
        public int? PerTrade
        {
            get => _perTrade;
            set { _perTrade = value; OnPropertyChanged(); OnPropertyChanged(nameof(InfoText)); OnPropertyChanged(nameof(IsBulk)); OnPropertyChanged(nameof(SoldButtonText)); OnPropertyChanged(nameof(PlusButtonText)); }
        }

        public bool BulkTradable { get; set; }

        private string _subtype;
        public string Subtype
        {
            get => _subtype;
            set { _subtype = value; OnPropertyChanged(); OnPropertyChanged(nameof(SubtypeDisplay)); OnPropertyChanged(nameof(HasSubtype)); }
        }

        public string[] AvailableSubtypes { get; set; }
        public bool HasSubtype => !string.IsNullOrEmpty(Subtype);
        public string SubtypeDisplay => string.IsNullOrEmpty(Subtype) ? null
            : char.ToUpper(Subtype[0]) + Subtype.Substring(1);

        private bool _vaulted;
        public bool Vaulted
        {
            get => _vaulted;
            set { _vaulted = value; OnPropertyChanged(); }
        }

        private int? _maxRank;
        public int? MaxRank
        {
            get => _maxRank;
            set { _maxRank = value; OnPropertyChanged(); OnPropertyChanged(nameof(InfoText)); OnPropertyChanged(nameof(HasInfoText)); }
        }

        public string TypeBadge => Type == "sell" ? "wts" : "wtb";
        public bool IsSell => Type == "sell";
        public string SoldButtonText
        {
            get
            {
                string label = Type == "sell" ? "Sold" : "Bought";
                if (PerTrade.HasValue && PerTrade.Value > 1)
                    return $"{label} ({PerTrade.Value})";
                return label;
            }
        }

        public bool IsBulk => PerTrade.HasValue && PerTrade.Value > 1;

        public string PlusButtonText => (PerTrade.HasValue && PerTrade.Value > 1) ? $"+{PerTrade.Value}" : "+1";

        public string VisibilityText => Visible ? "Visible" : "Hidden";

        public string InfoText
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (Rank.HasValue)
                {
                    if (MaxRank.HasValue)
                        parts.Add($"Rank: {Rank.Value} of {MaxRank.Value}");
                    else
                        parts.Add($"Rank: {Rank.Value}");
                }
                return string.Join("  ", parts);
            }
        }

        public bool HasInfoText => !string.IsNullOrEmpty(InfoText);

        private bool _isShown = true;
        public bool IsShown
        {
            get => _isShown;
            set { if (_isShown == value) return; _isShown = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}