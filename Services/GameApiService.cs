using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using DropAI.Hubs;
using System.Globalization;

namespace DropAI.Services
{
    /// <summary>
    /// API-based service để thay thế BrowserService (dùng cho Telegram Bot)
    /// Không cần mở browser, gọi API trực tiếp
    /// </summary>
    public class GameApiService
    {
        private readonly HttpClient _httpClient;
        private readonly Microsoft.AspNetCore.SignalR.IHubContext<DropAI.Hubs.BrowserHub> _hubContext;
        private readonly DropAI.TelegramBot.TelegramBotService _botService;
        private string? _token;
        private bool _isPolling = false;
        private CancellationTokenSource? _pollCts;
        public int CurrentGameId { get; set; } = 30; // Default WinGo 1Min
        
        private const string API_BASE = "https://vn168api.com/api/webapi";
        
        public bool IsLoggedIn => !string.IsNullOrEmpty(_token);
        public string Status { get; private set; } = "Ready (API Mode)";

        // AUTO-BET STATE
        public bool IsAutoBetEnabled { get; set; } = true;
        public decimal BaseAmount { get; set; } = 1000;
        public List<int> MartingaleConfig { get; set; } = new List<int> { 2, 4, 8, 19, 40, 90 };
        public int MartingaleStep { get; set; } = 0;
        public int WinStreak { get; set; } = 0;
        private string? _lastBetIssue;
        private string? _lastBetType;
        private decimal _lastBetAmount;
        private string? _lastFinishedBetIssue;
        private decimal _lastFinishedBetAmount;
        private string? _lastProcessedResultIssue; // To ensure we only bet once per new result
        private AiPrediction? _currentPrediction;
        private decimal _lastBalance = 0;

        private System.Collections.Concurrent.ConcurrentDictionary<string, AiPrediction> _aiPredictions = new();

        // EXTERNAL SIGNAL MODE
        private readonly ExternalSignalService _externalSignalService;
        public bool UseExternalSignal { get; set; } = true; // Default to external signal

        public GameApiService(Microsoft.AspNetCore.SignalR.IHubContext<DropAI.Hubs.BrowserHub> hubContext, DropAI.TelegramBot.TelegramBotService botService, ExternalSignalService externalSignalService)
        {
            _hubContext = hubContext;
            _botService = botService;
            _externalSignalService = externalSignalService;
            _httpClient = new HttpClient();
            ConfigureHttpClient();

            // Auto-login on PC if session exists
            _ = Task.Run(async () =>
            {
                try {
                    await Task.Delay(3000); // Wait for everything to settle
                    var saved = GetSavedLogin();
                    if (saved != null)
                    {
                        Console.WriteLine($"[GameAPI] Found saved login for {saved.User}. Attempting auto-login...");
                        if (await LoginAsync(saved.User, saved.Pass))
                        {
                            Console.WriteLine("[GameAPI] Auto-login SUCCESS. Starting polling...");
                            StartPolling();
                        }
                        else {
                            Console.WriteLine("[GameAPI] Auto-login FAILED.");
                        }
                    }
                    else {
                        Console.WriteLine("[GameAPI] No saved login found in login.json");
                    }
                } catch (Exception ex) {
                    Console.WriteLine($"[GameAPI] Auto-login CRASHED: {ex.Message}");
                }
            });
        }

        private void ConfigureHttpClient()
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://387vn.com");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://387vn.com/");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("ar-origin", "https://387vn.com");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", 
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        public void StartPolling()
        {
            if (_isPolling) return;
            _isPolling = true;
            _pollCts = new CancellationTokenSource();
            _ = PollLoopAsync(_pollCts.Token);
            Status = "API Polling Started";
        }

        public bool TrySetMartingaleConfig(string configStr)
        {
            try
            {
                var parts = configStr.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var validList = new List<int>();
                foreach (var p in parts)
                {
                    if (int.TryParse(p.Trim(), out int val) && val > 0)
                    {
                        validList.Add(val);
                    }
                }

                if (validList.Count > 0)
                {
                    MartingaleConfig = validList;
                    return true;
                }
            }
            catch { }
            return false;
        }

        public void StopPolling()
        {
            _isPolling = false;
            _pollCts?.Cancel();
            Status = "API Polling Stopped";
        }

        private bool IsSafeBettingTime()
        {
            // WinGo 30s cycle: 0-30, 30-60
            // Safe window: second 1-24 and 31-54
            // Keep it simple: second%30 < 25
            int sec = DateTime.Now.Second % 30;
            return sec < 25 && sec >= 1;
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            Console.WriteLine("[GameAPI] Polling loop started.");
            DateTime lastBalanceCheck = DateTime.MinValue;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (IsLoggedIn)
                    {
                        var history = await GetGameHistoryAsync(CurrentGameId, 20); 
                        
                        // Only check balance every 10 seconds or if it's currently 0
                        // This prioritizes the history call for better betting timing
                        // Only check balance every 10 seconds or if it's currently 0
                        // This prioritizes the history call for better betting timing
                        if ((DateTime.Now - lastBalanceCheck).TotalSeconds > 10)
                        {
                            _lastBalance = await GetBalanceAsync();
                            lastBalanceCheck = DateTime.Now;
                        }

                        if (history != null && history.Count > 0)
                        {
                            var latest = history[0];
                            
                            // CRITICAL: Only process if we detected a NEW result
                            // This prevents duplicate messages and redundant AI/Betting logic
                            if (_lastProcessedResultIssue != latest.IssueNumber)
                            {
                                 Console.WriteLine($"[DEBUG] New Result Found! Current: {latest.IssueNumber}, Previous: {_lastProcessedResultIssue}");
                                 _lastProcessedResultIssue = latest.IssueNumber;

                                // 1. Run AI or get External Signal
                                string nextIssue = (long.Parse(latest.IssueNumber) + 1).ToString();
                                
                                 if (UseExternalSignal)
                                 {
                                     // STICKY WAIT: Try to get external signal for up to 24 seconds (WinGo 30s lock is at 25s)
                                     _currentPrediction = null;
                                     for (int i = 0; i < 24; i++)
                                     {
                                         _currentPrediction = _externalSignalService.GetLatestSignal(nextIssue);
                                         if (_currentPrediction != null) break;
                                         
                                         if (i == 0) Console.WriteLine($"⏳ [{DateTime.Now:HH:mm:ss}] Đang đợi tín hiệu cho kỳ {nextIssue} từ @tinhieu168 (tối đa 24s)...");
                                         await Task.Delay(1000, ct); 
                                     }

                                     if (_currentPrediction == null)
                                     {
                                         Console.WriteLine($"❌ Không nhận được tín hiệu từ bot cho kỳ {nextIssue}. Bỏ qua cược.");
                                     }
                                 }
                                 else
                                 {
                                     // Even if AI mode is set, we use external logic if possible, or skip
                                     _currentPrediction = _externalSignalService.GetLatestSignal(nextIssue);
                                 }

                                 // STORE PREDICTION FOR HISTORY MAPPING
                                 _aiPredictions[nextIssue] = _currentPrediction ?? new AiPrediction { Pred = "-" };

                                 // 2. Evaluate AutoBet
                                 await EvaluateAutoBetInternal(history);

                                 // 3. Broadcast Result
                                 string historyJson = System.Text.Json.JsonSerializer.Serialize(history.Take(10).Select(item => {
                                     var pred = _aiPredictions.ContainsKey(item.IssueNumber) ? _aiPredictions[item.IssueNumber] : _externalSignalService.GetSignal(item.IssueNumber);
                                     return new
                                     {
                                         issue = item.IssueNumber,
                                         number = item.Number.ToString(),
                                         size = item.Size,
                                         parity = item.Parity,
                                         aiGuess = pred?.Pred ?? "-",
                                         aiResult = (pred != null && !string.IsNullOrEmpty(pred.Pred) && item.Size != "-") ? (pred.Pred == item.Size ? "✅" : "❌") : "-"
                                     };
                                 }));

                                 // For the main message:
                                 // We show the signal for the round that just finished (latest) 
                                 // AND the signal for the upcoming round (currentPred)
                                 string betWithFooter = "";
                                 
                                 if (_currentPrediction != null && !string.IsNullOrEmpty(_currentPrediction.RawSignalText))
                                 {
                                     betWithFooter += $"{_currentPrediction.RawSignalText}";
                                     
                                     // ADDED: Show actual bet amount for the upcoming round
                                     if (IsAutoBetEnabled && _lastBetIssue == nextIssue && _lastBetAmount > 0)
                                     {
                                         betWithFooter += $"\n😍 *Số tiền:* {_lastBetAmount.ToString("N0")} đ";
                                     }
                                 }
                                 else {
                                     betWithFooter += "⏳ Đang đợi tín hiệu tiếp theo...";
                                 }

                                 await NotifyBot(_lastBalance, latest, betWithFooter, historyJson);
                             }
                         }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GameAPI] Polling Error: {ex.Message}");
                }
                await Task.Delay(1000, ct); // Faster polling (1s) to rival WebSocket speed
            }
            Console.WriteLine("[GameAPI] Polling loop stopped.");
        }

        /// <summary>
        /// BƯỚC 1: Đăng nhập và lấy token
        /// Payload format đã được reverse engineer từ 387vn.com
        /// </summary>
        public class LoginInfo
        {
            public string User { get; set; } = "";
            public string Pass { get; set; } = "";
        }

        private void SaveLoginInfo(string u, string p)
        {
            try
            {
                var info = new LoginInfo { User = u, Pass = p };
                File.WriteAllText("login.json", JsonSerializer.Serialize(info));
                Console.WriteLine($"[GameAPI] Login Info Saved for {u}");
            }
            catch { }
        }

        public LoginInfo? GetSavedLogin()
        {
            try
            {
                if (File.Exists("login.json"))
                {
                    return JsonSerializer.Deserialize<LoginInfo>(File.ReadAllText("login.json"));
                }
            }
            catch { }
            return null;
        }

        public async Task<bool> LoginAsync(string username, string password)
        {
            try
            {
                // VỚI LOGIN: Dùng PASSWORD GỐC (RAW), KHÔNG HASH
                string passwordVal = password; 

                // Generate required fields
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string random = Guid.NewGuid().ToString("N").ToLower(); 
                string deviceId = "fd52ea6688df74da7e2bafaf4d6ecd48"; 

                // 1. TẠO CHỮ KÝ: DÙNG SortedDictionary (Alphabetical)
                // SignPayload sẽ tự động loại bỏ các field loại trừ (timestamp, packId rỗng, signature)
                var signParams = new SortedDictionary<string, object>
                {
                    { "username", username },
                    { "pwd", passwordVal },
                    { "phonetype", 0 },
                    { "logintype", "mobile" },
                    { "packId", "" },
                    { "deviceId", deviceId },
                    { "language", 2 },
                    { "random", random },
                    { "timestamp", timestamp }
                };

                string signature = SignPayload(signParams); 

                var jsonBody = new 
                {
                    username = username,
                    pwd = passwordVal,
                    phonetype = 0,
                    logintype = "mobile",
                    packId = "",
                    deviceId = deviceId,
                    language = 2,
                    random = random,
                    signature = signature,
                    timestamp = timestamp
                };
                
                var resultJson = await SendPostAsync($"{API_BASE}/Login", jsonBody);
                if (string.IsNullOrEmpty(resultJson)) return false;

                using var doc = JsonDocument.Parse(resultJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("code", out var code) && code.GetInt32() == 0)
                {
                    var data = root.GetProperty("data");
                    _token = data.GetProperty("token").GetString();
                    
                    // NORMALIZE DATA FOR FRONTEND (site.js expects lowercase keys)
                    var normalizedData = new {
                        userName = data.TryGetProperty("UserName", out var un) ? un.GetString() : "",
                        userId = data.TryGetProperty("UserId", out var uid) ? uid.GetString() : "",
                        amount = data.TryGetProperty("Amount", out var amt) ? amt.GetString() : "0",
                        token = _token
                    };
                    var normalizedJson = JsonSerializer.Serialize(new { code = 0, msg = "Succeed", data = normalizedData });

                    Console.WriteLine($"[GameAPI] Login Success. User: {normalizedData.userName}");
                    await _hubContext.Clients.All.SendAsync("ReceiveLoginSuccess", normalizedJson);
                    
                    StartPolling(); 
                    
                    // SAVE LOGIN INFO
                    SaveLoginInfo(username, passwordVal);
                    
                    return true;
                }
                else
                {
                    Console.WriteLine($"[GameAPI] Login Failed: {resultJson}");
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameAPI] Login Error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// BƯỚC 2: Lấy lịch sử game
        /// </summary>
        public async Task<List<GameHistoryItem>> GetGameHistoryAsync(int gameType = 30, int pageSize = 100)
        {
            if (!IsLoggedIn) throw new InvalidOperationException("Not logged in");

            try
            {
                string random = Guid.NewGuid().ToString("N").ToLower();
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var signParams = new SortedDictionary<string, object>
                {
                    { "typeId", gameType },
                    { "pageSize", pageSize },
                    { "pageNo", 1 },
                    { "language", 2 },
                    { "random", random }
                };

                string signature = SignPayload(signParams);

                var payload = new
                {
                    typeId = gameType,
                    pageSize = pageSize,
                    pageNo = 1,
                    language = 2,
                    random = random,
                    signature = signature,
                    timestamp = timestamp
                };

                var json = await SendPostAsync($"{API_BASE}/GetNoaverageEmerdList", payload);
                if (string.IsNullOrEmpty(json)) return new List<GameHistoryItem>();

                // Broadcast to UI to keep it alive
                await _hubContext.Clients.All.SendAsync("ReceiveGameHistory", json);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("code", out var code) && code.GetInt32() == 0)
                {
                    var dataProp = root.GetProperty("data");
                    if (dataProp.TryGetProperty("list", out var list))
                    {
                        var history = new List<GameHistoryItem>();
                        foreach (var item in list.EnumerateArray())
                        {
                            var issueNumber = item.GetProperty("issueNumber").GetString()!;
                            var numberStr = item.GetProperty("number").GetString()!;
                            int number = int.Parse(numberStr);
                            
                            history.Add(new GameHistoryItem
                            {
                                IssueNumber = issueNumber,
                                Number = number,
                                Size = number >= 5 ? "Big" : "Small",
                                Parity = number % 2 == 0 ? "Double" : "Single"
                            });
                        }
                        return history;
                    }
                }
                else
                {
                    Console.WriteLine($"[GameAPI] GetHistory Failed: {json}");
                }

                return new List<GameHistoryItem>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameAPI] GetHistory Error: {ex.Message}");
                return new List<GameHistoryItem>();
            }
        }

        /// <summary>
        /// BƯỚC 3: Đặt cược (Copy từ BrowserService.PlaceBetAsync)
        /// </summary>
        public async Task<BetResult> PlaceBetAsync(string type, int amount, int gameType = 30, string? manualNextIssue = null)
        {
            if (!IsLoggedIn) throw new InvalidOperationException("Not logged in");

            try
            {
                string nextIssue;

                if (!string.IsNullOrEmpty(manualNextIssue))
                {
                    nextIssue = manualNextIssue;
                }
                else
                {
                    // 1. Lấy issue number hiện tại (Fallback mode)
                    var history = await GetGameHistoryAsync(gameType, 1);
                    if (history.Count == 0) 
                        return new BetResult { Success = false, ErrorMessage = "Cannot get current issue" };

                    var lastIssue = history[0].IssueNumber;
                    nextIssue = (long.Parse(lastIssue) + 1).ToString();
                }

                // 2. Chuẩn bị payload
                int selectType = type.Equals("Big", StringComparison.OrdinalIgnoreCase) ? 13 : 14;
                int baseAmount = 1000;
                int betCount = amount / baseAmount;
                if (betCount < 1) betCount = 1;

                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string random = Guid.NewGuid().ToString("N");

                // 3. Tạo signature (QUAN TRỌNG: phải sorted alphabetically)
                var signParams = new SortedDictionary<string, object>
                {
                    { "amount", baseAmount },
                    { "betCount", betCount },
                    { "gameType", 2 },
                    { "issuenumber", nextIssue },
                    { "language", 2 },
                    { "random", random },
                    { "selectType", selectType },
                    { "typeId", gameType }
                };

                string signature = SignPayload(signParams);

                // 4. Build payload (manual order như user request)
                var payload = new
                {
                    typeId = gameType,
                    issuenumber = nextIssue,
                    amount = baseAmount,
                    betCount = betCount,
                    gameType = 2,
                    selectType = selectType,
                    language = 2,
                    random = random,
                    signature = signature,
                    timestamp = timestamp
                };

                // 5. Send request
                Console.WriteLine($"[GameAPI] Betting {type} {amount} on issue {nextIssue}");
                var respString = await SendPostAsync($"{API_BASE}/GameBetting", payload);
                if (string.IsNullOrEmpty(respString)) return new BetResult { Success = false, ErrorMessage = "Request failed" };

                Console.WriteLine($"[GameAPI] Bet Response: {respString}");

                // 6. Parse result
                using var doc = JsonDocument.Parse(respString);
                var code = doc.RootElement.GetProperty("code").GetInt32();

                if (code == 0)
                {
                    return new BetResult
                    {
                        Success = true,
                        IssueNumber = nextIssue,
                        Type = type,
                        Amount = amount,
                        MsgCode = 0,
                        Message = "Success"
                    };
                }
                else
                {
                    var msg = doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : "Unknown Error";
                    return new BetResult { 
                        Success = false, 
                        ErrorMessage = msg,
                        MsgCode = code,
                        Message = msg
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameAPI] PlaceBet Error: {ex.Message}");
                return new BetResult { Success = false, ErrorMessage = ex.Message, MsgCode = -1, Message = ex.Message };
            }
        }

        /// <summary>
        /// Lấy balance hiện tại
        /// </summary>
        public async Task<decimal> GetBalanceAsync()
        {
            if (!IsLoggedIn) throw new InvalidOperationException("Not logged in");

            try
            {
                string random = Guid.NewGuid().ToString("N").ToLower();
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var signParams = new SortedDictionary<string, object>
                {
                    { "language", 2 },
                    { "random", random }
                };

                string signature = SignPayload(signParams);

                var payload = new 
                { 
                    language = 2,
                    random = random,
                    signature = signature,
                    timestamp = timestamp
                };

                var json = await SendPostAsync($"{API_BASE}/GetBalance", payload);
                if (string.IsNullOrEmpty(json)) return 0;
                
                // Broadcast to UI
                await _hubContext.Clients.All.SendAsync("ReceiveBalance", json);
                
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("code", out var code) && code.GetInt32() == 0)
                {
                    return root.GetProperty("data").GetProperty("amount").GetDecimal();
                }
                else
                {
                    Console.WriteLine($"[GameAPI] GetBalance Failed: {json}");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameAPI] GetBalance Error: {ex.Message}");
                return 0;
            }
        }

        private async Task BroadcastStatus()
        {
            await _hubContext.Clients.All.SendAsync("ReceiveStatus", Status);
        }

        public void StorePrediction(string issue, AiPrediction prediction)
        {
            _aiPredictions[issue] = prediction;
            Console.WriteLine($"[GameAPI] Stored Prediction: {issue} -> {prediction.Pred} ({prediction.Reason})");
        }

        // === HELPER METHODS ===

        private string SignPayload(SortedDictionary<string, object> data)
        {
            // Logic từ interceptor của 387vn.com:
            // 1. Sắp xếp Alphabetical (SortedDictionary đã làm)
            // 2. Loại bỏ các key: signature, track, xosoBettingData, và ĐẶC BIỆT LÀ timestamp
            // 3. Loại bỏ null hoặc chuỗi rỗng ""
            
            var entries = new List<string>();
            var exclude = new HashSet<string> { "signature", "track", "xosoBettingData", "timestamp" };

            foreach (var kvp in data)
            {
                if (exclude.Contains(kvp.Key)) continue;

                var val = kvp.Value;
                if (val == null || (val is string s && string.IsNullOrEmpty(s))) continue;

                string valStr = val switch
                {
                    string str => $"\"{str}\"",
                    bool b => b.ToString().ToLower(),
                    int i => i.ToString(),
                    long l => l.ToString(),
                    _ => val.ToString() ?? "null"
                };
                
                entries.Add($"\"{kvp.Key}\":{valStr}");
            }
            
            var jsonString = "{" + string.Join(",", entries) + "}";
            // Console.WriteLine($"[GameAPI] Signing String: {jsonString}");
            
            return CreateMD5(jsonString);
        }

        private async Task EvaluateAutoBetInternal(List<GameHistoryItem> history)
        {
            if (!IsAutoBetEnabled || history.Count == 0) return;

            var latest = history[0];
            
            // Check Previous Result
            if (_lastBetIssue == latest.IssueNumber)
            {
                _lastFinishedBetIssue = _lastBetIssue;
                _lastFinishedBetAmount = _lastBetAmount;

                bool isWin = _lastBetType == latest.Size;
                if (isWin)
                {
                    MartingaleStep = 0;
                    WinStreak++;
                }
                else
                {
                    MartingaleStep++;
                    if (MartingaleStep >= MartingaleConfig.Count)
                    {
                        MartingaleStep = 0;
                        WinStreak = 0;
                    }
                }
                _lastBetIssue = null; // Clear
            }

            if (_currentPrediction == null) return;

            // Calculate Next Issue
            string nextIssue;
            try { nextIssue = (long.Parse(latest.IssueNumber) + 1).ToString(); } catch { nextIssue = "Unknown"; }

            if (_lastBetIssue == nextIssue) return;

            // Execute Bet
            int multiplier = 1;
            if (MartingaleStep < MartingaleConfig.Count)
            {
                multiplier = MartingaleConfig[MartingaleStep]; // Step 0 uses multipliers[0] (e.g. 2)
            }
            else
            {
                 // Fallback if step exceeds config (should reset, but safe fallback)
                 multiplier = MartingaleConfig.Last();
            }
            
            decimal betAmount = BaseAmount * multiplier;
            
            _lastBetIssue = nextIssue;
            _lastBetType = _currentPrediction.Pred;
            _lastBetAmount = betAmount;

            // BETTING RETRY LOGIC (Spam until success or give up)
            int retries = 0;
            const int MAX_RETRIES = 5;
            BetResult finalResult = null;

            while (retries < MAX_RETRIES)
            {
                var result = await PlaceBetAsync(_currentPrediction.Pred, (int)betAmount, CurrentGameId, nextIssue);
                if (result.Success)
                {
                    finalResult = result;
                    break; 
                }
                
                // If "Balance not enough", stop spamming immediately
                if (result.MsgCode == 404 || (result.Message?.Contains("settled", StringComparison.OrdinalIgnoreCase) == true))
                {
                     // SMART RE-SYNC: If 404 Settled, we are out of sync.
                     // Don't guess. Ask the server for the latest status.
                     Console.WriteLine($"[AutoBet] Period Settled (404). Re-Syncing with Server...");
                     
                     try 
                     {
                         // Wait 1.5s to allow server state to settle
                         await Task.Delay(1500); 
                         
                         var freshHistory = await GetGameHistoryAsync(CurrentGameId, 1);
                         if (freshHistory.Count > 0)
                         {
                             var realLatest = freshHistory[0].IssueNumber;
                             // Recalculate next issue based on FRESH data
                             nextIssue = (long.Parse(realLatest) + 1).ToString();
                         }
                         else
                         {
                             // Fallback if history fetch fails: try blind increment
                             nextIssue = (long.Parse(nextIssue) + 1).ToString();
                         }

                         // CRITICAL: Update STATE immediately
                         _lastBetIssue = nextIssue;
                         
                         Console.WriteLine($"[AutoBet] Retrying on FRESH Issue: {nextIssue}...");
                         
                         // Don't count this as a standard retry, we want to force this through.
                         // But to prevent infinite loops, we still use the loop counter.
                         continue;
                     }
                     catch { /* Ignore parse error */ }
                }
                else
                {
                    // If msgCode is 9 (Do not resubmit), it means we ALREADY SUCCESS
                    if (result.MsgCode == 9)
                    {
                         Console.WriteLine($"[AutoBet] Bet Confirmed (Code 9: Already Submitted). Marking Success.");
                         finalResult = result;
                         finalResult.Success = true; // Force success status
                         break;
                    }

                    Console.WriteLine($"[AutoBet] Bet Failed: {result.ErrorMessage}. Retrying ({retries+1}/{MAX_RETRIES})...");
                }

                retries++;
                await Task.Delay(300); // 300ms wait
            }

            if (finalResult == null || !finalResult.Success)
            {
                Console.WriteLine($"[AutoBet] FAILED after {MAX_RETRIES} attempts.");
                _lastBetIssue = null; // Reset
            }
        }

        private async Task NotifyBot(decimal balance, GameHistoryItem latest, string betWithFooter, string historyJson)
        {
            try
            {
                if (_botService != null)
                {
                    string balanceStr = balance.ToString("N0") + " đ";

                    Console.WriteLine("------------------------------------------");
                    Console.WriteLine($"🚀 BROADCASTING RESULT TO TELEGRAM");
                    Console.WriteLine($"💰 Tiền: {balanceStr}");
                    Console.WriteLine($"📅 Phiên: {latest.IssueNumber}");
                    Console.WriteLine($"🔢 Số: {latest.Number} ({latest.Size})");
                    Console.WriteLine($"✨ Tín hiệu: \n{betWithFooter}");
                    Console.WriteLine("------------------------------------------");

                    // Call updated Broadcast (aiGuess and aiResult are redundant now as they are in betWithFooter or table)
                    await _botService.BroadcastResultAsync(balanceStr, latest.IssueNumber, latest.Number.ToString(), latest.Size, "", "", betWithFooter, historyJson);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameAPI] NotifyBot Error: {ex.Message}");
            }
        }


        private async Task<string> SendPostAsync(string url, object body)
        {
            try
            {
                var json = JsonSerializer.Serialize(body);
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                // EXPLICIT HEADERS (Mirroring Browser)
                request.Headers.TryAddWithoutValidation("origin", "https://387vn.com");
                request.Headers.TryAddWithoutValidation("referer", "https://387vn.com/");
                request.Headers.TryAddWithoutValidation("Ar-Origin", "https://387vn.com");
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                
                if (!string.IsNullOrEmpty(_token))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_token}");
                }

                var response = await _httpClient.SendAsync(request);
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameAPI] Request Error: {ex.Message}");
                return string.Empty;
            }
        }

        private string CreateMD5(string input)
        {
            using var md5 = MD5.Create();
            byte[] inputBytes = Encoding.UTF8.GetBytes(input);
            byte[] hashBytes = md5.ComputeHash(inputBytes);
            var sb = new StringBuilder();
            foreach (var b in hashBytes) sb.Append(b.ToString("X2")); // X2 for Uppercase to match target
            return sb.ToString();
        }
    }

    // === DATA MODELS ===

    public class LoginResult
    {
        public bool Success { get; set; }
        public string? Token { get; set; }
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public decimal Balance { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class GameHistoryItem
    {
        public string IssueNumber { get; set; } = "";
        public int Number { get; set; }
        public string Size { get; set; } = "";
        public string Parity { get; set; } = "";
    }

    public class BetResult
    {
        public bool Success { get; set; }
        public string? IssueNumber { get; set; }
        public string? Type { get; set; }
        public int Amount { get; set; }
        public string? ErrorMessage { get; set; }
        public int MsgCode { get; set; }
        public string? Message { get; set; }
    }
}
