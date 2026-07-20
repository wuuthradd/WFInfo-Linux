using System.Collections.Generic;
using Newtonsoft.Json.Linq;

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
}