using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace WFInfo
{
    public enum WFtheme : int
    {
        VITRUVIAN = 0,
        STALKER = 1,
        BARUUK = 2,
        CORPUS = 3,
        FORTUNA = 4,
        GRINEER = 5,
        LOTUS = 6,
        NIDUS = 7,
        OROKIN = 8,
        TENNO = 9,
        HIGH_CONTRAST = 10,
        LEGACY = 11,
        EQUINOX = 12,
        DARK_LOTUS = 13,
        ZEPHYR = 14,
        CONQUERA = 15,
        DEADLOCK = 16,
        LUNAR_RENEWAL = 17,
        POM_2 = 18,
        UNKNOWN = -1,
        AUTO = -2,
        CUSTOM = -3
    }

    public enum Display
    {
        Window,
        Overlay,
        Light
    }
}

namespace WFInfo.Models
{
    public enum VirtualKey
    {
        None = 0,
        A = 44, B = 45, C = 46, D = 47, E = 48, F = 49, G = 50, H = 51,
        I = 52, J = 53, K = 54, L = 55, M = 56, N = 57, O = 58, P = 59,
        Q = 60, R = 61, S = 62, T = 63, U = 64, V = 65, W = 66, X = 67,
        Y = 68, Z = 69,
        D0 = 34, D1 = 35, D2 = 36, D3 = 37, D4 = 38, D5 = 39,
        D6 = 40, D7 = 41, D8 = 42, D9 = 43,
        F1 = 90, F2 = 91, F3 = 92, F4 = 93, F5 = 94, F6 = 95,
        F7 = 96, F8 = 97, F9 = 98, F10 = 99, F11 = 100, F12 = 101,
        LeftShift = 116, RightShift = 117, LeftCtrl = 118, RightCtrl = 119,
        LeftAlt = 120, RightAlt = 121,
        Space = 18, Enter = 6, Escape = 13, Tab = 3, Back = 2,
        Delete = 32, Insert = 31, Home = 22, End = 21,
        PageUp = 19, PageDown = 20,
        Left = 23, Up = 24, Right = 25, Down = 26,
        PrintScreen = 30,
        NumPad0 = 74, NumPad1 = 75, NumPad2 = 76, NumPad3 = 77,
        NumPad4 = 78, NumPad5 = 79, NumPad6 = 80, NumPad7 = 81,
        NumPad8 = 82, NumPad9 = 83,
        OemTilde = 130, OemMinus = 131, OemPlus = 132,
        OemOpenBrackets = 133, OemCloseBrackets = 134,
        OemPipe = 135, OemSemicolon = 136, OemQuotes = 137,
        OemComma = 138, OemPeriod = 139, OemSlash = 140,
        OemBackslash = 141,
    }

    public enum VirtualMouseButton
    {
        Left = 0,
        Middle = 1,
        Right = 2,
        XButton1 = 3,
        XButton2 = 4
    }

    public class RewardCollection
    {
        public List<string> PrimeNames { get; set; } = new(4);
        public List<short> PlatinumValues { get; set; } = new(4);
        public List<JObject> MarketResults { get; set; } = new(4);
        public short RewardIndex { get; set; } = 0;

        public RewardCollection(List<string> primeNames, List<short> platinumValues,
            List<JObject> marketResults, short rewardIndex)
        {
            PrimeNames = primeNames;
            PlatinumValues = platinumValues;
            MarketResults = marketResults;
            RewardIndex = rewardIndex;
        }
    }

    public class TradeItem
    {
        public string Name { get; set; }
        public int Count { get; set; }
        public int? Rank { get; set; }

        public TradeItem(string name, int count, int? rank = null)
        {
            Name = name;
            Count = count;
            Rank = rank;
        }
    }

    public class TradeInfo
    {
        public List<TradeItem> Given { get; set; } = new();
        public List<TradeItem> Received { get; set; } = new();
        public string Partner { get; set; }
        public DateTime Timestamp { get; set; }

        public bool IsSale => Received.Count > 0
            && Received.All(r => r.Name.Equals("Platinum", StringComparison.OrdinalIgnoreCase))
            && Given.Count > 0
            && !Given.All(g => g.Name.Equals("Platinum", StringComparison.OrdinalIgnoreCase));

        public int PlatinumReceived => IsSale
            ? Received.Where(r => r.Name.Equals("Platinum", StringComparison.OrdinalIgnoreCase)).Sum(r => r.Count)
            : 0;
    }

    public class TradeDoneEntry
    {
        public string ItemName { get; set; }
        public int Count { get; set; }
        public string MatchedOrderId { get; set; }
        public string MatchedItemName { get; set; }
        public int MatchedPlatinum { get; set; }
        public int MatchedQuantity { get; set; }
        public int? MatchedRank { get; set; }
        public string Partner { get; set; }
        public string Status { get; set; } = "";
    }
}
