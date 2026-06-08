using System.Collections.Generic;

namespace WFInfo.Models
{
    public class MarketListing
    {
        public short Platinum { get; set; }
        public short Amount { get; set; }
        public short Reputation { get; set; }

        public MarketListing(short platinum, short amount, short reputation)
        {
            Platinum = platinum;
            Amount = amount;
            Reputation = reputation;
        }

    }

    public class RewardCollection
    {
        public List<string> PrimeNames { get; set; } = new(4);
        public List<short> PlatinumValues { get; set; } = new(4);
        public List<List<MarketListing>> MarketListings { get; set; } = new(4);
        public short RewardIndex { get; set; } = 0;

        public RewardCollection(List<string> primeNames, List<short> platinumValues,
            List<List<MarketListing>> marketListings, short rewardIndex)
        {
            PrimeNames = primeNames;
            PlatinumValues = platinumValues;
            MarketListings = marketListings;
            RewardIndex = rewardIndex;
        }

    }
}