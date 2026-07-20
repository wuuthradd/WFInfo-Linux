using System;
using System.Collections.Generic;
using System.Linq;

namespace WFInfo.Models
{
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

        // Sale: user gives items, receives only Platinum
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
        public string Partner { get; set; }
        public string Status { get; set; } = "";
    }
}