using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WFInfo.Settings;

namespace WFInfo.LanguageProcessing
{
    /// <summary>
    /// English language processor for OCR text processing
    /// Handles standard English text with basic normalization
    /// </summary>
    public class EnglishLanguageProcessor : LanguageProcessor
    {
        public EnglishLanguageProcessor(IReadOnlyApplicationSettings settings) : base(settings)
        {
        }

        public override string Locale => "en";

        public override string[] BlueprintRemovals => new[] { "Blueprint" };

        private static readonly IReadOnlyDictionary<string, string> _ignoredItemNames = new Dictionary<string, string>
        {
            ["Forma Blueprint"] = "Forma Blueprint",
            ["Exilus Weapon Adapter Blueprint"] = "Exilus Weapon Adapter Blueprint",
            ["Kuva"] = "Kuva",
            ["Riven Sliver"] = "Riven Sliver",
            ["Ayatan Amber Star"] = "Ayatan Amber Star",
            ["Ayatan Cyan Star"] = "Ayatan Cyan Star",
            ["Galariak Prime Blueprint"] = "Galariak Prime Blueprint",
            ["Galariak Prime Blade"] = "Galariak Prime Blade",
            ["Galariak Prime Handle"] = "Galariak Prime Handle",
            ["Sagek Prime Blueprint"] = "Sagek Prime Blueprint",
            ["Sagek Prime Barrel"] = "Sagek Prime Barrel",
            ["Sagek Prime Receiver"] = "Sagek Prime Receiver"
        };

        public override IReadOnlyDictionary<string, string> IgnoredItemNames => _ignoredItemNames;

        public override string CharacterWhitelist => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz ";

        public override int CalculateLevenshteinDistance(string s, string t)
        {
            // Min of raw and space-free comparison. Space-free fixes concatenated OCR words,
            // raw handles garbled fragments. No Blueprint stripping to avoid asymmetric matching.
            int raw = DefaultLevenshteinDistance(s, t);
            int noSpaces = DefaultLevenshteinDistance(Regex.Replace(s, @"\s", ""), Regex.Replace(t, @"\s", ""));
            return Math.Min(raw, noSpaces);
        }

        public override string NormalizeForPatternMatching(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            string normalized = input.ToLower(_culture).Trim();

            normalized = normalized.Replace("prime", " prime ");

            var parts = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts);
        }

        public override bool IsPartNameValid(string partName)
        {
            return !string.IsNullOrEmpty(partName) && partName.Length >= 13;
        }

        public override string RemoveBlueprintTerms(string localizedName)
        {
            if (string.IsNullOrEmpty(localizedName))
                return localizedName;

            string result = base.RemoveBlueprintTerms(localizedName);

            // Handle no-space concatenation
            result = Regex.Replace(result, "\\s*Blueprint\\s*$", "", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, "\\s*Blueprint\\s+", " ", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, "^Blueprint\\s*[:\\-–—]?\\s*", "", RegexOptions.IgnoreCase);

            return result.Trim();
        }
    }
}
