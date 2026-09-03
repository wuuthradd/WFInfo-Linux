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
using System.Net.Sockets;
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
        private readonly string wfmItemsPath;
        private readonly string filterAllJsonFallbackPath;
        private readonly string sheetJsonFallbackPath;
        private readonly string etagsPath;
        private string filterAllETag;
        private string sheetJsonETag;
        public string JWT;
        private ClientWebSocket marketSocket = new ClientWebSocket();
        private CancellationTokenSource marketSocketCancellation = new CancellationTokenSource();

        private TaskCompletionSource<bool> _authenticationCompletionSource;
        private bool _isWebSocketAuthenticated = false;
        private Task _webSocketListenerTask;
        private const string filterAllJSON = "https://api.warframestat.us/wfinfo/filtered_items";
        private const string sheetJsonUrl = "https://api.warframestat.us/wfinfo/prices";
        private const string filterAllJSONFallback = "https://wfinfo.duckdns.org:21606/wfinfo/filtered-items";
        private const string sheetJsonUrlFallback = "https://wfinfo.duckdns.org:21606/wfinfo/prices";
        private const string wfmItemsUrl = "https://api.warframe.market/v2/items";
        public string inGameName { get; private set; } = string.Empty;
        readonly HttpClient client;
        readonly HttpMessageInvoker _wsInvoker;
        public bool rememberMe { get; set; }
        private ILogCapture _logCapture;
        private Task autoThread;

        // Reward tracking for AutoCSV/AutoCount/AutoList
        public List<List<string>> PrimeRewards { get; } = new();
        public short SelectedRewardIndex { get; set; } = 0;
        private readonly object _rewardsLock = new object();

        private static readonly object marketItemsLock = new object();
        private Dictionary<string, (string Name, string Slug)> _allItemNamesById = new();
        public bool HasItemNames => _allItemNamesById.Count > 0;

        public record WfmItemInfo(string Id, string Name, string Slug, int? MaxRank, bool BulkTradable, string[] Tags, string[] Subtypes, bool Vaulted);
        private List<WfmItemInfo> _allItems = new();
        public bool HasWfmItems => _allItems.Count > 0;

        public async Task<bool> TryReloadWfmItems()
        {
            try
            {
                var enItems = await GetWfmItemList("en");
                if (enItems.IsFallback)
                    return false;
                JArray items = JArray.FromObject(enItems.Data["data"]);
                var allNames = new Dictionary<string, (string Name, string Slug)>();
                var allItemsList = new List<WfmItemInfo>();
                foreach (var item in items)
                {
                    string id = item["id"]?.ToString();
                    string enName = item["i18n"]?["en"]?["name"]?.ToString();
                    string slug = item["slug"]?.ToString();
                    if (id != null && enName != null)
                    {
                        allNames[id] = (enName, slug ?? "");
                        int? maxRank = item["maxRank"]?.Value<int>();
                        bool bulk = item["bulkTradable"]?.Value<bool>() ?? false;
                        string[] tags = item["tags"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                        string[] subtypes = item["subtypes"]?.Values<string>().ToArray();
                        bool vaulted = item["vaulted"]?.Value<bool>() ?? false;
                        allItemsList.Add(new WfmItemInfo(id, enName, slug ?? "", maxRank, bulk, tags, subtypes, vaulted));
                    }
                }
                _allItemNamesById = allNames;
                _allItems = allItemsList;
                SaveWfmItems(allItemsList);
                return true;
            }
            catch (Exception ex)
            {
                AppMain.AddLog("TryReloadWfmItems failed: " + ex.Message);
                return false;
            }
        }

        private void SaveWfmItems(List<WfmItemInfo> items)
        {
            try
            {
                var arr = new JArray();
                foreach (var item in items)
                {
                    var obj = new JObject
                    {
                        ["id"] = item.Id,
                        ["name"] = item.Name,
                        ["slug"] = item.Slug,
                        ["bulkTradable"] = item.BulkTradable,
                        ["vaulted"] = item.Vaulted,
                    };
                    if (item.MaxRank.HasValue) obj["maxRank"] = item.MaxRank.Value;
                    if (item.Subtypes != null && item.Subtypes.Length > 0) obj["subtypes"] = new JArray(item.Subtypes);
                    arr.Add(obj);
                }
                File.WriteAllText(wfmItemsPath, arr.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"SaveWfmItems failed: {ex.Message}");
            }
        }

        private void LoadWfmItems()
        {
            if (!File.Exists(wfmItemsPath)) return;
            try
            {
                var arr = JsonConvert.DeserializeObject<JArray>(File.ReadAllText(wfmItemsPath));
                if (arr != null && arr.Count > 0)
                    LoadWfmItemsFromArray(arr, useNestedI18n: false);
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"LoadWfmItems failed: {ex.Message}");
            }
        }

        private void LoadWfmItemsFromArray(JArray items, bool useNestedI18n)
        {
            var dict = new Dictionary<string, (string Name, string Slug)>();
            var itemsList = new List<WfmItemInfo>();
            foreach (var item in items)
            {
                string id = item["id"]?.ToString();
                string name = useNestedI18n
                    ? item["i18n"]?["en"]?["name"]?.ToString()
                    : item["name"]?.ToString();
                string slug = item["slug"]?.ToString();
                if (id != null && name != null)
                {
                    dict[id] = (name, slug ?? "");
                    int? maxRank = item["maxRank"]?.Value<int>();
                    bool bulk = item["bulkTradable"]?.Value<bool>() ?? false;
                    string[] tags = item["tags"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                    string[] subtypes = item["subtypes"]?.Values<string>().ToArray();
                    bool vaulted = item["vaulted"]?.Value<bool>() ?? false;
                    itemsList.Add(new WfmItemInfo(id, name, slug ?? "", maxRank, bulk, tags, subtypes, vaulted));
                }
            }
            _allItemNamesById = dict;
            _allItems = itemsList;
        }

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
            wfmItemsPath = Path.Combine(applicationDirectory, "wfm_items.json");
            filterAllJsonFallbackPath = Path.Combine(applicationDirectory, "fallback_equipment_list.json");
            sheetJsonFallbackPath = Path.Combine(applicationDirectory, "fallback_price_sheet.json");
            etagsPath = Path.Combine(applicationDirectory, "etags.json");

            Directory.CreateDirectory(applicationDirectory);
            LoadAllETags();

            WebProxy proxy = null;
            string proxy_string = Environment.GetEnvironmentVariable("https_proxy")
                ?? Environment.GetEnvironmentVariable("HTTPS_PROXY")
                ?? Environment.GetEnvironmentVariable("http_proxy")
                ?? Environment.GetEnvironmentVariable("HTTP_PROXY");
            if (proxy_string != null)
                proxy = new WebProxy(new Uri(proxy_string));

            var handler = new SocketsHttpHandler
            {
                Proxy = proxy,
                UseProxy = proxy != null,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                ConnectCallback = ConnectPreferIpv4
            };
            client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WFInfo/" + AppMain.BuildVersion);
            _wsInvoker = new HttpMessageInvoker(handler, disposeHandler: false);

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

        private void LoadAllETags()
        {
            filterAllETag = null;
            sheetJsonETag = null;
            try
            {
                if (File.Exists(etagsPath))
                {
                    var obj = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(etagsPath));
                    if (obj != null)
                    {
                        filterAllETag = obj["filtered-items"]?.ToObject<string>();
                        sheetJsonETag = obj["prices"]?.ToObject<string>();
                    }
                }
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"Failed to load ETags from {etagsPath}: {ex.Message}");
            }
        }

        private void SaveAllETags()
        {
            try
            {
                var obj = new JObject
                {
                    ["filtered-items"] = filterAllETag,
                    ["prices"] = sheetJsonETag
                };
                File.WriteAllText(etagsPath, obj.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"Failed to save ETags to {etagsPath}: {ex.Message}");
            }
        }

        public bool IsJwtLoggedIn()
        {
            // WFM JWT tokens are >300 chars
            return JWT != null && JWT.Length > 300;
        }

        public async Task<bool> ReloadItems()
        {
            (JObject Data, bool IsFallback) enItems;
            (JObject Data, bool IsFallback) localizedItems;
            try
            {
                enItems = await GetWfmItemList("en");
                localizedItems = _settings.Locale == "en" ? enItems : await GetWfmItemList(_settings.Locale);
            }
            catch (Exception ex)
            {
                AppMain.AddLog("WFM items API unavailable, order window will not work: " + ex.Message);
                return true;
            }

            JObject tempMarketItems = new JObject();
            JArray items = JArray.FromObject(enItems.Data["data"]);
            int primeCount = 0;

            var allNames = new Dictionary<string, (string Name, string Slug)>();
            var allItemsList = new List<WfmItemInfo>();
            foreach (var item in items)
            {
                string id = item["id"]?.ToString();
                string enName = item["i18n"]?["en"]?["name"]?.ToString();
                string slug = item["slug"]?.ToString();
                if (id != null && enName != null)
                {
                    allNames[id] = (enName, slug ?? "");
                    int? maxRank = item["maxRank"]?.Value<int>();
                    bool bulk = item["bulkTradable"]?.Value<bool>() ?? false;
                    string[] tags = item["tags"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                    string[] subtypes = item["subtypes"]?.Values<string>().ToArray();
                    bool vaulted = item["vaulted"]?.Value<bool>() ?? false;
                    allItemsList.Add(new WfmItemInfo(id, enName, slug ?? "", maxRank, bulk, tags, subtypes, vaulted));
                }
            }
            _allItemNamesById = allNames;
            _allItems = allItemsList;
            SaveWfmItems(allItemsList);

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

        private async Task<(T Data, bool IsFallback, bool IsLocalFallback, string NewETag)> GetTieredData<T>(
            string upstreamUrl,
            string fallbackUrl,
            string localCachePath,
            string label,
            Func<T, bool> validate,
            string currentETag = null) where T : JToken
        {
            // Tier 1: upstream api.warframestat.us (no ETag support)
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (var upstreamResp = await client.GetAsync(upstreamUrl, cts.Token).ConfigureAwait(false))
                {
                    if (upstreamResp.IsSuccessStatusCode)
                    {
                        string response = await upstreamResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        T data = JsonConvert.DeserializeObject<T>(response);
                        if (validate(data))
                        {
                            File.WriteAllText(localCachePath, response);
                            return (data, false, false, null);
                        }
                        AppMain.AddLog($"Upstream {upstreamUrl} returned invalid payload, trying fallback");
                    }
                    else
                    {
                        AppMain.AddLog($"Upstream {upstreamUrl} returned {(int)upstreamResp.StatusCode}, trying fallback");
                    }
                }
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"Upstream {upstreamUrl} unreachable: {ex.Message}, trying fallback");
            }

            // Tier 2: WFInfoServer fallback (gzipped, User-Agent required, supports ETags)
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (var fbReq = new HttpRequestMessage(HttpMethod.Get, fallbackUrl))
                {
                    if (!string.IsNullOrEmpty(currentETag))
                    {
                        fbReq.Headers.TryAddWithoutValidation("If-None-Match", currentETag);
                    }
                    using (var fbResp = await client.SendAsync(fbReq, cts.Token).ConfigureAwait(false))
                    {
                        // Handle 304 Not Modified - use cached data, retry without ETag if cache invalid
                        if (fbResp.StatusCode == System.Net.HttpStatusCode.NotModified)
                        {
                            AppMain.AddLog($"Fallback {label} unchanged (304), using cached data");
                            if (File.Exists(localCachePath))
                            {
                                string response = File.ReadAllText(localCachePath);
                                T data = JsonConvert.DeserializeObject<T>(response);
                                if (validate(data))
                                    return (data, true, false, currentETag);
                            }
                            AppMain.AddLog($"Fallback {label} 304 but no valid cached data, retrying without ETag");
                            using (var retryReq = new HttpRequestMessage(HttpMethod.Get, fallbackUrl))
                            using (var retryResp = await client.SendAsync(retryReq, cts.Token).ConfigureAwait(false))
                            {
                                if (retryResp.IsSuccessStatusCode)
                                {
                                    string retryBody = await retryResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                    T retryData = JsonConvert.DeserializeObject<T>(retryBody);
                                    if (validate(retryData))
                                    {
                                        File.WriteAllText(localCachePath, retryBody);
                                        string newETag = retryResp.Headers.ETag?.Tag;
                                        AppMain.AddLog($"Fallback {label} repaired from unconditional response");
                                        return (retryData, true, false, newETag);
                                    }
                                }
                                AppMain.AddLog($"Fallback {label} retry also failed or invalid");
                            }
                        }
                        else if (fbResp.IsSuccessStatusCode)
                        {
                            string response = await fbResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            T data = JsonConvert.DeserializeObject<T>(response);
                            if (validate(data))
                            {
                                File.WriteAllText(localCachePath, response);
                                string newETag = fbResp.Headers.ETag?.Tag;
                                AppMain.AddLog($"Fallback {label} fetched successfully from {fallbackUrl}");
                                return (data, true, false, newETag);
                            }
                            AppMain.AddLog($"Fallback {fallbackUrl} returned invalid payload");
                        }
                        else
                        {
                            AppMain.AddLog($"Fallback {fallbackUrl} returned {(int)fbResp.StatusCode}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppMain.AddLog($"Fallback {fallbackUrl} failed: {ex.Message}");
            }

            // Tier 3: local file
            AppMain.AddLog("Using local fallback file " + localCachePath);
            if (File.Exists(localCachePath))
            {
                string response = File.ReadAllText(localCachePath);
                T data = JsonConvert.DeserializeObject<T>(response);
                if (validate(data))
                    return (data, true, true, currentETag);
                AppMain.AddLog($"Local fallback {localCachePath} has invalid payload");
            }
            throw new AggregateException($"No data source available for {label}");
        }

        private static bool IsValidFilteredPayload(JObject data)
        {
            return data != null && data["relics"] != null && data["eqmt"] != null && data["ignored_items"] != null;
        }

        private async Task<(JObject Data, bool IsFallback, bool IsLocalFallback, string NewETag)> GetAllFiltered()
        {
            return await GetTieredData<JObject>(
                filterAllJSON,
                filterAllJSONFallback,
                filterAllJsonFallbackPath,
                "filtered-items",
                IsValidFilteredPayload,
                filterAllETag).ConfigureAwait(false);
        }

        private async Task<(JArray Data, bool IsFallback, bool IsLocalFallback, string NewETag)> GetSheetData()
        {
            return await GetTieredData<JArray>(
                sheetJsonUrl,
                sheetJsonUrlFallback,
                sheetJsonFallbackPath,
                "prices",
                data => data != null && data.Count > 0,
                sheetJsonETag).ConfigureAwait(false);
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

            string oldMarketTimeText;
            string oldEquipmentTimeText;

            // When ETags are available, always proceed to fetch (304 handling makes it cheap).
            // Fall back to timestamp-based freshness check only when ETags are absent.
            bool hasETags = !string.IsNullOrEmpty(filterAllETag) || !string.IsNullOrEmpty(sheetJsonETag);

            bool marketIsRecent = false;
            bool equipmentIsRecent = false;

            if (!hasETags)
            {
                if (marketData.TryGetValue("version", out _) && marketData["version"].ToObject<string>() == AppMain.BuildVersion
                    && marketData.TryGetValue("timestamp", out var timestamp) && timestamp.ToObject<DateTime>() > now.AddHours(-12))
                {
                    marketIsRecent = true;
                    oldMarketTimeText = timestamp.ToObject<DateTime>().ToString("MMM dd - HH:mm", AppMain.culture);
                }
                else
                {
                    oldMarketTimeText = "UNKNOWN";
                }

                if (equipmentData.TryGetValue("timestamp", out var eqTs) && eqTs.ToObject<DateTime>() > now.AddHours(-12))
                {
                    equipmentIsRecent = true;
                    oldEquipmentTimeText = eqTs.ToObject<DateTime>().ToString("MMM dd - HH:mm", AppMain.culture);
                }
                else
                {
                    oldEquipmentTimeText = "UNKNOWN";
                }
            }
            else
            {
                oldMarketTimeText = marketData.TryGetValue("timestamp", out var ts)
                    ? ts.ToObject<DateTime>().ToString("MMM dd - HH:mm", AppMain.culture)
                    : "UNKNOWN";
                oldEquipmentTimeText = equipmentData.TryGetValue("timestamp", out var ets)
                    ? ets.ToObject<DateTime>().ToString("MMM dd - HH:mm", AppMain.culture)
                    : "UNKNOWN";
            }

            if (!parseHasFailed && !force && marketIsRecent && equipmentIsRecent)
            {
                if (_allItemNamesById.Count == 0)
                    LoadWfmItems();
                OnMarketDataUpdated?.Invoke(oldMarketTimeText);
                OnDropDataUpdated?.Invoke(oldEquipmentTimeText);
                return;
            }

            var allFiltered = await GetAllFiltered();
            var sheetData = await GetSheetData();
            filterAllETag = allFiltered.NewETag;
            sheetJsonETag = sheetData.NewETag;
            SaveAllETags();
            var marketItemsIsFallback = await ReloadItems();
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
            if (!allFiltered.IsLocalFallback && !sheetData.IsLocalFallback && !marketItemsIsFallback)
            {
                newMarketData["timestamp"] = now;
                marketTimeText = now.ToString("MMM dd - HH:mm", AppMain.culture);
            }
            else { marketTimeText = "FALLBACK"; }

            if (!allFiltered.IsLocalFallback)
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

            if (line.Contains("Got rewards"))
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
                var watch = Stopwatch.StartNew();
                long fixedStop = watch.ElapsedMilliseconds + _settings.FixedAutoDelay;
                long pollInterval = _settings.AutoDelay;
                long maxWait = fixedStop + 5000;
                long wait = fixedStop;

                _window.UpdateWindow();

                double configScale = OCR.ReadUiScaleFromConfig();
                if (configScale > 0)
                {
                    OCR.uiScaling = configScale;
                    AppMain.AddLog($"Auto: UI scaling {configScale:P0} from EE.cfg");
                }

                // Initial delay: wait FixedAutoDelay before polling
                long initialRemaining = fixedStop - watch.ElapsedMilliseconds;
                if (initialRemaining > 0)
                    await Task.Delay((int)initialRemaining).ConfigureAwait(false);

                // Poll at AutoDelay intervals until theme detected or timeout
                SkiaSharp.SKBitmap pollShot = null;
                while (watch.ElapsedMilliseconds < maxWait)
                {
                    pollShot?.Dispose();
                    pollShot = OCR.CaptureScreenshot();
                    if (pollShot == null) break;

                    OCR.GetThemeWeighted(out double diff, pollShot);
                    if (diff > 0.005)
                    {
                        long remaining = wait - watch.ElapsedMilliseconds;
                        if (remaining > 0)
                            await Task.Delay((int)remaining).ConfigureAwait(false);
                        AppMain.AddLog($"Auto: theme detected, {watch.ElapsedMilliseconds}ms, processing");
                        OCR.ProcessRewardScreen(pollShot);
                        pollShot = null;
                        AppMain.AddLog($"Auto: total {watch.ElapsedMilliseconds}ms");
                        watch.Stop();
                        return;
                    }
                    wait += pollInterval;
                    long delayMs = Math.Min(wait - watch.ElapsedMilliseconds, maxWait - watch.ElapsedMilliseconds);
                    if (delayMs > 0)
                        await Task.Delay((int)delayMs).ConfigureAwait(false);
                }

                // Timeout: process anyway with the last poll screenshot
                AppMain.AddLog($"Auto: timeout after {watch.ElapsedMilliseconds}ms, processing anyway");
                if (pollShot != null)
                {
                    OCR.ProcessRewardScreen(pollShot);
                    pollShot = null;
                }
                else
                {
                    OCR.ProcessRewardScreen();
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

        private static async ValueTask<Stream> ConnectPreferIpv4(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
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

                using var connectCts = new CancellationTokenSource(30000);
                await marketSocket.ConnectAsync(new Uri("wss://warframe.market/socket-v2"), _wsInvoker, connectCts.Token);

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
            {
                AppMain.AddLog($"WebSocket not connected, reconnecting to set status {status}");
                if (!await OpenWebSocket())
                {
                    AppMain.AddLog($"Cannot set status to {status}: WebSocket offline");
                    AppMain.StatusUpdate("warframe.market socket offline", 1);
                    return;
                }
            }

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

        public async Task<JObject> GetTopListingsBySlug(string urlSlug, int? rank = null, int? rankLt = null, string subtype = null)
        {
            if (string.IsNullOrEmpty(urlSlug))
                return null;
            try
            {
                var url = "https://api.warframe.market/v2/orders/item/" + urlSlug + "/top";
                var queryParts = new List<string>();
                if (rankLt.HasValue)
                    queryParts.Add("rankLt=" + rankLt.Value);
                else if (rank.HasValue)
                    queryParts.Add("rank=" + rank.Value);
                if (!string.IsNullOrEmpty(subtype))
                    queryParts.Add("subtype=" + Uri.EscapeDataString(subtype));
                if (queryParts.Count > 0)
                    url += "?" + string.Join("&", queryParts);
                using (var request = new HttpRequestMessage
                {
                    RequestUri = new Uri(url),
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
                AppMain.AddLog("GetTopListingsBySlug: " + e.Message);
                return null;
            }
        }

        public async Task<JObject> GetItemInfoBySlug(string urlSlug)
        {
            if (string.IsNullOrEmpty(urlSlug))
                return null;
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.warframe.market/v2/items/" + urlSlug))
                {
                    request.Headers.Add("accept", "application/json");
                    request.Headers.Add("platform", "pc");
                    var response = await client.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) return null;
                    return JsonConvert.DeserializeObject<JObject>(body)?["data"] as JObject;
                }
            }
            catch (Exception e)
            {
                AppMain.AddLog("GetItemInfoBySlug: " + e.Message);
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

        public async Task<bool> UpdateListing(string listingId, int platinum, int quantity, bool visible = true, int? rank = null, int? perTrade = null, string subtype = null)
        {
            try
            {
                using (var request = new HttpRequestMessage
                {
                    RequestUri = new Uri("https://api.warframe.market/v2/order/" + listingId),
                    Method = HttpMethod.Patch
                })
                {
                    var bodyObj = new JObject
                    {
                        ["platinum"] = platinum,
                        ["quantity"] = quantity,
                        ["visible"] = visible
                    };
                    if (rank.HasValue) bodyObj["rank"] = rank.Value;
                    if (perTrade.HasValue) bodyObj["perTrade"] = perTrade.Value;
                    if (!string.IsNullOrEmpty(subtype)) bodyObj["subtype"] = subtype;
                    var json = bodyObj.ToString();
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    request.Headers.Add("Authorization", "Bearer " + JWT);
                    request.Headers.Add("language", "en");
                    request.Headers.Add("accept", "application/json");
                    request.Headers.Add("platform", "pc");
                    request.Headers.Add("auth_type", "header");

                    var response = await client.SendAsync(request);
                    var responseBody = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) throw new Exception($"{(int)response.StatusCode}: {responseBody}");
                    return true;
                }
            }
            catch (Exception e)
            {
                AppMain.AddLog($"UpdateListing (extended): {e.Message}");
                return false;
            }
        }

        public async Task<JArray> GetAllMyOrders()
        {
            try
            {
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
                    return (JArray)payload?["data"];
                }
            }
            catch (Exception e)
            {
                AppMain.AddLog($"GetAllMyOrders: {e.Message}");
                return null;
            }
        }

        public async Task<bool> CloseOrder(string orderId, int quantity = 1)
        {
            try
            {
                using (var request = new HttpRequestMessage
                {
                    RequestUri = new Uri("https://api.warframe.market/v2/order/" + orderId + "/close"),
                    Method = HttpMethod.Post
                })
                {
                    var json = JsonConvert.SerializeObject(new { quantity });
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    request.Headers.Add("Authorization", "Bearer " + JWT);
                    request.Headers.Add("language", "en");
                    request.Headers.Add("accept", "application/json");
                    request.Headers.Add("platform", "pc");
                    request.Headers.Add("auth_type", "header");
                    var response = await client.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync();
                        throw new Exception($"{(int)response.StatusCode}: {responseBody}");
                    }
                    return true;
                }
            }
            catch (Exception e)
            {
                AppMain.AddLog($"CloseOrder: {e.Message}");
                return false;
            }
        }

        public async Task<JArray> GetMyTransactionData()
        {
            try
            {
                if (string.IsNullOrEmpty(inGameName))
                    await SetIngameName();
                if (string.IsNullOrEmpty(inGameName))
                {
                    AppMain.AddLog("GetMyTransactionData: no ingame name available");
                    return null;
                }

                using (var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.warframe.market/v1/profile/{inGameName}/statistics"))
                {
                    req.Headers.Add("Authorization", "Bearer " + JWT);
                    req.Headers.Add("language", "en");
                    req.Headers.Add("accept", "application/json");
                    req.Headers.Add("platform", "pc");
                    var resp = await client.SendAsync(req);
                    var body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        AppMain.AddLog($"GetMyTransactionData: {resp.StatusCode}");
                        return null;
                    }
                    var payload = JsonConvert.DeserializeObject<JObject>(body);
                    return payload?["payload"]?["closed_orders"] as JArray;
                }
            }
            catch (Exception e)
            {
                AppMain.AddLog($"GetMyTransactionData: {e.Message}");
                return null;
            }
        }

        public async Task<bool> DeleteOrder(string orderId)
        {
            try
            {
                using (var request = new HttpRequestMessage
                {
                    RequestUri = new Uri("https://api.warframe.market/v2/order/" + orderId),
                    Method = HttpMethod.Delete
                })
                {
                    request.Headers.Add("Authorization", "Bearer " + JWT);
                    request.Headers.Add("language", "en");
                    request.Headers.Add("accept", "application/json");
                    request.Headers.Add("platform", "pc");
                    request.Headers.Add("auth_type", "header");
                    var response = await client.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync();
                        throw new Exception(responseBody);
                    }
                    return true;
                }
            }
            catch (Exception e)
            {
                AppMain.AddLog($"DeleteOrder: {e.Message}");
                return false;
            }
        }

        public async Task<string> DeleteClosedOrder(string orderId)
        {
            try
            {
                var url = "https://api.warframe.market/v1/profile/statistics/remove/" + orderId;
                AppMain.AddLog($"DeleteClosedOrder: DELETE {url}");
                using (var request = new HttpRequestMessage(HttpMethod.Delete, url))
                {
                    request.Headers.Add("Authorization", "JWT " + JWT);
                    request.Headers.Add("language", "en");
                    request.Headers.Add("accept", "application/json");
                    request.Headers.Add("platform", "pc");
                    var response = await client.SendAsync(request);
                    string body = await response.Content.ReadAsStringAsync();
                    AppMain.AddLog($"DeleteClosedOrder: {(int)response.StatusCode} method={response.RequestMessage?.Method} uri={response.RequestMessage?.RequestUri}");
                    if (!response.IsSuccessStatusCode)
                    {
                        AppMain.AddLog($"DeleteClosedOrder: {body}");
                        return $"{(int)response.StatusCode}: {response.ReasonPhrase}";
                    }
                    return null;
                }
            }
            catch (Exception e)
            {
                AppMain.AddLog($"DeleteClosedOrder: {e.Message}");
                return e.Message;
            }
        }

        public string ItemIdToDisplayName(string itemId)
        {
            if (_allItemNamesById.TryGetValue(itemId, out var entry))
                return entry.Name;
            lock (marketItemsLock)
            {
                if (marketItems != null && marketItems.TryGetValue(itemId, out var token))
                {
                    string[] vals = token.ToString().Split('|');
                    if (vals.Length > 0) return vals[0];
                }
            }
            return null;
        }

        public string ItemIdToUrlSlug(string itemId)
        {
            if (_allItemNamesById.TryGetValue(itemId, out var entry))
                return entry.Slug;
            lock (marketItemsLock)
            {
                if (marketItems != null && marketItems.TryGetValue(itemId, out var token))
                {
                    string[] vals = token.ToString().Split('|');
                    if (vals.Length > 1) return vals[1];
                }
            }
            return null;
        }

        public List<WfmItemInfo> SearchItems(string query, int limit = 15)
        {
            if (string.IsNullOrWhiteSpace(query) || _allItems.Count == 0)
                return new List<WfmItemInfo>();
            string q = query.Trim().ToLowerInvariant();
            return _allItems
                .Where(i => i.Name.ToLowerInvariant().Contains(q))
                .OrderBy(i =>
                {
                    string lower = i.Name.ToLowerInvariant();
                    if (lower == q) return 0;
                    if (lower.StartsWith(q)) return 1;
                    return 2;
                })
                .ThenBy(i => i.Name.Length)
                .Take(limit)
                .ToList();
        }

        public WfmItemInfo GetItemInfoById(string itemId)
        {
            return _allItems.FirstOrDefault(i => i.Id == itemId);
        }

        // Returns the total number of items needed for a set trade.
        // Uses equipmentData part counts (e.g. Ankyros Prime: Blade x2 + Blueprint x1 + Gauntlet x2 = 5).
        // Falls back to counting WFM items by slug prefix for non-prime sets.
        // Returns -1 if the set is not found.
        public int GetSetPartCount(string setName)
        {
            if (!setName.EndsWith(" Set"))
                return -1;

            // Try equipmentData first (covers primes, has per-part counts)
            string primeName = setName.Substring(0, setName.Length - 4);
            if (equipmentData != null && equipmentData.TryGetValue(primeName, out var val) && val is JObject obj)
            {
                var parts = obj["parts"] as JObject;
                if (parts != null)
                {
                    int total = 0;
                    foreach (var part in parts)
                    {
                        int cnt = (part.Value as JObject)?["count"]?.ToObject<int>() ?? 1;
                        total += cnt;
                    }
                    return total > 0 ? total : -1;
                }
            }

            // Non-prime sets where slug-based counting gives the wrong total.
            var nonPrimeOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "Broken War Set", 3 },       // 2x War Blade + 1x War Hilt (slugs don't match set prefix)
                { "Dual Decurion Set", 4 },    // 2x Decurion Barrel + 2x Decurion Receiver
                { "Dual Viciss Set", 4 },      // 2x Blade + 2x Hilt
                { "Onorix Set", 3 },           // 2x Blade + 1x Handle
                { "Pathocyst Set", 4 },        // 2x Blade + 1x Blueprint + 1x Subcortex
            };

            if (nonPrimeOverrides.TryGetValue(setName, out int overrideCount))
                return overrideCount;

            // Fallback: count WFM items sharing the base slug (covers non-prime sets)
            var setItem = _allItems.FirstOrDefault(i => i.Name.Equals(setName, StringComparison.OrdinalIgnoreCase));
            if (setItem == null)
                return -1;
            string baseSlug = setItem.Slug.Replace("_set", "");
            int count = 0;
            foreach (var item in _allItems)
            {
                if (item.Slug == setItem.Slug) continue;
                if (!item.Slug.StartsWith(baseSlug + "_")) continue;
                if (item.Slug.EndsWith("_set")) continue;
                if (item.Name.Contains("Prime")) continue;
                count++;
            }
            return count > 0 ? count : -1;
        }

        public async Task<string> CreateOrder(string itemId, string type, int platinum, int quantity,
            bool visible, int? rank = null, int? perTrade = null, string subtype = null)
        {
            var (_, error) = await CreateOrderReturningId(itemId, type, platinum, quantity, visible, rank, perTrade, subtype);
            return error;
        }

        // Returns (newOrderId, error). On success error is null; on failure newOrderId is null.
        public async Task<(string newOrderId, string error)> CreateOrderReturningId(string itemId, string type, int platinum, int quantity,
            bool visible, int? rank = null, int? perTrade = null, string subtype = null)
        {
            try
            {
                var body = new JObject
                {
                    ["itemId"] = itemId,
                    ["type"] = type,
                    ["platinum"] = platinum,
                    ["quantity"] = quantity,
                    ["visible"] = visible
                };
                if (rank.HasValue) body["rank"] = rank.Value;
                if (perTrade.HasValue) body["perTrade"] = perTrade.Value;
                if (!string.IsNullOrEmpty(subtype)) body["subtype"] = subtype;

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.warframe.market/v2/order");
                request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
                request.Headers.Add("Authorization", "Bearer " + JWT);
                request.Headers.Add("language", "en");
                request.Headers.Add("accept", "application/json");
                request.Headers.Add("platform", "pc");
                request.Headers.Add("auth_type", "header");
                var response = await client.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    AppMain.AddLog($"CreateOrder: {(int)response.StatusCode} {responseBody}");
                    if (responseBody.Contains("exceededOrderLimitSamePrice"))
                        return (null, "The order you're trying to create already exists.");
                    return (null, $"{(int)response.StatusCode}: {response.ReasonPhrase}");
                }
                var parsed = JsonConvert.DeserializeObject<JObject>(responseBody);
                string newId = parsed?["data"]?["id"]?.ToString();
                return (newId, null);
            }
            catch (Exception e)
            {
                AppMain.AddLog($"CreateOrder: {e.Message}");
                return (null, e.Message);
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