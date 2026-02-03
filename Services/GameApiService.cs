using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using DropAI.TelegramBot;

namespace DropAI.Services
{
    public class GameApiService
    {
        private readonly HttpClient _httpClient;
        private readonly PredictionService _predictionService;
        private readonly SupabaseService _supabase;
        private string? _token;
        private bool _isPolling = false;
        private CancellationTokenSource? _pollCts;
        private TelegramBotService? _botService;
        private string? _lastProcessedResultIssue;
        private const string CONFIG_FILE = "config.json";
        
        private Dictionary<string, PredictionService.PredictionResult> _predictions = new();

        private const string API_BASE = "https://vn168api.com/api/webapi";
        
        public bool IsLoggedIn => !string.IsNullOrEmpty(_token);
        public bool IsPolling => _isPolling;
        public int CurrentGameId { get; set; } = 30; // Default WinGo 1Min

        public class LoginInfo
        {
            public string User { get; set; } = "";
            public string Pass { get; set; } = "";
        }

        public class ConfigInfo
        {
            public bool AutoStartPolling { get; set; } = false;
            public bool AutoBetEnabled { get; set; } = false;
            public int BaseBetAmount { get; set; } = 1000;
            public string MartingaleConfig { get; set; } = "1,2,5,12,28,65";
            public decimal TargetProfit { get; set; } = 0;
        }

        public class GameHistoryItem
        {
            public string IssueNumber { get; set; } = "";
            public int Number { get; set; }
            public string Size { get; set; } = "";
            public string Parity { get; set; } = "";
        }

        public GameApiService(PredictionService predictionService, SupabaseService supabase)
        {
            _httpClient = new HttpClient();
            _predictionService = predictionService;
            _supabase = supabase;
            ConfigureHttpClient();

            // Auto-login and Resume Polling
            _ = Task.Run(async () =>
            {
                try {
                    await Task.Delay(2000);
                    await _supabase.InitializeAsync();
                    
                    var saved = GetSavedLogin();
                    if (saved != null)
                    {
                        var config = LoadConfig();
                        ApplyConfig(config);

                        Console.WriteLine($"[GameAPI] Found saved login for {saved.User}. Attempting auto-login...");
                        if (await LoginAsync(saved.User, saved.Pass))
                        {
                            // Load historical data for AI training
                            Console.WriteLine("[GameAPI] Loading historical data for AI...");
                            var longHistory = await _supabase.GetRecentHistoryAsync(2000);
                            _predictionService.UpdateLongHistory(longHistory);

                            if (config.AutoStartPolling)
                            {
                                Console.WriteLine("[GameAPI] Resuming Polling based on saved config.");
                                StartPolling();
                            }
                        }
                    }
                } catch (Exception ex) {
                    Console.WriteLine($"[GameAPI] Auto-init Error: {ex.Message}");
                }
            });
        }

        private ConfigInfo LoadConfig()
        {
            try
            {
                if (File.Exists(CONFIG_FILE))
                {
                    return JsonSerializer.Deserialize<ConfigInfo>(File.ReadAllText(CONFIG_FILE)) ?? new ConfigInfo();
                }
            }
            catch { }
            return new ConfigInfo();
        }

        public bool AutoBetEnabled { get; set; } = false;
        public int BaseBetAmount { get; set; } = 1000;
        public int[] MartingaleMultipliers { get; set; } = new[] { 1, 2, 5, 12, 28, 65 };
        public int CurrentMartingaleStep { get; set; } = 0;
        public string? LastBetIssue { get; set; }
        public string? LastBetSide { get; set; }
        public int LastBetAmount { get; set; }
        public decimal InitialBalance { get; set; } = 0;
        public decimal TargetProfit { get; set; } = 0;
        public decimal CurrentProfit => IsLoggedIn ? (_lastKnownBalance - InitialBalance) : 0;
        private decimal _lastKnownBalance = 0;
        private HashSet<string> _bettedIssues = new(); // Track bet issues to prevent duplicates

        public void SaveConfig()
        {
            try
            {
                var config = new ConfigInfo 
                { 
                    AutoStartPolling = _isPolling,
                    AutoBetEnabled = AutoBetEnabled,
                    BaseBetAmount = BaseBetAmount,
                    MartingaleConfig = string.Join(",", MartingaleMultipliers),
                    TargetProfit = TargetProfit
                };
                File.WriteAllText(CONFIG_FILE, JsonSerializer.Serialize(config));
            }
            catch { }
        }

        private void ApplyConfig(ConfigInfo config)
        {
            AutoBetEnabled = config.AutoBetEnabled;
            BaseBetAmount = config.BaseBetAmount;
            TargetProfit = config.TargetProfit;
            CurrentMartingaleStep = 0; // Reset on config load
            try {
                MartingaleMultipliers = config.MartingaleConfig.Split(',').Select(int.Parse).ToArray();
            } catch { }
        }

        public void SetBotService(TelegramBotService botService)
        {
            _botService = botService;
        }

        private void ConfigureHttpClient()
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://387vn.com");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://387vn.com/");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Ar-Origin", "https://387vn.com");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", 
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        public async Task<bool> LoginAsync(string username, string password)
        {
            try
            {
                StopPolling(); // Reset polling on new login
                _token = null;

                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string random = Guid.NewGuid().ToString("N").ToLower(); 
                string deviceId = "fd52ea6688df74da7e2bafaf4d6ecd48"; 

                var signParams = new SortedDictionary<string, object>
                {
                    { "username", username }, { "pwd", password }, { "phonetype", 0 },
                    { "logintype", "mobile" }, { "packId", "" }, { "deviceId", deviceId },
                    { "language", 2 }, { "random", random }, { "timestamp", timestamp }
                };

                string signature = SignPayload(signParams); 
                var jsonBody = new 
                {
                    username = username, pwd = password, phonetype = 0, logintype = "mobile",
                    packId = "", deviceId = deviceId, language = 2, random = random,
                    signature = signature, timestamp = timestamp
                };
                
                var resultJson = await SendPostAsync($"{API_BASE}/Login", jsonBody);
                if (string.IsNullOrEmpty(resultJson)) return false;

                using var doc = JsonDocument.Parse(resultJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("code", out var code) && code.GetInt32() == 0)
                {
                    var data = root.GetProperty("data");
                    _token = data.GetProperty("token").GetString();
                    Console.WriteLine($"[GameAPI] Login Success for {username}");
                    SaveLoginInfo(username, password);
                    return true;
                }
                Console.WriteLine($"[GameAPI] Login Failed: {resultJson}");
                return false;
            }
            catch (Exception ex) { Console.WriteLine($"[GameAPI] Login Error: {ex.Message}"); return false; }
        }

        public async Task<List<GameHistoryItem>> GetGameHistoryAsync(int gameType = 30, int pageSize = 100)
        {
            if (!IsLoggedIn) return new List<GameHistoryItem>();
            try
            {
                string random = Guid.NewGuid().ToString("N").ToLower();
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var signParams = new SortedDictionary<string, object>
                {
                    { "typeId", gameType }, { "pageSize", pageSize }, { "pageNo", 1 },
                    { "language", 2 }, { "random", random }
                };
                string signature = SignPayload(signParams);
                var payload = new { typeId = gameType, pageSize = pageSize, pageNo = 1, language = 2, random = random, signature = signature, timestamp = timestamp };
                var json = await SendPostAsync($"{API_BASE}/GetNoaverageEmerdList", payload);
                if (string.IsNullOrEmpty(json)) return new List<GameHistoryItem>();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("code", out var code) && code.GetInt32() == 0)
                {
                    var list = root.GetProperty("data").GetProperty("list");
                    var history = new List<GameHistoryItem>();
                    foreach (var item in list.EnumerateArray())
                    {
                        var issueNumber = item.GetProperty("issueNumber").GetString()!;
                        var number = int.Parse(item.GetProperty("number").GetString()!);
                        history.Add(new GameHistoryItem { IssueNumber = issueNumber, Number = number, Size = number >= 5 ? "Big" : "Small", Parity = number % 2 == 0 ? "Double" : "Single" });
                    }
                    return history;
                }
                return new List<GameHistoryItem>();
            }
            catch { return new List<GameHistoryItem>(); }
        }

        public void Logout()
        {
            StopPolling();
            _token = null;
            if (File.Exists("login.json")) File.Delete("login.json");
            Console.WriteLine("[GameAPI] User logged out.");
        }

        public async Task<decimal> GetBalanceAsync()
        {
            if (!IsLoggedIn) return 0;
            try
            {
                string random = Guid.NewGuid().ToString("N").ToLower();
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var signParams = new SortedDictionary<string, object> { { "language", 2 }, { "random", random } };
                string signature = SignPayload(signParams);
                var payload = new { language = 2, random = random, signature = signature, timestamp = timestamp };
                var json = await SendPostAsync($"{API_BASE}/GetBalance", payload);
                if (string.IsNullOrEmpty(json)) return 0;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("code", out var code) && code.GetInt32() == 0)
                {
                    _lastKnownBalance = root.GetProperty("data").GetProperty("amount").GetDecimal();
                    return _lastKnownBalance;
                }
                return 0;
            }
            catch { return 0; }
        }

        public void StartPolling()
        {
            if (_isPolling) return;
            _isPolling = true;
            SaveConfig();
            _pollCts = new CancellationTokenSource();
            _ = PollLoopAsync(_pollCts.Token);
            Console.WriteLine("[GameAPI] Polling Loop ACTIVATED.");
        }

        public void StopPolling()
        {
            _isPolling = false;
            SaveConfig();
            _pollCts?.Cancel();
            Console.WriteLine("[GameAPI] Polling Loop DEACTIVATED.");
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            int loopCount = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (IsLoggedIn)
                    {
                        var history = await GetGameHistoryAsync(CurrentGameId, 30);
                        if (history.Count > 0)
                        {
                            var latest = history[0];
                            if (_lastProcessedResultIssue != latest.IssueNumber)
                            {
                                _lastProcessedResultIssue = latest.IssueNumber;
                                
                                // Save to Supabase
                                await _supabase.AddHistoryAsync(latest);

                                // Refresh long history and train AI every 20 rounds
                                if (loopCount++ % 20 == 0)
                                {
                                    var longHistory = await _supabase.GetRecentHistoryAsync(2000);
                                    _predictionService.UpdateLongHistory(longHistory);
                                    await _supabase.RunCleanupAsync(); // Also cleanup old data
                                }

                                // AI Prediction
                                string nextIssue = (long.Parse(latest.IssueNumber) + 1).ToString();
                                var prediction = _predictionService.PredictNext(history);
                                _predictions[nextIssue] = prediction;

                                // Handle Martingale Result
                                if (LastBetIssue == latest.IssueNumber)
                                {
                                    if (LastBetSide == latest.Size)
                                    {
                                        Console.WriteLine($"[AutoBet] WIN detected on {latest.IssueNumber}! Resetting step.");
                                        CurrentMartingaleStep = 0;
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[AutoBet] LOSS detected on {latest.IssueNumber}. Next step.");
                                        CurrentMartingaleStep++;
                                        if (CurrentMartingaleStep >= MartingaleMultipliers.Length) CurrentMartingaleStep = 0;
                                    }
                                    LastBetIssue = null;
                                }

                                // Update balances for profit tracking
                                var balance = await GetBalanceAsync();
                                
                                // Auto Stop on Target Profit
                                if (AutoBetEnabled && TargetProfit > 0 && CurrentProfit >= TargetProfit)
                                {
                                    Console.WriteLine($"[AutoBet] TARGET REACHED: +{CurrentProfit} / {TargetProfit}. Stopping.");
                                    AutoBetEnabled = false;
                                    SaveConfig();
                                    if (_botService != null) await _botService.BroadcastSimpleAsync($"🎯 *MỤC TIÊU ĐÃ ĐẠT!* \nLợi nhuận: `+{CurrentProfit:N0} đ` \nĐã dừng cược tự động.");
                                }

                                // Execute Auto Bet
                                if (AutoBetEnabled && prediction.Pred != "Wait")
                                {
                                    int multiplier = MartingaleMultipliers[CurrentMartingaleStep];
                                    int betAmount = multiplier * 1000; // For display only
                                    Console.WriteLine($"[AutoBet] EXECUTING: {prediction.Pred} - {betAmount}đ on {nextIssue}");
                                    
                                    var success = await PlaceBetAsync(CurrentGameId, nextIssue, prediction.Pred, multiplier);
                                    if (success)
                                    {
                                        LastBetIssue = nextIssue;
                                        LastBetSide = prediction.Pred;
                                        LastBetAmount = betAmount;
                                    }
                                }

                                if (_botService != null)
                                {
                                    balance = await GetBalanceAsync();
                                    
                                    var historyWithPred = history.Select(h => {
                                        string predStr = "-";
                                        string resultStr = "";
                                        if (_predictions.TryGetValue(h.IssueNumber, out var pred))
                                        {
                                            predStr = pred.Pred;
                                            if (pred.Pred != "Wait") {
                                                resultStr = (pred.Pred == h.Size) ? "✅" : "❌";
                                            } else {
                                                resultStr = "  "; 
                                            }
                                        }
                                        return new {
                                            issue = h.IssueNumber, num = h.Number, sz = h.Size, p = h.Parity, pred = predStr, res = resultStr
                                        };
                                    }).Take(10).ToList();

                                    string historyJson = JsonSerializer.Serialize(historyWithPred);
                                    await _botService.BroadcastResultAsync(
                                        balance.ToString("N0") + " đ", 
                                        latest.IssueNumber, 
                                        latest.Number.ToString(), 
                                        latest.Size, 
                                        historyJson,
                                        prediction.Pred,
                                        prediction.Confidence,
                                        prediction.Reason);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[GameAPI] Poll Error: {ex.Message}"); }
                await Task.Delay(2000, ct); 
            }
        }

        public async Task<bool> PlaceBetAsync(int typeId, string issue, string selection, int betCount)
        {
            if (!IsLoggedIn) return false;
            
            for (int retry = 0; retry < 10; retry++) // Increased to 10 retries (up to 20 seconds)
            {
                try
                {
                    // Get fresh history EACH retry to find the actual latest settled issue
                    var freshHistory = await GetGameHistoryAsync(typeId, 5);
                    string currentIssue;
                    if (freshHistory.Count > 0)
                    {
                        currentIssue = (long.Parse(freshHistory[0].IssueNumber) + 1).ToString();
                    }
                    else
                    {
                        currentIssue = issue;
                    }
                    
                    // Skip if already bet on this issue
                    if (_bettedIssues.Contains(currentIssue))
                    {
                        Console.WriteLine($"[GameAPI] Already bet on {currentIssue}, waiting for new period...");
                        await Task.Delay(1000);
                        continue;
                    }
                    
                    // gameType: 2 for WinGo Big/Small betting
                    // selectType: 13 = Big, 14 = Small
                    int gameType = 2;
                    int selectType = selection == "Big" ? 13 : 14;
                    int amount = 1000;
                    string random = Guid.NewGuid().ToString("N").ToLower();
                    long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    var signParams = new SortedDictionary<string, object>
                    {
                        { "amount", amount },
                        { "betCount", betCount },
                        { "gameType", gameType },
                        { "issuenumber", currentIssue },
                        { "language", 2 },
                        { "random", random },
                        { "selectType", selectType },
                        { "typeId", typeId }
                    };
                    
                    string signature = SignPayload(signParams);
                    var payload = new 
                    { 
                        typeId = typeId,
                        issuenumber = currentIssue,
                        amount = amount,
                        betCount = betCount,
                        gameType = gameType,
                        selectType = selectType,
                        language = 2,
                        random = random,
                        signature = signature,
                        timestamp = timestamp
                    };

                    var json = await SendPostAsync($"{API_BASE}/GameBetting", payload);
                    if (string.IsNullOrEmpty(json)) return false;

                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("code", out var code) && code.GetInt32() == 0)
                    {
                        Console.WriteLine($"[GameAPI] Bet Placed: {selection} {betCount * 1000}đ on {currentIssue}");
                        _bettedIssues.Add(currentIssue);
                        // Cleanup old entries (keep only last 50)
                        if (_bettedIssues.Count > 50) _bettedIssues.Clear();
                        return true;
                    }
                    
                    // Check if period is settled - wait a moment and retry
                    if (root.TryGetProperty("msg", out var msg))
                    {
                        var msgStr = msg.GetString() ?? "";
                        
                        if (msgStr.Contains("settled"))
                        {
                            Console.WriteLine($"[GameAPI] Period {currentIssue} settled, waiting for new period...");
                            await Task.Delay(2000);
                            continue;
                        }
                        
                        if (msgStr.Contains("resubmit"))
                        {
                            Console.WriteLine($"[GameAPI] Already bet on {currentIssue}, treating as success");
                            _bettedIssues.Add(currentIssue);
                            return true; // Already bet on this period - treat as success
                        }
                    }
                    
                    Console.WriteLine($"[GameAPI] Bet Failed ({currentIssue}): {json}");
                    return false;
                }
                catch (Exception ex) { Console.WriteLine($"[GameAPI] Bet Error: {ex.Message}"); return false; }
            }
            return false;
        }

        private void SaveLoginInfo(string u, string p)
        {
            try { File.WriteAllText("login.json", JsonSerializer.Serialize(new LoginInfo { User = u, Pass = p })); }
            catch { }
        }

        public LoginInfo? GetSavedLogin()
        {
            try { if (File.Exists("login.json")) return JsonSerializer.Deserialize<LoginInfo>(File.ReadAllText("login.json")); }
            catch { }
            return null;
        }

        private string SignPayload(SortedDictionary<string, object> data)
        {
            var entries = new List<string>();
            var exclude = new HashSet<string> { "signature", "track", "xosoBettingData", "timestamp" };
            foreach (var kvp in data)
            {
                if (exclude.Contains(kvp.Key)) continue;
                var val = kvp.Value;
                if (val == null || (val is string s && string.IsNullOrEmpty(s))) continue;
                string valStr = val switch { string str => $"\"{str}\"", bool b => b.ToString().ToLower(), _ => val.ToString() ?? "null" };
                entries.Add($"\"{kvp.Key}\":{valStr}");
            }
            return CreateMD5("{" + string.Join(",", entries) + "}");
        }

        private string CreateMD5(string input)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);
                StringBuilder sb = new StringBuilder();
                foreach (var b in hashBytes) sb.Append(b.ToString("X2"));
                return sb.ToString();
            }
        }

        private async Task<string?> SendPostAsync(string url, object body)
        {
            try
            {
                var json = JsonSerializer.Serialize(body);
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("Origin", "https://387vn.com");
                request.Headers.TryAddWithoutValidation("Referer", "https://387vn.com/");
                request.Headers.TryAddWithoutValidation("Ar-Origin", "https://387vn.com");
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                if (!string.IsNullOrEmpty(_token)) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_token}");
                var resp = await _httpClient.SendAsync(request);
                return await resp.Content.ReadAsStringAsync();
            }
            catch { return null; }
        }
    }
}
