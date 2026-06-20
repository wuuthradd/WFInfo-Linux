using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WFInfo.Settings;

namespace WFInfo.LanguageProcessing
{
    /// <summary>
    /// Turkish language processor for OCR text processing
    /// Handles Turkish characters with special diacritics
    /// </summary>
    public class TurkishLanguageProcessor : LanguageProcessor
    {
        public TurkishLanguageProcessor(IReadOnlyApplicationSettings settings) : base(settings)
        {
        }

        public override string Locale => "tr";

        public override string[] BlueprintRemovals => new[] { "Plan", "Şema" };

        private static readonly IReadOnlyDictionary<string, string> _ignoredItemNames = new Dictionary<string, string>
        {
            ["Forma Blueprint"] = "Forma Plan",
            ["Exilus Weapon Adapter Blueprint"] = "Silah Exilus Adaptörü Plan",
            ["Kuva"] = "Kuva",
            ["Riven Sliver"] = "Riven Parçası",
            ["Ayatan Amber Star"] = "Ayatan Amber Yıldızı",
            ["Galariak Prime Blueprint"] = "Galariak Prime Plan",
            ["Galariak Prime Blade"] = "Galariak Prime Bıçak",
            ["Galariak Prime Handle"] = "Galariak Prime Kabza",
            ["Sagek Prime Blueprint"] = "Sagek Prime Plan",
            ["Sagek Prime Barrel"] = "Sagek Prime Namlu",
            ["Sagek Prime Receiver"] = "Sagek Prime Alıcı"
        };

        public override IReadOnlyDictionary<string, string> IgnoredItemNames => _ignoredItemNames;

        public override string CharacterWhitelist => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz " + "ÇçĞğİıÖöŞşÜü"; // Turkish-specific characters

        public override int CalculateLevenshteinDistance(string s, string t)
        {
            return LevenshteinDistanceWithPreprocessing(s, t, BlueprintRemovals, NormalizeTurkishCharacters, callBaseDefault: true);
        }

        public override string NormalizeForPatternMatching(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            string normalized = input.ToLower(_culture).Trim();
            normalized = normalized.Replace("prime", " prime ");
            normalized = RemoveAccents(normalized);
            var parts = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts);
        }

        public override bool IsPartNameValid(string partName)
        {
            return !string.IsNullOrEmpty(partName) && partName.Replace(" ", "").Length >= 6;
        }

        /// <summary>
        /// Checks if a part name is an ignored item with Turkish diacritics normalization.
        /// Normalizes input to handle OCR that loses Turkish diacritics.
        /// </summary>
        public override bool IsIgnoredItem(string partName)
        {
            if (string.IsNullOrEmpty(partName))
                return false;

            // Normalize input to handle OCR without diacritics
            string normalizedInput = NormalizeTurkishCharacters(partName);
            var ignoredSet = GetIgnoredItemNamesHashSet();

            // Check raw input first
            if (ignoredSet.Contains(partName))
                return true;

            // Check normalized input against normalized set values
            foreach (var item in ignoredSet)
            {
                if (NormalizeTurkishCharacters(item).Equals(normalizedInput, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Normalizes Turkish characters to standard equivalents for comparison
        /// </summary>
        private static readonly System.Globalization.CultureInfo _turkishCulture = new System.Globalization.CultureInfo("tr-TR");

        private static string NormalizeTurkishCharacters(string input)
        {
            // Handle Turkish dotted/dotless I explicitly before any casing to avoid Unicode edge cases
            string result = input
                .Replace('İ', 'i') // U+0130 LATIN CAPITAL LETTER I WITH DOT ABOVE → i
                .Replace('I', 'ı'); // U+0049 LATIN CAPITAL LETTER I → ı (Turkish dotless)
            // Lowercase with Turkish culture so ş/ğ/ç/ö/ü fold correctly
            result = result.ToLower(_turkishCulture);
            result = result
                .Replace('ğ', 'g')
                .Replace('ş', 's')
                .Replace('ç', 'c')
                .Replace('ö', 'o')
                .Replace('ü', 'u')
                .Replace('ı', 'i'); // dotless i → i
            return result;
        }
    }
}
