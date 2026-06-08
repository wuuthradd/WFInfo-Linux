using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;

using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Input;

namespace WFInfo
{
    public class INPC : INotifyPropertyChanged
    {
        private static SynchronizationContext _uiContext;

        public static void CaptureUIContext()
        {
            _uiContext = SynchronizationContext.Current;
        }

        protected bool SetField<T>(ref T backingField, T value, [CallerMemberName] string propName = null)
        {
            bool valueChanged = false;

            if (!EqualityComparer<T>.Default.Equals(backingField, value))
            {
                backingField = value;
                RaisePropertyChanged(propName);
                valueChanged = true;
            }

            return valueChanged;
        }

        public event PropertyChangedEventHandler PropertyChanged = delegate { };

        protected void RaisePropertyChanged([CallerMemberName] string propName = null)
        {
            if (!string.IsNullOrWhiteSpace(propName) && (PropertyChanged != null))
            {
                var ctx = _uiContext;
                if (ctx != null && ctx != SynchronizationContext.Current)
                {
                    ctx.Post(_ => PropertyChanged(this, new PropertyChangedEventArgs(propName)), null);
                }
                else
                {
                    PropertyChanged(this, new PropertyChangedEventArgs(propName));
                }
            }
        }
    }

    public class SimpleCommand : ICommand
    {
        public SimpleCommand(Action action)
        {
            Action = action;
        }

        public Action Action { get; set; }

        public bool CanExecute(object parameter)
        {
            return (Action != null);
        }

        public event EventHandler CanExecuteChanged
        {
            add { }
            remove { }
        }

        public void Execute(object parameter)
        {
            Action?.Invoke();
        }
    }

    /// <summary>
    /// Simple thickness record replacing System.Windows.Thickness
    /// </summary>
    public record SimpleThickness(double Left, double Top, double Right, double Bottom)
    {
        public SimpleThickness() : this(0, 0, 0, 0) { }
    }

    public class TreeNode : INPC
    {
        private const double INTACT_CHANCE_RARE = 0.02;
        private const double RADIANT_CHANCE_RARE = 0.1;
        private const double INTACT_CHANCE_UNCOMMON = 0.11;
        private const double RADIANT_CHANCE_UNCOMMON = 0.2;
        private const double INTACT_CHANCE_COMMON = 0.2533;
        private const double RADIANT_CHANCE_COMMON = 0.1667;

        // Image keys (resource paths) replacing WPF ImageSource
        private static string PLAT_SRC = "plat";
        private static string DUCAT_SRC = "ducat_w";

        // Colors as (R,G,B) bytes replacing WPF Color
        public static (byte R, byte G, byte B) RARE_COLOR = (255, 215, 0);
        public static (byte R, byte G, byte B) UNCOMMON_COLOR = (192, 192, 192);
        public static (byte R, byte G, byte B) COMMON_COLOR = (205, 127, 50);

        // Hex color strings replacing WPF Brush
        public static string RARE_HEX = "#FFD700";
        public static string UNCOMMON_HEX = "#C0C0C0";
        public static string COMMON_HEX = "#CD7F32";

        public static string BACK_D_HEX = "#161616";
        public static string BACK_HEX = "#1B1B1B";
        public static string BACK_U_HEX = "#202020";

        public TreeNode(string name, string vaulted, bool mastered, byte showAll)
        {
            Name = name;
            Vaulted = vaulted;
            Mastered = mastered;
            ShowAll = showAll;
            ChildrenFiltered = new List<TreeNode>();
            Children = new List<TreeNode>();
            SetSilent();
        }

        public object ShowAll { get; set; }

        public bool topLevel = false;

        private string _era;
        public string Era
        {
            get { return _era; }
            set { SetField(ref _era, value); }
        }

        private int _sortNum = -1;
        public int SortNum
        {
            get { return _sortNum; }
            set { SetField(ref _sortNum, value); }
        }

        private int _depth;
        public int Depth
        {
            get { return _depth; }
            set { SetField(ref _depth, value); }
        }

        public string ExpandIndicator => Children.Count > 0 ? (IsExpanded ? "▼" : "▶") : "";

        private string _name;
        public string Name
        {
            get { return topLevel ? _era + " " + _name : _name; }
            set { SetField(ref _name, value); }
        }
        public string Name_Sort
        {
            get { return current != null ? current.SortNum + _era + " " + _name : SortNum + _name; }
            set { SetField(ref _name, value); }
        }
        public string EqmtName_Sort
        {
            get { return SortNum + _name; }
            set { SetField(ref _name, value); }
        }

        private string _nameColorHex = "#B1D0D9";
        public string NameColorHex
        {
            get { return _nameColorHex; }
            set { SetField(ref _nameColorHex, value); }
        }

        private (byte R, byte G, byte B) _nameColor = (177, 208, 217);
        public (byte R, byte G, byte B) NameColor
        {
            get { return _nameColor; }
            set { SetField(ref _nameColor, value); }
        }

        private string _backgroundColorHex = "#1B1B1B";
        public string Background_Color
        {
            get { return _backgroundColorHex; }
            set { SetField(ref _backgroundColorHex, value); }
        }

        private SimpleThickness _col1margin = new SimpleThickness(0, 0, 18, 0);
        public SimpleThickness Col1_Margin1
        {
            get { return _col1margin; }
            set { SetField(ref _col1margin, value); }
        }

        private SimpleThickness _col1margin2 = new SimpleThickness(0, 0, 0, 0);
        public SimpleThickness Col1_Margin2
        {
            get { return _col1margin2; }
            set { SetField(ref _col1margin2, value); }
        }

        private SimpleThickness _col2margin = new SimpleThickness(0, 0, 18, 0);
        public SimpleThickness Col2_Margin1
        {
            get { return _col2margin; }
            set { SetField(ref _col2margin, value); }
        }

        private SimpleThickness _col2margin2 = new SimpleThickness(0, 0, 0, 0);
        public SimpleThickness Col2_Margin2
        {
            get { return _col2margin2; }
            set { SetField(ref _col2margin2, value); }
        }

        private string _vaulted;
        public string Vaulted
        {
            get { return _vaulted; }
            set { SetField(ref _vaulted, value); }
        }

        public bool IsVaulted()
        {
            return !string.IsNullOrEmpty(Vaulted);
        }

        public void SetSilent()
        {
            IsGridVisible = true;

            Col1_Text1 = "";
            Col1_Text2 = "";
            Col1_Img1 = null;
            IsCol1Img1Visible = false;

            Col2_Text1 = "";
            Col2_Text2 = "";
            Col2_Text3 = "";
            Col2_Img1 = null;
            IsCol2Img1Visible = false;
        }

        public void SetEraText()
        {
            _intact = 0;
            _radiant = 0;

            foreach (TreeNode node in Children)
            {
                if (!node.IsVaulted())
                {
                    _intact += node._intact;
                    _radiant += node._radiant;
                }
            }

            _bonus = _radiant - _intact;

            Col1_Text1 = "INT:";
            Col1_Text2 = _intact.ToString("F1");

            Col1_Img1 = PLAT_SRC;
            IsCol1Img1Visible = true;

            Col2_Text1 = "RAD:";
            Col2_Text2 = _radiant.ToString("F1");
            int tempBonus = (int)(_bonus * 10);
            Col2_Text3 = "(";
            if (tempBonus >= 0)
                Col2_Text3 += "+";
            Col2_Text3 += (tempBonus / 10.0).ToString("F1") + ")";

            Col2_Img1 = PLAT_SRC;
            IsCol2Img1Visible = true;
        }

        public void SetRelicText()
        {
            _intact = 0;
            _radiant = 0;

            foreach (TreeNode node in Children)
            {
                if (node.NameColor == RARE_COLOR)
                {
                    _intact += INTACT_CHANCE_RARE * node._plat;
                    _radiant += RADIANT_CHANCE_RARE * node._plat;
                }
                else if (node.NameColor == UNCOMMON_COLOR)
                {
                    _intact += INTACT_CHANCE_UNCOMMON * node._plat;
                    _radiant += RADIANT_CHANCE_UNCOMMON * node._plat;
                }
                else
                {
                    _intact += INTACT_CHANCE_COMMON * node._plat;
                    _radiant += RADIANT_CHANCE_COMMON * node._plat;
                }
            }

            _bonus = _radiant - _intact;
            IsGridVisible = true;

            Col1_Text1 = "INT:";
            Col1_Text2 = _intact.ToString("F1");

            Col1_Img1 = PLAT_SRC;
            IsCol1Img1Visible = true;

            Col2_Text1 = "RAD:";
            Col2_Text2 = _radiant.ToString("F1");
            int tempBonus = (int)(_bonus * 10);
            Col2_Text3 = "(";
            if (tempBonus >= 0)
                Col2_Text3 += "+";
            Col2_Text3 += (tempBonus / 10.0).ToString("F1") + ")";

            Col2_Img1 = PLAT_SRC;
            IsCol2Img1Visible = true;
        }

        public bool GetSetInfo()
        {
            string primeSetName = Data.GetSetName(Name);
            if (!AppMain.dataBase.marketData.TryGetValue(primeSetName, out JToken primeSetJToken))
            {
                return false;
            }
            JObject primeSet = (JObject)primeSetJToken;

            string primeSetPlat = primeSet["plat"].ToObject<string>();

            IsGridVisible = true;
            Plat_Val = double.Parse(primeSetPlat, AppMain.culture);
            Owned_Capped_Val = 0;
            Owned_Plat_Val = 0;
            Owned_Ducat_Val = 0;
            Owned_Val = 0;
            Count_Val = 0;
            Mastered = AppMain.dataBase.equipmentData[this.dataRef]["mastered"].ToObject<bool>();
            foreach (TreeNode kid in Children)
            {
                Owned_Capped_Val += kid.Owned_Capped_Val;
                Owned_Plat_Val += kid.Owned_Plat_Val;
                Owned_Ducat_Val += kid.Owned_Ducat_Val;
                Owned_Val += kid.Owned_Val;
                Count_Val += kid.Count_Val;
            }

            PrimeUpdateDiff(true);
            Col1_Text2 = _plat.ToString("F1");

            Col1_Img1 = PLAT_SRC;
            IsCol1Img1Visible = true;
            return true;
        }

        public void SetPrimeEqmt(double plat, double ducat, int owned, int count)
        {
            Plat_Val = plat;
            Owned_Capped_Val = Math.Min(owned, count);
            Owned_Plat_Val = owned * plat;
            Owned_Ducat_Val = owned * ducat;
            Owned_Val = owned;
            Count_Val = count;

            PrimeUpdateDiff(false);
            Col1_Text2 = _plat.ToString("F1");

            Col1_Img1 = PLAT_SRC;
            IsCol1Img1Visible = true;

            Col2_Text1 = "";
            Col2_Text2 = "";
            Col2_Text3 = "";
            Col2_Img1 = null;
            IsCol2Img1Visible = false;
        }

        public void ChangeExpandedTo(bool expand)
        {
            IsExpanded = expand;
            foreach (TreeNode kid in Children)
                kid.ChangeExpandedTo(expand);
        }

        public void SetPrimePart(double plat, int ducat, int owned, int count)
        {
            SetPrimeEqmt(plat, ducat, owned, count);
            Col2_Text3 = ducat.ToString();
            Col2_Img1 = DUCAT_SRC;
            IsCol2Img1Visible = true;
            Col2_Margin1 = new SimpleThickness(0, 0, 28, 0);
            Col2_Margin2 = new SimpleThickness(0, 0, 10, 0);
        }

        public void SetPartText(double plat, int ducat, string rarity)
        {
            if (rarity.Contains("rare"))
            {
                NameColor = RARE_COLOR;
                NameColorHex = RARE_HEX;
            }
            else if (rarity.Contains("uncomm"))
            {
                NameColor = UNCOMMON_COLOR;
                NameColorHex = UNCOMMON_HEX;
            }
            else if (rarity.Contains("comm"))
            {
                NameColor = COMMON_COLOR;
                NameColorHex = COMMON_HEX;
            }

            if (Name != "Forma Blueprint")
            {
                _plat = plat;
                _ducat = ducat;

                Col1_Text1 = "";
                Col1_Text2 = _plat.ToString("F1");

                Col1_Img1 = PLAT_SRC;
                IsCol1Img1Visible = true;
                Col1_Margin1 = new SimpleThickness(0, 0, 38, 0);
                Col1_Margin2 = new SimpleThickness(0, 0, 20, 0);

                Col2_Text1 = "";
                Col2_Text2 = "";
                Col2_Text3 = ducat.ToString();
                Col2_Img1 = DUCAT_SRC;
                IsCol2Img1Visible = true;
                Col2_Margin1 = new SimpleThickness(0, 0, 78, 0);
                Col2_Margin2 = new SimpleThickness(0, 0, 60, 0);
            }
            else
            {
                Col1_Img1 = null;
                Col1_Text1 = "";
                Col1_Text2 = "";

                Col2_Img1 = null;
                Col2_Text1 = "";
                Col2_Text2 = "";
            }
        }

        public void ResetFilter()
        {
            foreach (TreeNode node in Children)
                node.ResetFilter();

            ForceVisibility = false;
            if (!ReferenceEquals(_childrenFiltered, _children))
                ChildrenFiltered = Children;
        }

        public void FilterOutVaulted(bool additionalFilter = false)
        {
            List<TreeNode> filterList = additionalFilter ? ChildrenFiltered : Children;
            ChildrenFiltered = filterList.Where(node => !node.IsVaulted()).ToList();
        }

        public void RecolorChildren()
        {
            bool i = false;
            foreach (TreeNode child in ChildrenFiltered)
            {
                i = !i;
                if (i)
                    child.Background_Color = BACK_D_HEX;
                else
                    child.Background_Color = BACK_U_HEX;
            }
        }

        public bool FilterSearchText(string[] searchText, bool removeLeaves, bool additionalFilter = false, Dictionary<string, bool> matchedText = null)
        {
            Dictionary<string, bool> matchedTextCopy = new Dictionary<string, bool>();

            bool done = true;
            foreach (string text in searchText)
            {
                bool tempVal = (matchedText != null && matchedText[text]) || Name.Contains(text, StringComparison.OrdinalIgnoreCase);
                matchedTextCopy[text] = tempVal;
                done = done && tempVal;
            }

            List<TreeNode> filterList = additionalFilter ? ChildrenFiltered : Children;
            if (done)
            {
                if (ChildrenFiltered.Count > 0)
                    ChildrenFiltered = filterList;
                else
                    ForceVisibility = true;

                return true;
            }

            List<TreeNode> temp = new List<TreeNode>();
            foreach (TreeNode node in filterList)
                if (node.FilterSearchText(searchText, removeLeaves, additionalFilter, matchedTextCopy))
                    temp.Add(node);

            if (temp.Count == Children.Count)
                foreach (TreeNode node in filterList)
                    node.ForceVisibility = false;

            ChildrenFiltered = (filterList.Count > 0 && filterList[0].ChildrenFiltered.Count > 0) || removeLeaves ? temp : filterList;
            return temp.Count > 0;
        }

        public void Sort(int index, bool isRelics = true, int depth = 0)
        {
            foreach (TreeNode node in Children)
                node.Sort(index, isRelics, depth + 1);
            if (Children.Count <= 1) return;

            Comparison<TreeNode> cmp;
            if (isRelics)
            {
                if (depth == 0)
                {
                    cmp = index switch
                    {
                        1 => (a, b) => b._intact.CompareTo(a._intact),
                        2 => (a, b) => b._radiant.CompareTo(a._radiant),
                        3 => (a, b) => b._bonus.CompareTo(a._bonus),
                        _ => (a, b) => string.Compare(PadNumbers(a.Name), PadNumbers(b.Name), StringComparison.Ordinal)
                    };
                }
                else
                {
                    cmp = (a, b) => b.NameColor.G.CompareTo(a.NameColor.G);
                }
            }
            else
            {
                cmp = index switch
                {
                    1 => (a, b) => b._plat.CompareTo(a._plat),
                    2 => (a, b) => { int r = a._diff.CompareTo(b._diff); return r != 0 ? r : a._owned_capped.CompareTo(b._owned_capped); },
                    3 => (a, b) => b._owned.CompareTo(a._owned),
                    4 => (a, b) => b._owned_plat.CompareTo(a._owned_plat),
                    5 => (a, b) => b._owned_ducat.CompareTo(a._owned_ducat),
                    _ => (a, b) => string.Compare(PadNumbers(a.Name), PadNumbers(b.Name), StringComparison.Ordinal)
                };
            }

            _children.Sort(cmp);
            if (!ReferenceEquals(_children, _childrenFiltered) && _childrenFiltered.Count > 1)
                _childrenFiltered.Sort(cmp);
        }

        public static string PadNumbers(string input)
        {
            return System.Text.RegularExpressions.Regex.Replace(input, "[0-9]+", match => match.Value.PadLeft(5, '0'));
        }

        private string _col1_text1 = "INT:";
        public string Col1_Text1
        {
            get { return _col1_text1; }
            private set { SetField(ref _col1_text1, value); }
        }

        private string _col1_text2 = "4.4";
        public string Col1_Text2
        {
            get { return _col1_text2; }
            private set { SetField(ref _col1_text2, value); }
        }

        private string _col1_img1 = null;
        public string Col1_Img1
        {
            get { return _col1_img1; }
            private set { SetField(ref _col1_img1, value); }
        }

        private bool _isGridVisible = true;
        public bool IsGridVisible
        {
            get { return _isGridVisible; }
            private set { SetField(ref _isGridVisible, value); }
        }

        private bool _isCol1Img1Visible = true;
        public bool IsCol1Img1Visible
        {
            get { return _isCol1Img1Visible; }
            private set { SetField(ref _isCol1Img1Visible, value); }
        }

        private string _col2_text1 = "RAD:";
        public string Col2_Text1
        {
            get { return _col2_text1; }
            private set { SetField(ref _col2_text1, value); }
        }

        private string _col2_text2 = "9.9";
        public string Col2_Text2
        {
            get { return _col2_text2; }
            private set { SetField(ref _col2_text2, value); }
        }

        private string _col2_text3 = "(+5.5)";
        public string Col2_Text3
        {
            get { return _col2_text3; }
            private set { SetField(ref _col2_text3, value); }
        }

        private string _col2_img1 = null;
        public string Col2_Img1
        {
            get { return _col2_img1; }
            private set
            {
                SetField(ref _col2_img1, value);
                RaisePropertyChanged(nameof(IsCol2PlatVisible));
                RaisePropertyChanged(nameof(IsCol2DucatVisible));
            }
        }

        private bool _isCol2Img1Visible = true;
        public bool IsCol2Img1Visible
        {
            get { return _isCol2Img1Visible; }
            private set
            {
                SetField(ref _isCol2Img1Visible, value);
                RaisePropertyChanged(nameof(IsCol2PlatVisible));
                RaisePropertyChanged(nameof(IsCol2DucatVisible));
            }
        }

        public bool IsCol2PlatVisible => IsCol2Img1Visible && Col2_Img1 == PLAT_SRC;
        public bool IsCol2DucatVisible => IsCol2Img1Visible && Col2_Img1 == DUCAT_SRC;

        private double _plat = 0;
        public double Plat_Val
        {
            get { return _plat; }
            set { SetField(ref _plat, value); }
        }

        private int _ducat = 0;
        public int Ducat_Val
        {
            get { return _ducat; }
            set { SetField(ref _ducat, value); }
        }

        private int _owned = 0;
        public int Owned_Val
        {
            get { return _owned; }
            set { SetField(ref _owned, value); }
        }

        private int _owned_capped = 0;
        public int Owned_Capped_Val
        {
            get { return _owned_capped; }
            set { SetField(ref _owned_capped, value); }
        }

        private double _owned_plat = 0;
        public double Owned_Plat_Val
        {
            get { return _owned_plat; }
            set { SetField(ref _owned_plat, value); }
        }

        private double _owned_ducat = 0;
        public double Owned_Ducat_Val
        {
            get { return _owned_ducat; }
            set { SetField(ref _owned_ducat, value); }
        }

        private int _count = 0;
        public int Count_Val
        {
            get { return _count; }
            set { SetField(ref _count, value); }
        }

        private double _diff = 0;
        public double Diff_Val
        {
            get { return _diff; }
            set { SetField(ref _diff, value); }
        }

        private double _intact = 0;
        public double Intact_Val
        {
            get { return _intact; }
            set { SetField(ref _intact, value); }
        }

        private double _radiant = 0;
        public double Radiant_Val
        {
            get { return _radiant; }
            set { SetField(ref _radiant, value); }
        }

        private double _bonus = 0;
        public double Bonus_Val
        {
            get { return _bonus; }
            set { SetField(ref _bonus, value); }
        }

        public bool IsVisible
        {
            get { return _forceVisibility || current == null || current.IsExpanded || topLevel; }
        }

        private bool _forceVisibility = false;
        public bool ForceVisibility
        {
            get { return _forceVisibility; }
            set
            {
                SetField(ref _forceVisibility, value);
                RaisePropertyChanged("IsVisible");
            }
        }

        private bool _isExpanded = false;
        public bool IsExpanded
        {
            get { return _isExpanded; }
            set
            {
                SetField(ref _isExpanded, value);
                RaisePropertyChanged(nameof(ExpandIndicator));
                foreach (TreeNode kid in Children)
                    kid.RaisePropertyChanged("IsVisible");
            }
        }

        private List<TreeNode> _childrenFiltered;
        public List<TreeNode> ChildrenFiltered
        {
            get { return _childrenFiltered; }
            private set { SetField(ref _childrenFiltered, value); }
        }

        private List<TreeNode> _children;
        public List<TreeNode> Children
        {
            get { return _children; }
            private set { SetField(ref _children, value); }
        }

        private bool _mastered = false;
        public bool Mastered
        {
            get { return _mastered; }
            set { SetField(ref _mastered, value); }
        }

        public bool IsLeaf => Children.Count == 0;
        public bool IsParentNode => Children.Count > 0;

        public TreeNode current;
        public void AddChild(TreeNode kid)
        {
            kid.current = this;
            Children.Add(kid);
        }

        public override string ToString()
        {
            return Era + " " + Name;
        }

        private ICommand _decrement;
        public ICommand DecrementPart
        {
            get { return _decrement; }
            private set { SetField(ref _decrement, value); }
        }

        private ICommand _increment;
        public ICommand IncrementPart
        {
            get { return _increment; }
            private set { SetField(ref _increment, value); }
        }

        private ICommand _markcomplete;
        public ICommand MarkComplete
        {
            get { return _markcomplete; }
            private set { SetField(ref _markcomplete, value); }
        }

        private string dataRef;
        private static readonly object _ownedLock = new object();

        public void MakeClickable(string eqmtRef)
        {
            dataRef = eqmtRef;
            DecrementPart = new SimpleCommand(DecrementPartFunc);
            IncrementPart = new SimpleCommand(IncrementPartFunc);
            MarkComplete = new SimpleCommand(MarkCompleteFunc);
        }

        public async void DecrementPartFunc()
        {
            try
            {
                if (current.dataRef != null)
                {
                    await System.Threading.Tasks.Task.Run(() => DecrementPartThreaded(current));
                }
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"ERROR: DecrementPart failed: {ex.Message}");
            }
        }

        public async void IncrementPartFunc()
        {
            try
            {
                if (current.dataRef != null)
                {
                    await System.Threading.Tasks.Task.Run(() => IncrementPartThreaded(current));
                }
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"ERROR: IncrementPart failed: {ex.Message}");
            }
        }

        public async void MarkCompleteFunc()
        {
            try
            {
                await System.Threading.Tasks.Task.Run(() => MarkSetAsComplete());
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"ERROR: MarkComplete failed: {ex.Message}");
            }
        }

        public void ReloadPartOwned(TreeNode Parent)
        {
            JObject job = AppMain.dataBase.equipmentData[Parent.dataRef]["parts"][dataRef] as JObject;
            Owned_Val = job["owned"].ToObject<int>();
            Owned_Capped_Val = Math.Min(Owned_Val, Count_Val);
            Owned_Plat_Val = Owned_Val * Plat_Val;
            Owned_Ducat_Val = Owned_Val * Ducat_Val;
            PrimeUpdateDiff(false);
        }

        private void DecrementPartThreaded(TreeNode Parent)
        {
            lock (_ownedLock)
            {
                JObject job = AppMain.dataBase.equipmentData[Parent.dataRef]["parts"][dataRef] as JObject;
                int owned = Owned_Val;
                if (owned > 0)
                {
                    job["owned"] = owned - 1;
                    AppMain.dataBase.SaveAllJSONs();
                    Owned_Val--;
                    Owned_Capped_Val = Math.Min(Owned_Val, Count_Val);
                    Owned_Plat_Val = Owned_Val * Plat_Val;
                    Owned_Ducat_Val = Owned_Val * Ducat_Val;
                    PrimeUpdateDiff(false);
                    int count = Count_Val;
                    Parent.Owned_Val--;
                    Parent.Owned_Plat_Val -= Plat_Val;
                    Parent.Owned_Ducat_Val -= Ducat_Val;
                    if (owned <= count)
                    {
                        Parent.Owned_Capped_Val--;
                        Parent.PrimeUpdateDiff(true);
                    }
                }
            }
        }

        private void IncrementPartThreaded(TreeNode Parent)
        {
            lock (_ownedLock)
            {
                JObject job = AppMain.dataBase.equipmentData[Parent.dataRef]["parts"][dataRef] as JObject;
                int count = Count_Val;
                int owned = Owned_Val;
                job["owned"] = owned + 1;
                AppMain.dataBase.SaveAllJSONs();
                Owned_Val++;
                Owned_Capped_Val = Math.Min(Owned_Val, Count_Val);
                Owned_Plat_Val = Owned_Val * Plat_Val;
                Owned_Ducat_Val = Owned_Val * Ducat_Val;
                PrimeUpdateDiff(false);
                Parent.Owned_Val++;
                Parent.Owned_Plat_Val += Plat_Val;
                Parent.Owned_Ducat_Val += Ducat_Val;
                if (owned < count)
                {
                    Parent.Owned_Capped_Val++;
                    Parent.PrimeUpdateDiff(true);
                }
            }
        }

        private void MarkSetAsComplete()
        {
            lock (_ownedLock)
            {
                AppMain.dataBase.equipmentData[this.dataRef]["mastered"] = !Mastered;
                Mastered = !Mastered;
                AppMain.dataBase.SaveAllJSONs();
            }
        }

        private void PrimeUpdateDiff(bool UseCappedOwned)
        {
            int owned = Owned_Val;
            if (UseCappedOwned)
            {
                owned = Owned_Capped_Val;
            }
            // Completion progress: fraction owned minus small penalty for larger sets
            Diff_Val = owned / (double)Math.Max(Count_Val, 1) - 0.01 * Count_Val;
            Col1_Text1 = owned + "/" + Count_Val;
        }
    }
}
