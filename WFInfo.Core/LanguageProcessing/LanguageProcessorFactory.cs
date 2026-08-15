using System;
using System.Collections.Generic;
using WFInfo.Settings;

namespace WFInfo.LanguageProcessing
{
    public class LanguageProcessorFactory
    {
        private static readonly Dictionary<string, LanguageProcessor> _processors = new Dictionary<string, LanguageProcessor>();
        private static readonly object _lock = new object();
        private static IReadOnlyApplicationSettings _settings;

        public static void Initialize(IReadOnlyApplicationSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            _settings = settings;
            ClearCache();
        }

        public static LanguageProcessor GetProcessor(string locale)
        {
            if (string.IsNullOrEmpty(locale))
                locale = "en";

            lock (_lock)
            {
                if (_processors.TryGetValue(locale, out LanguageProcessor processor))
                    return processor;

                processor = CreateProcessor(locale);
                _processors[locale] = processor;
                return processor;
            }
        }

        public static LanguageProcessor GetCurrentProcessor()
        {
            if (_settings == null)
                throw new InvalidOperationException("Factory not initialized. Call Initialize() first.");

            return GetProcessor(_settings.Locale);
        }

        private static LanguageProcessor CreateProcessor(string locale)
        {
            if (_settings == null)
                throw new InvalidOperationException("Factory not initialized. Call Initialize() first.");

            locale = locale.ToLowerInvariant();
            switch (locale)
            {
                case "en":
                    return new EnglishLanguageProcessor(_settings);
                case "ko":
                    return new KoreanLanguageProcessor(_settings);
                case "ja":
                    return new JapaneseLanguageProcessor(_settings);
                case "zh-hans":
                    return new SimplifiedChineseLanguageProcessor(_settings);
                case "zh-hant":
                    return new TraditionalChineseLanguageProcessor(_settings);
                case "th":
                    return new ThaiLanguageProcessor(_settings);
                case "ru":
                    return new RussianLanguageProcessor(_settings);
                case "uk":
                    return new UkrainianLanguageProcessor(_settings);
                case "tr":
                    return new TurkishLanguageProcessor(_settings);
                case "pl":
                    return new PolishLanguageProcessor(_settings);
                case "fr":
                    return new FrenchLanguageProcessor(_settings);
                case "de":
                    return new GermanLanguageProcessor(_settings);
                case "es":
                    return new SpanishLanguageProcessor(_settings);
                case "pt":
                    return new PortugueseLanguageProcessor(_settings);
                case "it":
                    return new ItalianLanguageProcessor(_settings);
                default:
                    return new EnglishLanguageProcessor(_settings);
            }
        }

        public static void ClearCache()
        {
            lock (_lock)
            {
                _processors.Clear();
            }
        }
    }
}
