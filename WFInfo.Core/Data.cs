using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;

using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WFInfo.Services;
using WFInfo.Services.WarframeProcess;
using WFInfo.Services.WindowInfo;
using WFInfo.Settings;
using WFInfo.LanguageProcessing;

namespace WFInfo
{
    public class Data : IDisposable
    {
        public JObject marketItems;
        public JObject marketData;
        public JObject relicData;
        public JObject equipmentData;
        public JObject nameData;

        private readonly string applicationDirectory;
        private readonly string marketItemsPath;
        private readonly string marketDataPath;
        private readonly string equipmentDataPath;
        private readonly string relicDataPath;
        private readonly string nameDataPath;
        private readonly string filterAllJsonFallbackPath;
        private readonly string sheetJsonFallbackPath;
        public string JWT;
        private ClientWebSocket marketSocket = new ClientWebSocket();
        private CancellationTokenSource marketSocketCancellation = new CancellationTokenSource();

        private TaskCompletionSource<bool> _authenticationCompletionSource;
        private bool _isWebSocketAuthenticated = false;
        private Task _webSocketListenerTask;
        private const string filterAllJSON = "https://api.warframestat.us/wfinfo/filtered_items";
        private const string sheetJsonUrl = "https://api.warframestat.us/wfinfo/prices";
        private const string wfmItemsUrl = "https://api.warframe.market/v2/items";
        public string inGameName { get; private set; } = string.Empty;
        readonly HttpClient client;
        public bool rememberMe { get; set; }
        private ILogCapture _logCapture;
        private Task autoThread;

        // Reward tracking for AutoCSV/AutoCount/AutoList
        public List<List<string>> PrimeRewards { get; } = new();
        public short SelectedRewardIndex { get; set; } = 0;
        private readonly object _rewardsLock = new object();

        private static readonly object marketItemsLock = new object();
        private List<Tuple<string, string>> _nonEnglishSnapshotCache;
        private string _nonEnglishSnapshotLocale;

        private SemaphoreSlim _sendSemaphore = new SemaphoreSlim(1, 1);

        private readonly object _statusUpdateLock = new object();
        private DateTime _lastStatusUpdate = DateTime.MinValue;
        private string _lastStatusSent = "";
        private volatile bool _statusUpdateInProgress = false;

        private volatile bool _intentionalDisconnect = false;
        private volatile bool _reconnectionInProgress = false;
        private int _reconnectionAttempts = 0;
        // Exponential backoff delays (ms) for WebSocket reconnection
        private readonly int[] _reconnectionDelays = { 1000, 2000, 4000, 8000, 15000, 30000 };

        private readonly object _reconnectionLock = new object();

        private readonly IReadOnlyApplicationSettings _settings;
        private readonly IProcessFinder _process;
        private readonly IWindowInfoService _window;

        public event Action<string> OnMarketDataUpdated;
        public event Action<string> OnDropDataUpdated;
        public event Action<bool> OnReloadEnabled;
        public void FireReloadEnabled(bool enabled) => OnReloadEnabled?.Invoke(enabled);
        public event Action<List<List<string>>, short> OnSessionEnd;
        public event Action<string> OnWebSocketStatusChanged;

        public Data(IReadOnlyApplicationSettings settings, IProcessFinder process, IWindowInfoService window, ILogCapture logCapture = null)
        {
            _settings = settings;
            _process = process;
            _window = window;
            _logCapture = logCapture;

            LanguageProcessorFactory.Initialize(settings);

            AppMain.AddLog("Initializing Databases");
            applicationDirectory = PlatformPaths.AppDataPath;
            marketItemsPath = Path.Combine(applicationDirectory, "market_items.json");
            marketDataPath = Path.Combine(applicationDirectory, "market_data.json");
            equipmentDataPath = Path.Combine(applicationDirectory, "eqmt_data.json");
            relicDataPath = Path.Combine(applicationDirectory, "relic_data.json");
            nameDataPath = Path.Combine(applicationDirectory, "name_data.json");
            filterAllJsonFallbackPath = Path.Combine(applicationDirectory, "fallback_equipment_list.json");
            sheetJsonFallbackPath = Path.Combine(applicationDirectory, "fallback_price_sheet.json");

            Directory.CreateDirectory(applicationDirectory);

            WebProxy proxy = null;
            string proxy_string = Environment.GetEnvironmentVariable("https_proxy")
                ?? Environment.GetEnvironmentVariable("HTTPS_PROXY")
                ?? Environment.GetEnvironmentVariable("http_proxy")
                ?? Environment.GetEnvironmentVariable("HTTP_PROXY");
            if (proxy_string != null)
                proxy = new WebProxy(new Uri(proxy_string));

            HttpClientHandler handler = new HttpClientHandler { Proxy = proxy, UseCookies = false };
            client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WFInfo/" + AppMain.BuildVersion);

            OCR.OnRewardsProcessed += OnRewardsProcessed;
        }

        public void EnableLogCapture()
        {
            if (_logCapture != null)
            {
                _logCapture.TextChanged -= LogChanged;  // Prevent double-subscribe
                _logCapture.TextChanged += LogChanged;
                AppMain.AddLog("Data: LogCapture subscribed (auto-detection active)");
            }
            else
            {
                AppMain.AddLog("Data: LogCapture is null, auto-detection unavailable");
            }
        }

        public void DisableLogCapture()
        {
            if (_logCapture != null)
            {
                _logCapture.TextChanged -= LogChanged;
            }
        }

        private void OnRewardsProcessed(List<string> rewards)
        {
            if (rewards == null || rewards.Count == 0) return;
            lock (_rewardsLock)
            {
                if (PrimeRewards.Count > 0)
                    PrimeRewards[PrimeRewards.Count - 1] = new List<string>(rewards);
                else
                    PrimeRewards.Add(new List<string>(rewards));
                AppMain.AddLog($"Rewards tracked: {string.Join(" | ", rewards)} (total screens: {PrimeRewards.Count})");
            }
        }

        private static void SaveDatabase(string path, object db)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(db, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
        }

        public bool IsJwtLoggedIn()
        {
            // WFM JWT tokens are >300 chars
            return JWT != null && JWT.Length > 300;
        }

        public async Task<bool> ReloadItems()
        {
            var enItems = await GetWfmItemList("en");
            var localizedItems = _settings.Locale == "en" ? enItems : await GetWfmItemList(_settings.Locale);

            JObject tempMarketItems = new JObject();
            JArray items = JArray.FromObject(enItems.Data["data"]);
            int primeCount = 0;

            foreach (var item in items)
            {
                string name = item["i18n"]["en"]["name"].ToString();
                if (name.Contains(" Prime") && !name.Contains(" Set"))
                {
                    if (name.Contains("Neuroptics") || name.Contains("Chassis") ||
                        name.Contains("Systems") || name.Contains("Harness") || name.Contains("Wings"))
                        name = name.Replace(" Blueprint", "");

                    tempMarketItems[item["id"].ToString()] = name + "|" + item["slug"];
                    primeCount++;
                }
            }

            items = JArray.FromObject(localizedItems.Data["data"]);
            foreach (var item in items)
            {
                string itemId = item["id"].ToString();
                if (tempMarketItems.ContainsKey(itemId))
                {
                    string localizedName = null;
                    if (item["i18n"]?[_settings.Locale]?["name"] != null)
                        localizedName = item["i18n"][_settings.Locale]["name"].ToString();

                    tempMarketItems[itemId] = tempMarketItems[itemId] + "|" + (localizedName ?? string.Empty);
                }
            }

            foreach (var key in tempMarketItems.Properties().Select(p => p.Name).ToList())
            {
                string val = tempMarketItems[key].ToString();
                if (val.Split('|').Length < 3)
                    tempMarketItems[key] = val + "|";
            }

            tempMarketItems["locale"] = _settings.Locale;

            lock (marketItemsLock) { marketItems = tempMarketItems; _nonEnglishSnapshotCache = null; }
            SaveDatabase(marketItemsPath, marketItems);

            AppMain.AddLog("Item database has been downloaded");
            return enItems.IsFallback || localizedItems.IsFallback;
        }

        private JObject LoadMarket(JObject allFiltered, JArray sheetData)
        {
            var newMarketData = new JObject();
            foreach (var item in sheetData)
            {
                var key = item["name"].ToString();
                var transformedItem = new JObject
                {
                    ["name"] = item["name"],
                    ["plat"] = item["custom_avg"],
                    ["volume"] = item["today_vol"],
                    ["ducats"] = 0
                };
                newMarketData[key] = transformedItem;

                var alias = key.Replace(" Blueprint", "");
                if (!string.Equals(alias, key, StringComparison.Ordinal) && !newMarketData.TryGetValue(alias, out _))
                    newMarketData[alias] = transformedItem;
            }

            foreach (KeyValuePair<string, JToken> ignored in (JObject)allFiltered["ignored_items"])
                newMarketData[ignored.Key] = ignored.Value;

            AppMain.AddLog("Plat database has been downloaded");
            return newMarketData;
        }

        private bool IsItemUntradeable(JObject allFiltered, string itemName)
        {
            if (allFiltered == null || !allFiltered.ContainsKey("eqmt")) return false;
            foreach (KeyValuePair<string, JToken> prime in (JObject)allFiltered["eqmt"])
            {
                JObject primeObj = prime.Value as JObject;
                if (primeObj != null && primeObj.ContainsKey("parts"))
                {
                    foreach (KeyValuePair<string, JToken> part in (JObject)primeObj["parts"])
                    {
                        if (string.Equals(part.Key, itemName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(part.Key, itemName + " Blueprint", StringComparison.OrdinalIgnoreCase))
                        {
                            JObject partObj = part.Value as JObject;
                            if (partObj != null && partObj.ContainsKey("untradeable") && partObj["untradeable"].ToObject<bool>())
                                return true;
                        }
                    }
                }
            }
            return false;
        }

        private async Task<JObject> LoadMarketItem(string url)
        {
            JObject stats = new JObject { { "avg_price", 999 }, { "volume", 0 } };
            try
            {
                await Task.Delay(333); // rate-limit WFM API (~3 req/sec)
                string statsResponse = await client.GetStringAsync("https://api.warframe.market/v1/items/" + url + "/statistics");
                JObject allStats = JsonConvert.DeserializeObject<JObject>(statsResponse);
                JToken latestStats = allStats["payload"]["statistics_closed"]["90days"].LastOrDefault();
                if (latestStats != null) stats = latestStats.ToObject<JObject>();
            }
            catch (Exception ex) { AppMain.AddLog("Failed to fetch stats: " + ex.ToString()); }

            string ducat = "0";
            try
            {
                await Task.Delay(333); // rate-limit WFM API (~3 req/sec)
                string itemResponse = await client.GetStringAsync("https://api.warframe.market/v2/item/" + url);
                JObject responseJObject = JsonConvert.DeserializeObject<JObject>(itemResponse);
                if (responseJObject["data"].ToObject<JObject>().TryGetValue("ducats", out JToken temp))
                    ducat = temp.ToObject<string>();
            }
            catch (Exception ex) { AppMain.AddLog("Failed to fetch ducats: " + ex.ToString()); }

            return new JObject { { "ducats", ducat }, { "plat", stats["avg_price"] }, { "volume", stats["volume"] } };
        }

        private (JObject RelicData, JObject NameData) LoadEqmtData(JObject allFiltered, JObject mrktData, JObject eqmtData)
        {
            var newRelicData = new JObject();
            var newNameData = new JObject();

            foreach (KeyValuePair<string, JToken> era in (JObject)allFiltered["relics"])
            {
                newRelicData[era.Key] = new JObject();
                foreach (KeyValuePair<string, JToken> relic in (JObject)era.Value)
                    newRelicData[era.Key][relic.Key] = relic.Value;
            }

            foreach (KeyValuePair<string, JToken> prime in (JObject)allFiltered["eqmt"])
            {
                string primeName = prime.Key.Substring(0, prime.Key.IndexOf("Prime") + 5);
                if (!eqmtData.TryGetValue(primeName, out _))
                    eqmtData[primeName] = new JObject();
                JObject primeEqmt = (JObject)eqmtData[primeName];
                primeEqmt["vaulted"] = prime.Value["vaulted"];
                primeEqmt["type"] = prime.Value["type"];
                if (primeEqmt["mastered"] == null) primeEqmt["mastered"] = false;
                if (primeEqmt["parts"] == null) primeEqmt["parts"] = new JObject();
                JObject primeParts = (JObject)primeEqmt["parts"];

                foreach (KeyValuePair<string, JToken> part in (JObject)prime.Value["parts"])
                {
                    string partName = part.Key;
                    if (primeParts[partName] == null) primeParts[partName] = new JObject();
                    JObject partObj = (JObject)primeParts[partName];
                    if (partObj["owned"] == null) partObj["owned"] = 0;
                    partObj["vaulted"] = part.Value["vaulted"];
                    partObj["count"] = part.Value["count"];
                    partObj["ducats"] = part.Value["ducats"];

                    if (part.Key != null && prime.Value?["type"] != null && part.Value?["ducats"] != null)
                    {
                        string gameName = part.Key;
                        string partType = prime.Value["type"].ToString();
                        if (partType == "Archwing" && (part.Key.Contains("Systems") || part.Key.Contains("Harness") || part.Key.Contains("Wings")))
                            gameName += " Blueprint";
                        else if (partType == "Warframes" && (part.Key.Contains("Systems") || part.Key.Contains("Neuroptics") || part.Key.Contains("Chassis")))
                            gameName += " Blueprint";

                        string targetKey = null;
                        if (mrktData.TryGetValue(partName, out _)) targetKey = partName;
                        else if (mrktData.TryGetValue(partName + " Blueprint", out _)) targetKey = partName + " Blueprint";

                        if (targetKey != null)
                        {
                            newNameData[gameName] = partName;
                            mrktData[targetKey]["ducats"] = Convert.ToInt32(part.Value["ducats"].ToString(), AppMain.culture);
                        }
                    }
                }
            }

            foreach (KeyValuePair<string, JToken> ignored in (JObject)allFiltered["ignored_items"])
                newNameData[ignored.Key] = ignored.Key;

            AppMain.AddLog("Prime Database has been downloaded");
            return (newRelicData, newNameData);
        }

        private async Task<(JObject Data, bool IsFallback)> GetWfmItemList(string locale)
        {
            string localeSpecificFallbackPath = Path.Combine(applicationDirectory, $"fallback_names.{locale}.json");
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, wfmItemsUrl);
                request.Headers.Add("language", locale);
                request.Headers.Add("accept", "application/json");
                request.Headers.Add("platform", "pc");
                await Task.Delay(333);
                var response = await client.SendAsync(request).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var data = JsonConvert.DeserializeObject<JObject>(body);
                if (data != null && data["data"] is JArray)
                {
                    File.WriteAllText(localeSpecificFallbackPath, body);
                    return (data, false);
                }
                throw new InvalidDataException("Invalid JSON payload");
            }
            catch (Exception ex)
            {
                AppMain.AddLog("Failed to fetch " + wfmItemsUrl + ", using fallback. " + ex.Message);
                if (File.Exists(localeSpecificFallbackPath))
                {
                    var data = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(localeSpecificFallbackPath));
                    if (data?["data"] is JArray) return (data, true);
                }
                throw;
            }
        }

        private async Task<(JObject Data, bool IsFallback)> GetAllFiltered()
        {
            try
            {
                string response = await client.GetStringAsync(filterAllJSON);
                JObject data = JsonConvert.DeserializeObject<JObject>(response);
                File.WriteAllText(filterAllJsonFallbackPath, response);
                return (data, false);
            }
            catch (Exception ex)
            {
                AppMain.AddLog("Failed to fetch " + filterAllJSON + ", using fallback. " + ex.Message);
                if (File.Exists(filterAllJsonFallbackPath))
                    return (JsonConvert.DeserializeObject<JObject>(File.ReadAllText(filterAllJsonFallbackPath)), true);
                throw;
            }
        }

        private async Task<(JArray Data, bool IsFallback)> GetSheetData()
        {
            try
            {
                string response = await client.GetStringAsync(sheetJsonUrl);
                JArray data = JsonConvert.DeserializeObject<JArray>(response);
                File.WriteAllText(sheetJsonFallbackPath, response);
                return (data, false);
            }
            catch (Exception ex)
            {
                AppMain.AddLog("Failed to fetch " + sheetJsonUrl + ", using fallback. " + ex.Message);
                if (File.Exists(sheetJsonFallbackPath))
                    return (JsonConvert.DeserializeObject<JArray>(File.ReadAllText(sheetJsonFallbackPath)), true);
                throw;
            }
        }

        private SemaphoreSlim _DataUpdateSema = new SemaphoreSlim(1);

        public async Task Update()
        {
            await _DataUpdateSema.WaitAsync();
            try { await UpdateInner(false); }
            finally { _DataUpdateSema.Release(); }
        }

        public async Task ForceDataUpdate()
        {
            var acquired = await _DataUpdateSema.WaitAsync(TimeSpan.Zero);
            if (!acquired) { AppMain.StatusUpdate("Data Update already in progress", 3); OnReloadEnabled?.Invoke(true); return; }
            try
            {
                await UpdateInner(true);
                OnReloadEnabled?.Invoke(true);
            }
            catch (Exception ex)
            {
                AppMain.AddLog("ForceDataUpdate FAILED: " + ex);
                AppMain.StatusUpdate("Data Update Failed", 0);
                OnReloadEnabled?.Invoke(true);
            }
            finally { _DataUpdateSema.Release(); }
        }

        private JObject ParseFileOrMakeNew(string path, ref bool parseHasFailed)
        {
            if (File.Exists(path))
            {
                try { return JsonConvert.DeserializeObject<JObject>(File.ReadAllText(path)); }
                catch (Exception ex) { AppMain.AddLog($"Failed to parse {path}: {ex.Message}"); AppMain.StatusUpdate($"Data file corrupted: {Path.GetFileName(path)}, re-downloading", 1); parseHasFailed = true; return new JObject(); }
            }
            AppMain.AddLog(path + " missing, loading blank");
            parseHasFailed = true;
            return new JObject();
        }

        public async Task UpdateInner(bool force)
        {
            AppMain.AddLog("Starting UpdateInner, force: " + force);
            DateTime now = DateTime.Now;
            bool parseHasFailed = false;

            if (marketData == null) { marketData = ParseFileOrMakeNew(marketDataPath, ref parseHasFailed); }
            lock (marketItemsLock)
            {
                if (marketItems == null) { marketItems = ParseFileOrMakeNew(marketItemsPath, ref parseHasFailed); _nonEnglishSnapshotCache = null; }
            }
            if (equipmentData == null) { equipmentData = ParseFileOrMakeNew(equipmentDataPath, ref parseHasFailed); }
            if (relicData == null) { relicData = ParseFileOrMakeNew(relicDataPath, ref parseHasFailed); }
            if (nameData == null) { nameData = ParseFileOrMakeNew(nameDataPath, ref parseHasFailed); }

            bool marketIsRecent = false;
            string oldMarketTimeText = "UNKNOWN";
            if (marketData.TryGetValue("version", out _) && marketData["version"].ToObject<string>() == AppMain.BuildVersion
                && marketData.TryGetValue("timestamp", out var timestamp) && timestamp.ToObject<DateTime>() > now.AddHours(-12))
            {
                marketIsRecent = true;
                oldMarketTimeText = timestamp.ToObject<DateTime>().ToString("MMM dd - HH:mm", AppMain.culture);
            }

            bool equipmentIsRecent = false;
            string oldEquipmentTimeText = "UNKNOWN";
            if (equipmentData.TryGetValue("timestamp", out var eqTs) && eqTs.ToObject<DateTime>() > now.AddHours(-12))
            {
                equipmentIsRecent = true;
                oldEquipmentTimeText = eqTs.ToObject<DateTime>().ToString("MMM dd - HH:mm", AppMain.culture);
            }

            if (!parseHasFailed && !force && marketIsRecent && equipmentIsRecent)
            {
                OnMarketDataUpdated?.Invoke(oldMarketTimeText);
                OnDropDataUpdated?.Invoke(oldEquipmentTimeText);
                return;
            }

            var allFilteredTask = GetAllFiltered();
            var sheetDataTask = GetSheetData();
            var marketItemsIsFallbackTask = ReloadItems();
            await Task.WhenAll(allFilteredTask, sheetDataTask, marketItemsIsFallbackTask);
            var allFiltered = allFilteredTask.Result;
            var sheetData = sheetDataTask.Result;
            var marketItemsIsFallback = marketItemsIsFallbackTask.Result;
            var newMarketData = LoadMarket(allFiltered.Data, sheetData.Data);

            var missing = new List<(string Name, string Url)>();
            lock (marketItemsLock)
            {
                foreach (KeyValuePair<string, JToken> elem in marketItems)
                {
                    if (elem.Key == "version") continue;
                    string[] split = elem.Value.ToString().Split('|');
                    if (split.Length < 2) continue;
                    string itemName = split[0];
                    string itemUrl = split[1];
                    if (!itemName.Contains(" Set") && !newMarketData.ContainsKey(itemName) && !newMarketData.ContainsKey(itemName + " Blueprint"))
                        missing.Add((itemName, itemUrl));
                }
            }
            var tradeable = missing.Where(m => !IsItemUntradeable(allFiltered.Data, m.Name)).ToList();
            if (tradeable.Count > 0)
            {
                var semaphore = new SemaphoreSlim(3);
                var tasks = tradeable.Select(async m =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        AppMain.AddLog("Load missing market item: " + m.Name);
                        var data = await LoadMarketItem(m.Url);
                        return (m.Name, data);
                    }
                    finally { semaphore.Release(); }
                }).ToList();
                var results = await Task.WhenAll(tasks);
                foreach (var (name, data) in results)
                    newMarketData[name] = data;
            }

            var newEquipmentData = (JObject)equipmentData.DeepClone();
            var (newRelicData, newNameData) = LoadEqmtData(allFiltered.Data, newMarketData, newEquipmentData);

            string marketTimeText;
            string equipmentTimeText;
            if (!allFiltered.IsFallback && !sheetData.IsFallback && !marketItemsIsFallback)
            {
                newMarketData["timestamp"] = now;
                marketTimeText = now.ToString("MMM dd - HH:mm", AppMain.culture);
            }
            else { marketTimeText = "FALLBACK"; }

            if (!allFiltered.IsFallback)
            {
                newEquipmentData["timestamp"] = now;
                equipmentTimeText = now.ToString("MMM dd - HH:mm", AppMain.culture);
            }
            else { equipmentTimeText = "FALLBACK"; }

            newMarketData["version"] = AppMain.BuildVersion;

            marketData = newMarketData;
            equipmentData = newEquipmentData;
            relicData = newRelicData;
            nameData = newNameData;
            SaveAllJSONs();

            OnMarketDataUpdated?.Invoke(marketTimeText);
            OnDropDataUpdated?.Invoke(equipmentTimeText);

            AppMain.AddLog("Data Update Complete");
            AppMain.StatusUpdate("Data Update Complete", 0);
        }

        public void SaveAllJSONs()
        {
            SaveDatabase(equipmentDataPath, equipmentData);
            SaveDatabase(relicDataPath, relicData);
            SaveDatabase(nameDataPath, nameData);
            SaveDatabase(marketItemsPath, marketItems);
            SaveDatabase(marketDataPath, marketData);
        }

        public bool IsPartVaulted(string name)
        {
            if (name.IndexOf("Prime") < 0) return false;
            string eqmt = name.Substring(0, name.IndexOf("Prime") + 5);
            return equipmentData[eqmt]?["parts"]?[name]?["vaulted"]?.ToObject<bool>() ?? false;
        }

        public bool IsPartMastered(string name)
        {
            if (name.IndexOf("Prime") < 0) return false;
            string eqmt = name.Substring(0, name.IndexOf("Prime") + 5);
            return equipmentData[eqmt]?["mastered"]?.ToObject<bool>() ?? false;
        }

        public string PartsOwned(string name)
        {
            if (name.IndexOf("Prime") < 0) return "0";
            string eqmt = name.Substring(0, name.IndexOf("Prime") + 5);
            return equipmentData[eqmt]?["parts"]?[name]?["owned"]?.ToString() ?? "0";
        }

        public string PartsCount(string name)
        {
            if (name.IndexOf("Prime") < 0) return "0";
            string eqmt = name.Substring(0, name.IndexOf("Prime") + 5);
            return equipmentData[eqmt]?["parts"]?[name]?["count"]?.ToString() ?? "0";
        }

        public int LevenshteinDistance(string s, string t)
        {
            var processor = LanguageProcessorFactory.GetCurrentProcessor();
            return processor.CalculateLevenshteinDistance(s, t);
        }

        public string GetPartNameHuman(string name, out int low)
        {
            string lowest = null;
            string lowest_unfiltered = null;
            low = 9999;

            string resolvedName;
            if (_settings.Locale == "en")
                resolvedName = name;
            else
                resolvedName = GetLocaleNameData(name, false) ?? name;

            foreach (var prop in nameData)
            {
                if (prop.Value.ToString().ToLower(AppMain.culture).Contains(resolvedName.ToLower(AppMain.culture)))
                {
                    int val = LevenshteinDistance(prop.Value.ToString(), resolvedName);
                    if (val < low)
                    {
                        low = val;
                        lowest = prop.Value.ToObject<string>();
                        lowest_unfiltered = prop.Value.ToString();
                    }
                }
            }
            if (low > 10)
            {
                foreach (var prop in nameData)
                {
                    int val = LevenshteinDistance(prop.Value.ToString(), resolvedName);
                    if (val < low)
                    {
                        low = val;
                        lowest = prop.Value.ToObject<string>();
                        lowest_unfiltered = prop.Value.ToString();
                    }
                }
            }
            AppMain.AddLog($"Search found part({low}): \"{lowest_unfiltered}\" from \"{name}\"");
            return lowest;
        }

        public string GetUrlName(string primeName)
        {
            lock (marketItemsLock)
            {
                if (marketItems != null)
                {
                    foreach (var marketItem in marketItems)
                    {
                        string[] vals = marketItem.Value.ToString().Split('|');
                        if (vals.Length > 2 && vals[0].Equals(primeName, StringComparison.OrdinalIgnoreCase))
                        {
                            return vals[1];
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Resolves OCR-specific ambiguities between similar-looking operator names
        /// </summary>
        private bool ResolveOcrAmbiguity(string currentBest, string candidate, string ocrText)
        {
            if (currentBest.StartsWith("Gara") && candidate.StartsWith("Ivara"))
                return true;

            if (currentBest.StartsWith("Gara") && candidate.StartsWith("Mesa") &&
                !string.IsNullOrEmpty(ocrText) && ocrText.StartsWith("M", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        public string GetPartName(string name, out int low, bool suppressLogging, out bool multipleLowest)
        {
            string lowest = null;
            string lowest_unfiltered = null;
            low = 9999;
            multipleLowest = false;

            if (_settings.Locale != "en")
            {
                var processor = LanguageProcessorFactory.GetCurrentProcessor();
                string normalizedName = processor.NormalizeForPatternMatching(name);

                List<Tuple<string, string>> baseSnapshot;
                lock (marketItemsLock)
                {
                    if (_nonEnglishSnapshotCache != null && _nonEnglishSnapshotLocale == _settings.Locale)
                    {
                        baseSnapshot = _nonEnglishSnapshotCache;
                    }
                    else if (marketItems != null)
                    {
                        string cachedLocale = marketItems.TryGetValue("locale", out var localeToken) ? localeToken?.ToString() : null;
                        bool useLocalizedNames = cachedLocale == _settings.Locale;

                        baseSnapshot = new List<Tuple<string, string>>();
                        foreach (var marketItem in marketItems)
                        {
                            if (marketItem.Key == "version") continue;
                            string[] split = marketItem.Value.ToString().Split('|');
                            if (split.Length < 2) continue;
                            string comparisonName = useLocalizedNames && split.Length >= 3 ? split[2] : split[0];
                            baseSnapshot.Add(Tuple.Create(split[0], comparisonName));
                        }
                        _nonEnglishSnapshotCache = baseSnapshot;
                        _nonEnglishSnapshotLocale = _settings.Locale;
                    }
                    else
                    {
                        baseSnapshot = new List<Tuple<string, string>>();
                    }
                }

                var marketItemsSnapshot = new List<Tuple<string, string>>(baseSnapshot.Count);
                foreach (var item in baseSnapshot)
                {
                    int englishNameLength = item.Item1.Length;
                    string normalizedStoredName = processor.NormalizeForPatternMatching(item.Item2);
                    int lengthDiff = Math.Abs(normalizedStoredName.Length - normalizedName.Length);
                    if (lengthDiff > Math.Max(englishNameLength, normalizedName.Length) / 2) continue;
                    marketItemsSnapshot.Add(item);
                }

                var ignoredItems = processor.IgnoredItemNames;
                if (ignoredItems != null)
                {
                    foreach (var kvp in ignoredItems)
                    {
                        string normalizedIgnoredName = processor.NormalizeForPatternMatching(kvp.Value);
                        marketItemsSnapshot.Add(Tuple.Create(kvp.Key, normalizedIgnoredName));
                    }
                }

                foreach (var item in marketItemsSnapshot)
                {
                    string englishName = item.Item1;
                    string storedName = item.Item2;
                    string normalizedStoredName = processor.NormalizeForPatternMatching(storedName);

                    int val;
                    if (processor.Locale == "ko")
                        val = processor.CalculateLevenshteinDistance(name, storedName);
                    else
                        val = processor.SimpleLevenshteinDistance(normalizedName, normalizedStoredName);

                    if (val >= normalizedStoredName.Length * processor.DistanceThresholdRatio) continue;

                    if (val < low)
                    {
                        low = val;
                        lowest = englishName;
                        lowest_unfiltered = storedName;
                        multipleLowest = false;
                    }
                    else if (val == low) { multipleLowest = true; }
                }
            }
            else
            {
                foreach (KeyValuePair<string, JToken> prop in nameData)
                {
                    int lengthDiff = Math.Abs(prop.Key.Length - name.Length);
                    if (lengthDiff > Math.Max(prop.Key.Length, name.Length) / 2) continue;
                    int val = LevenshteinDistance(prop.Key, name);
                    if (val >= prop.Key.Length * 0.5) continue;

                    if (val < low)
                    {
                        low = val;
                        lowest = prop.Value.ToObject<string>();
                        lowest_unfiltered = prop.Key;
                        multipleLowest = false;
                    }
                    else if (val == low)
                    {
                        if (prop.Key.Length > (lowest_unfiltered?.Length ?? 0))
                        {
                            lowest = prop.Value.ToObject<string>();
                            lowest_unfiltered = prop.Key;
                        }
                        multipleLowest = true;
                    }

                    if (val == low && ResolveOcrAmbiguity(lowest, prop.Key, name))
                    {
                        lowest = prop.Value.ToObject<string>();
                        lowest_unfiltered = prop.Key;
                    }
                }
            }

            if (!suppressLogging)
                AppMain.AddLog("Found part(" + low + "): \"" + lowest_unfiltered + "\" from \"" + name + "\"");

            return lowest;
        }

        public string GetLocalizedNameForClipboard(string englishName)
        {
            if (_settings.Locale == "en" || string.IsNullOrEmpty(englishName))
                return englishName;

            lock (marketItemsLock)
            {
                if (marketItems == null) return englishName;
                string cachedLocale = marketItems.TryGetValue("locale", out var lt) ? lt?.ToString() : null;
                if (cachedLocale != _settings.Locale) return englishName;

                foreach (var marketItem in marketItems)
                {
                    if (marketItem.Key == "locale" || marketItem.Key == "version") continue;
                    string[] split = marketItem.Value.ToString().Split('|');
                    if (split.Length < 3) continue;
                    if (split[0].Equals(englishName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(split[2])) return split[2];
                        break;
                    }
                }
            }
            return englishName;
        }

        public string RemoveBlueprintTerms(string localizedName)
        {
            if (string.IsNullOrEmpty(localizedName)) return localizedName;
            var processor = LanguageProcessorFactory.GetCurrentProcessor();
            return processor.RemoveBlueprintTerms(localizedName);
        }

        public bool IsIgnoredItem(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return false;
            var processor = LanguageProcessorFactory.GetCurrentProcessor();
            return processor.IsIgnoredItem(partName);
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Culture, string Name), string> _setNameCache =
            new System.Collections.Concurrent.ConcurrentDictionary<(string, string), string>();

        public static string GetSetName(string name)
        {
            var culture = LanguageProcessorFactory.GetCurrentProcessor().Culture;
            return _setNameCache.GetOrAdd((culture.Name, name), key => ComputeSetName(key.Name));
        }

        private static string ComputeSetName(string name)
        {
            var processor = LanguageProcessorFactory.GetCurrentProcessor();
            string result = name.ToLower(processor.Culture);
            if (result.Contains("kavasa")) return "Kavasa Prime Kubrow Collar Set";

            foreach (var term in new[] { "lower limb", "upper limb", "neuroptics", "chassis", "systems", "carapace", "cerebrum",
                "blueprint", "harness", "blade", "pouch", "head", "barrel", "receiver", "stock", "disc", "grip", "string",
                "handle", "ornament", "wings", "blades", "hilt", "link" })
                result = result.Replace(term, "");

            result = result.TrimEnd();
            result = processor.Culture.TextInfo.ToTitleCase(result);
            result += " Set";
            return result;
        }

        public string GetLocaleNameData(string s, bool useLevenshtein = true)
        {
            var processor = LanguageProcessorFactory.GetCurrentProcessor();
            List<KeyValuePair<string, string>> snapshot;
            lock (marketItemsLock)
            {
                if (marketItems == null) return s;

                string cachedLocale = marketItems.TryGetValue("locale", out var localeToken) ? localeToken?.ToString() : null;
                if (cachedLocale != processor.Culture.Name)
                {
                    AppMain.AddLog($"Warning: marketItems locale ({cachedLocale ?? "null"}) doesn't match processor locale ({processor.Culture.Name}), triggering background refresh");
                    Task.Run(async () =>
                    {
                        await _DataUpdateSema.WaitAsync();
                        try
                        {
                            await ReloadItems();
                            AppMain.AddLog($"Background ReloadItems completed for locale {processor.Culture.Name}");
                        }
                        catch (Exception ex)
                        {
                            AppMain.AddLog($"Background ReloadItems failed: {ex.Message}");
                        }
                        finally { _DataUpdateSema.Release(); }
                    });
                }

                snapshot = new List<KeyValuePair<string, string>>(marketItems.Count);
                foreach (var kvp in marketItems)
                    snapshot.Add(new KeyValuePair<string, string>(kvp.Key, kvp.Value.ToString()));
            }
            return processor.GetLocalizedNameData(s, snapshot, useLevenshtein);
        }

        private readonly Stopwatch _autoE2eWatch = new Stopwatch();

        private void LogChanged(object sender, string line)
        {
            if (autoThread != null && !autoThread.IsCompleted) return;
            autoThread?.Dispose();
            autoThread = null;

            if (line.Contains("Pause countdown done") || line.Contains("Got rewards"))
            {
                _autoE2eWatch.Restart();
                AppMain.AddLog($"Auto: trigger detected, \"{(line.Length > 80 ? line.Substring(0, 80) + "..." : line)}\"");
                autoThread = Task.Run(async () =>
                {
                    await AutoTriggered();
                    AppMain.AddLog($"Auto: end-to-end from trigger detection: {_autoE2eWatch.ElapsedMilliseconds}ms");
                });
            }

            // Session-end: trigger AutoCSV/AutoCount/AutoList
            if ((line.Contains("MatchingService::EndSession") || line.Contains("Relic timer closed"))
                && (_settings.AutoList || _settings.AutoCSV || _settings.AutoCount))
            {
                List<List<string>> rewards;
                short selectedIdx;
                lock (_rewardsLock)
                {
                    if (PrimeRewards.Count == 0) return;
                    // Inner lists are already copies
                    rewards = new List<List<string>>(PrimeRewards);
                    selectedIdx = SelectedRewardIndex;
                    PrimeRewards.Clear();
                    SelectedRewardIndex = 0;
                }
                AppMain.AddLog($"Session end detected, dispatching {rewards.Count} reward screen(s) to auto features");
                OnSessionEnd?.Invoke(rewards, selectedIdx);
            }
        }

        public async Task AutoTriggered()
        {
            try
            {
                long triggerTicks = DateTime.UtcNow.Ticks;
                var watch = Stopwatch.StartNew();

                _window.UpdateWindow();
                AppMain.AddLog($"Auto: UpdateWindow took {watch.ElapsedMilliseconds}ms");

                if (_settings.ThemeSelection == WFtheme.AUTO)
                {
                    long stop = watch.ElapsedMilliseconds + 5000;
                    int checks = 0;
                    long wait = watch.ElapsedMilliseconds;

                    while (watch.ElapsedMilliseconds < stop)
                    {
                        if (watch.ElapsedMilliseconds <= wait)
                        {
                            await Task.Delay(10).ConfigureAwait(false);
                            continue;
                        }
                        wait += _settings.AutoDelay;
                        checks++;

                        // If manual scan already processed since this trigger, bail out
                        if (OCR.LastRewardProcessedTicks > triggerTicks)
                        {
                            AppMain.AddLog("Auto: skipping, rewards already processed by manual scan");
                            return;
                        }

                        OCR.GetThemeWeighted(out double diff);
                        if (!(diff > 40)) continue;

                        long remaining = wait - watch.ElapsedMilliseconds;
                        if (remaining > 0)
                            await Task.Delay((int)remaining).ConfigureAwait(false);

                        AppMain.AddLog($"Auto: theme detected after {checks} checks, {watch.ElapsedMilliseconds}ms, processing");
                        OCR.ProcessRewardScreen();
                        AppMain.AddLog($"Auto: total {watch.ElapsedMilliseconds}ms");
                        break;
                    }
                }
                else
                {
                    long fixedStop = _settings.FixedAutoDelay;
                    long remaining = fixedStop - watch.ElapsedMilliseconds;
                    if (remaining > 0) await Task.Delay((int)remaining).ConfigureAwait(false);

                    if (OCR.LastRewardProcessedTicks > triggerTicks)
                    {
                        AppMain.AddLog("Auto: skipping, rewards already processed by manual scan");
                        return;
                    }

                    AppMain.AddLog($"Auto: fixed delay {watch.ElapsedMilliseconds}ms, processing");
                    OCR.ProcessRewardScreen();
                    AppMain.AddLog($"Auto: total {watch.ElapsedMilliseconds}ms");
                }
                watch.Stop();
            }
            catch (Exception ex)
            {
                AppMain.AddLog("AUTO FAILED: " + ex);
                AppMain.StatusUpdate("Auto Detection Failed", 0);
                AppMain.SpawnErrorPopup(DateTime.UtcNow);
            }
        }

        public async Task GetUserLogin(string email, string password)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.warframe.market/v1/auth/signin");
            var content = JsonConvert.SerializeObject(new { email, password, device_id = "wfinfo", auth_type = "header" });
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");
            request.Headers.Add("Authorization", "JWT");
            request.Headers.Add("language", "en");
            request.Headers.Add("accept", "application/json");
            request.Headers.Add("platform", "pc");
            var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            Regex rgxBody = new Regex("\"check_code\": \".*?\"");
            string censoredResponse = rgxBody.Replace(responseBody, "\"check_code\": \"REDACTED\"");
            AppMain.AddLog(censoredResponse);
            if (response.IsSuccessStatusCode)
            {
                SetJWT(response.Headers);
                await OpenWebSocket();
            }
            else
            {
                Regex rgxEmail = new Regex("[a-zA-Z0-9]");
                string censoredEmail = rgxEmail.Replace(email, "*");
                throw new Exception("GetUserLogin, " + censoredResponse + $"Email: {censoredEmail}, Pw length: {password.Length}");
            }
        }

        private void SetJWT(HttpResponseHeaders headers)
        {
            if (headers.TryGetValues("authorization", out var vals))
            {
                JWT = vals.FirstOrDefault()?.Split(' ').Last();
                if (rememberMe && JWT != null)
                    EncryptedDataService.PersistJWT(JWT);
            }
        }

        public async Task<bool> OpenWebSocket()
        {
            _intentionalDisconnect = false;
            if (marketSocket?.State == WebSocketState.Open && _isWebSocketAuthenticated) return true;

            if (marketSocket != null)
            {
                try { if (marketSocket.State == WebSocketState.Open || marketSocket.State == WebSocketState.Connecting) await marketSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch { }
                try { marketSocket.Dispose(); } catch { }
            }

            marketSocket = new ClientWebSocket();
            _isWebSocketAuthenticated = false;
            _authenticationCompletionSource = new TaskCompletionSource<bool>();

            try
            {
                marketSocket.Options.AddSubProtocol("wfm");
                marketSocket.Options.SetRequestHeader("Authorization", "Bearer " + JWT);
                try { marketSocket.Options.SetRequestHeader("User-Agent", "WFInfo/" + AppMain.BuildVersion); } catch { }

                using var connectCts = new CancellationTokenSource(15000);
                await marketSocket.ConnectAsync(new Uri("wss://warframe.market/socket-v2"), connectCts.Token);

                if (marketSocket.State == WebSocketState.Open)
                {
                    _webSocketListenerTask = Task.Run(StartWebSocketListener);
                    bool authSuccess = await AuthenticateWebSocket();
                    if (authSuccess)
                    {
                        _ = Task.Delay(500).ContinueWith(async _ =>
                        {
                            try
                            {
                                string status;
                                if (_settings?.ManualMarketStatus == true)
                                    status = _settings.MarketStatus ?? "ingame";
                                else
                                    status = _process.IsRunning ? "ingame" : "online";
                                await SetWebsocketStatus(status);
                            }
                            catch (Exception ex)
                            {
                                AppMain.AddLog($"Failed to set initial WebSocket status: {ex.Message}");
                            }
                        });
                    }
                    return authSuccess;
                }
            }
            catch (Exception ex) { AppMain.AddLog($"WebSocket error: {ex.Message}"); }
            return false;
        }

        private async Task<bool> AuthenticateWebSocket()
        {
            try
            {
                await SendMessage(JsonConvert.SerializeObject(new
                {
                    route = "@wfm|cmd/auth/signIn",
                    payload = new { token = JWT, deviceId = "wfinfo" }
                }));

                using var cts = new CancellationTokenSource(10000);
                cts.Token.Register(() => _authenticationCompletionSource?.TrySetResult(false));
                return await _authenticationCompletionSource.Task;
            }
            catch { return false; }
        }

        private async Task<bool> SendMessage(string msg)
        {
            if (marketSocket == null || marketSocket.State != WebSocketState.Open)
                return false;

            bool acquired = false;
            try
            {
                acquired = await _sendSemaphore.WaitAsync(TimeSpan.FromSeconds(10));
                if (!acquired)
                {
                    AppMain.AddLog("Failed to acquire send semaphore within timeout");
                    return false;
                }

                if (marketSocket == null || marketSocket.State != WebSocketState.Open)
                    return false;

                var bytes = Encoding.UTF8.GetBytes(msg);
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    await marketSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                AppMain.AddLog("WebSocket send operation timed out");
                return false;
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"WebSocket send error: {ex.Message}");
                return false;
            }
            finally
            {
                if (acquired)
                    _sendSemaphore.Release();
            }
        }

        private async Task StartWebSocketListener()
        {
            var buffer = new byte[8192];
            try
            {
                while (marketSocket.State == WebSocketState.Open && !marketSocketCancellation.Token.IsCancellationRequested)
                {
                    var result = await marketSocket.ReceiveAsync(new ArraySegment<byte>(buffer), marketSocketCancellation.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string message;
                        if (result.EndOfMessage)
                        {
                            message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        }
                        else
                        {
                            using (var ms = new MemoryStream())
                            {
                                ms.Write(buffer, 0, result.Count);
                                while (!result.EndOfMessage)
                                {
                                    result = await marketSocket.ReceiveAsync(new ArraySegment<byte>(buffer), marketSocketCancellation.Token).ConfigureAwait(false);
                                    ms.Write(buffer, 0, result.Count);
                                }
                                message = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                            }
                        }
                        await HandleWebSocketMessage(message).ConfigureAwait(false);
                    }
                    else if (result.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException wsEx) { AppMain.AddLog($"WebSocket connection error: {wsEx.Message}"); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { AppMain.AddLog($"WebSocket listener error: {ex.Message}"); }
            finally
            {
                if (!_intentionalDisconnect && IsJwtLoggedIn())
                    _ = Task.Run(StartReconnectionProcess);
            }
        }

        private async Task HandleWebSocketMessage(string message)
        {
            try
            {
                var msg = JsonConvert.DeserializeObject<JObject>(message);
                var route = msg["route"]?.ToString();
                var payload = msg["payload"] as JObject;

                if (route == "@wfm|cmd/auth/signIn" || route?.Contains("auth") == true)
                {
                    var success = payload?["success"]?.ToObject<bool>() ??
                                  (payload?["error"] == null);

                    if (success)
                    {
                        AppMain.AddLog("WebSocket authentication successful");
                        _isWebSocketAuthenticated = true;
                        _authenticationCompletionSource?.TrySetResult(true);
                    }
                    else
                    {
                        var error = payload?["error"]?.ToString() ?? "Unknown authentication error";
                        AppMain.AddLog($"WebSocket authentication failed: {error}");
                        _authenticationCompletionSource?.TrySetResult(false);
                    }
                }

                if (_isWebSocketAuthenticated)
                {
                    var statusPayload = payload?["status"]?.ToString();
                    if (!string.IsNullOrEmpty(statusPayload))
                        OnWebSocketStatusChanged?.Invoke(statusPayload);
                }
            }
            catch (Exception e) { AppMain.AddLog($"WebSocket message error: {e.Message}"); }
        }

        private async Task StartReconnectionProcess()
        {
            lock (_reconnectionLock)
            {
                if (_reconnectionInProgress || _intentionalDisconnect) return;
                _reconnectionInProgress = true;
                _reconnectionAttempts = 0;
            }
            try
            {
                while (_reconnectionAttempts < _reconnectionDelays.Length && !_intentionalDisconnect)
                {
                    _reconnectionAttempts++;
                    await Task.Delay(_reconnectionDelays[_reconnectionAttempts - 1]);
                    if (_intentionalDisconnect || !IsJwtLoggedIn()) break;
                    try
                    {
                        _isWebSocketAuthenticated = false;
                        bool reconnected = await OpenWebSocket();
                        if (reconnected) { AppMain.AddLog("WebSocket reconnected"); break; }
                    }
                    catch (Exception ex) { AppMain.AddLog($"Reconnect attempt {_reconnectionAttempts} error: {ex.Message}"); }
                }
            }
            finally { lock (_reconnectionLock) { _reconnectionInProgress = false; } }
        }

        public async Task SetWebsocketStatus(string status)
        {
            if (!_isWebSocketAuthenticated)
                return;

            lock (_statusUpdateLock)
            {
                if (_statusUpdateInProgress)
                    return;

                var now = DateTime.UtcNow;
                if (_lastStatusSent == status && (now - _lastStatusUpdate).TotalMilliseconds < 500)
                    return;

                _statusUpdateInProgress = true;
                _lastStatusUpdate = now;
                _lastStatusSent = status;
            }

            try
            {
                var payload = new { route = "@wfm|cmd/status/set", payload = new { status = status } };
                string message = JsonConvert.SerializeObject(payload);
                bool success = await SendMessage(message);
                if (!success)
                    AppMain.AddLog($"Failed to set websocket status to: {status}");
            }
            finally
            {
                lock (_statusUpdateLock)
                {
                    _statusUpdateInProgress = false;
                }
            }
        }

        public void Disconnect()
        {
            try
            {
                _intentionalDisconnect = true;

                // Set invisible before disconnect
                if (marketSocket != null && marketSocket.State == WebSocketState.Open && _isWebSocketAuthenticated && IsJwtLoggedIn())
                {
                    try
                    {
                        var task = SetWebsocketStatus("invisible");
                        task.Wait(2000);
                    }
                    catch (Exception ex)
                    {
                        AppMain.AddLog($"Could not send invisible status: {ex.Message}");
                    }
                }

                _isWebSocketAuthenticated = false;

                if (_authenticationCompletionSource != null && !_authenticationCompletionSource.Task.IsCompleted)
                    _authenticationCompletionSource.TrySetResult(false);
                _authenticationCompletionSource = null;

                marketSocketCancellation?.Cancel();



                if (_webSocketListenerTask != null && !_webSocketListenerTask.IsCompleted)
                {
                    try { _webSocketListenerTask.Wait(2000); }
                    catch { }
                }

                if (marketSocket != null)
                {
                    try { marketSocket.Dispose(); }
                    catch { }
                    finally
                    {
                        marketSocket = null;
                        _webSocketListenerTask = null;
                    }
                }

                JWT = null;
                rememberMe = false;
                inGameName = string.Empty;

                try
                {
                    marketSocketCancellation?.Dispose();
                    marketSocketCancellation = new CancellationTokenSource();
                }
                catch { }

                AppMain.AddLog("WebSocket disconnected successfully");
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"Error during disconnect: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Disconnect();
            marketSocketCancellation?.Dispose();
            _sendSemaphore?.Dispose();
            _DataUpdateSema?.Dispose();
            client?.Dispose();
        }

        #region Market Listing API (AutoList)

        public async Task<JObject> GetTopListings(string primeName)
        {
            var urlName = GetUrlName(primeName);
            if (urlName == null)
            {
                AppMain.AddLog($"GetTopListings: \"{primeName}\" not found in marketItems");
                return null;
            }
            try
            {
                using (var request = new HttpRequestMessage
                {
                    RequestUri = new Uri("https://api.warframe.market/v2/orders/item/" + urlName + "/top"),
                    Method = HttpMethod.Get
                })
                {
                    request.Headers.Add("Authorization", "Bearer " + JWT);
                    request.Headers.Add("language", "en");
                    request.Headers.Add("accept", "application/json");
                    request.Headers.Add("platform", "pc");
                    request.Headers.Add("auth_type", "header");
                    var response = await client.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    if (body.Length < 3)
                        throw new Exception("No sell orders found");
                    return JsonConvert.DeserializeObject<JObject>(body);
                }
            }
            catch (Exception e)
            {
                AppMain.AddLog("GetTopListings: " + e.Message);
                return null;
            }
        }

        public async Task<bool> IsJWTvalid()
        {
            if (JWT == null) return false;
            try
            {
                using (var request = new HttpRequestMessage
                {
                    RequestUri = new Uri("https://api.warframe.market/v2/me"),
                    Method = HttpMethod.Get
                })
                {
                    request.Headers.Add("Authorization", "Bearer " + JWT);
                    var response = await client.SendAsync(request);
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch (Exception e)
            {
                AppMain.AddLog($"IsJWTvalid: {e.Message}");
                return false;
            }
        }

        public async Task<JToken> GetCurrentListing(string primeName)
        {
            try
            {
                if (string.IsNullOrEmpty(inGameName))
                    await SetIngameName();

                using (var request = new HttpRequestMessage
                {
                    RequestUri = new Uri("https://api.warframe.market/v2/orders/my"),
                    Method = HttpMethod.Get
                })
                {
                    request.Headers.Add("Authorization", "Bearer " + JWT);
                    request.Headers.Add("language", "en");
                    request.Headers.Add("accept", "application/json");
                    request.Headers.Add("platform", "pc");
                    request.Headers.Add("auth_type", "header");
                    var response = await client.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    var payload = JsonConvert.DeserializeObject<JObject>(body);
                    var allOrders = (JArray)payload?["data"];
                    string itemID = PrimeItemToItemID(primeName);

                    if (allOrders != null)
                    {
                        foreach (var listing in allOrders)
                        {
                            if ((string)listing["type"] == "sell" && itemID == (string)listing?["itemId"])
                                return listing;
                        }
                        return null;
                    }
                    else
                    {
                        throw new Exception("No sell orders found: " + payload);
                    }
                }
            }
            catch (Exception e)
            {
                AppMain.AddLog("GetCurrentListing: " + e.Message);
                return null;
            }
        }

        public async Task<bool> ListItem(string primeItem, int platinum, int quantity)
        {
            try
            {
                using (var request = new HttpRequestMessage
                {
                    RequestUri = new Uri("https://api.warframe.market/v2/order"),
                    Method = HttpMethod.Post
                })
                {
                    var itemId = PrimeItemToItemID(primeItem);
                    var json = JsonConvert.SerializeObject(new
                    {
                        type = "sell",
                        itemId,
                        platinum,
                        quantity,
                        visible = true
                    });
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    request.Headers.Add("Authorization", "Bearer " + JWT);
                    request.Headers.Add("language", "en");
                    request.Headers.Add("accept", "application/json");
                    request.Headers.Add("platform", "pc");
                    request.Headers.Add("auth_type", "header");

                    var response = await client.SendAsync(request);
                    var responseBody = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) throw new Exception(responseBody);
                    return true;
                }
            }
            catch (Exception e)
            {
                AppMain.AddLog($"ListItem: {e.Message}");
                return false;
            }
        }

        public async Task<bool> UpdateListing(string listingId, int platinum, int quantity)
        {
            try
            {
                using (var request = new HttpRequestMessage
                {
                    RequestUri = new Uri("https://api.warframe.market/v2/order/" + listingId),
                    Method = HttpMethod.Put
                })
                {
                    var json = JsonConvert.SerializeObject(new
                    {
                        platinum,
                        quantity,
                        visible = true
                    });
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    request.Headers.Add("Authorization", "Bearer " + JWT);
                    request.Headers.Add("language", "en");
                    request.Headers.Add("accept", "application/json");
                    request.Headers.Add("platform", "pc");
                    request.Headers.Add("auth_type", "header");

                    var response = await client.SendAsync(request);
                    var responseBody = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) throw new Exception(responseBody);
                    return true;
                }
            }
            catch (Exception e)
            {
                AppMain.AddLog($"UpdateListing: {e.Message}");
                return false;
            }
        }

        public string PrimeItemToItemID(string primeItem)
        {
            lock (marketItemsLock)
            {
                if (marketItems != null)
                {
                    foreach (var marketItem in marketItems)
                    {
                        if (marketItem.Value.ToString().Split('|').First()
                            .Equals(primeItem, StringComparison.OrdinalIgnoreCase))
                            return marketItem.Key;
                    }
                }
            }
            throw new Exception($"PrimeItemToItemID: \"{primeItem}\" not found in marketItems");
        }

        public async Task<bool> PostReview(string message = "Thank you for WFinfo!")
        {
            var msg = JsonConvert.SerializeObject(new { text = message, review_type = "1" });
            var developers = new List<string> { "wutwutrad", "dimon222", "Dapal003", "Kekasi", "D1firehail" };
            foreach (var developer in developers)
            {
                try
                {
                    using (var request = new HttpRequestMessage
                    {
                        RequestUri = new Uri("https://api.warframe.market/v1/profile/" + developer + "/review"),
                        Method = HttpMethod.Post
                    })
                    {
                        request.Headers.Add("Authorization", "JWT " + JWT);
                        request.Headers.Add("language", "en");
                        request.Headers.Add("accept", "application/json");
                        request.Headers.Add("platform", "pc");
                        request.Headers.Add("auth_type", "header");
                        request.Content = new StringContent(msg, Encoding.UTF8, "application/json");
                        var response = await client.SendAsync(request);
                    }
                }
                catch (Exception e)
                {
                    AppMain.AddLog("PostReview: " + e.Message);
                    return false;
                }
            }
            return true;
        }

        public async Task SetIngameName()
        {
            try
            {
                using (var request = new HttpRequestMessage
                {
                    RequestUri = new Uri("https://api.warframe.market/v2/me"),
                    Method = HttpMethod.Get
                })
                {
                    request.Headers.Add("Authorization", "Bearer " + JWT);
                    request.Headers.Add("language", "en");
                    request.Headers.Add("accept", "application/json");
                    request.Headers.Add("platform", "pc");
                    request.Headers.Add("auth_type", "header");
                    var response = await client.SendAsync(request);
                    var profile = JsonConvert.DeserializeObject<JObject>(await response.Content.ReadAsStringAsync());
                    inGameName = profile["data"]?.Value<string>("ingameName");
                }
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"SetIngameName failed: {ex.Message}");
            }
        }

        #endregion
    }
}