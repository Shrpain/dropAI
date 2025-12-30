using Microsoft.AspNetCore.SignalR;
using Microsoft.Playwright;
using DropAI.Hubs;
using System;
using System.Threading.Tasks;

namespace DropAI.Services
{
    public class BrowserService
    {
        private IPlaywright? _playwright;
        private IBrowser? _browser;
        private IBrowserContext? _context;
        private IPage? _page;
        private readonly IHubContext<BrowserHub> _hubContext;
        private string? _cachedToken;
        private string? _currentIssueNumber;
        private readonly System.Net.Http.HttpClient _httpClient = new System.Net.Http.HttpClient();

        public bool IsRunning => _browser != null;
        public string Status { get; private set; } = "Ready";

        public BrowserService(IHubContext<BrowserHub> hubContext)
        {
            _hubContext = hubContext;
        }

        public async Task StartBrowserAsync(string url, string? username = null, string? password = null)
        {
            if (IsRunning)
            {
                await StopBrowserAsync();
            }

            Status = "Launching Browser...";
            await BroadcastStatus();

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                Args = new[] { "--start-maximized", "--mute-audio" }, // Mute audio by default
                Channel = "chrome" // Try to use installed Chrome if available for better compatibility
            });

            _context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = ViewportSize.NoViewport // Disable viewport lock
            });
            _page = await _context.NewPageAsync();
            
            // --- TEST SIGNATURE LOGIC ---
            TestSignature(); 

            // Request Interception
            // Request Interception
            _page.Request += async (sender, request) =>
            {
                try
                {
                    // 1. Capture Authorization Token
                    var headers = request.Headers;
                    if (headers.TryGetValue("authorization", out var token) && !string.IsNullOrEmpty(token))
                    {
                        if (_cachedToken != token)
                        {
                            _cachedToken = token;
                            Console.WriteLine($"[BrowserService] Token Captured: {_cachedToken.Substring(0, 15)}...");
                        }
                    }

                    // 2. Generic Spy & Logging
                    string postData = request.PostData ?? "";
                    if (request.Url.Contains("GameBetting", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[API SPY] BETTING DETECTED: {request.Url}");
                        Console.WriteLine($"[API SPY] Payload: {postData}");
                        await _hubContext.Clients.All.SendAsync("ReceiveNetworkEvent", new 
                        {
                             Type = "BetSpy",
                             Url = request.Url,
                             PostData = postData,
                             Headers = request.Headers,
                             Timestamp = DateTime.Now.ToString("HH:mm:ss")
                        });
                    }
                    else
                    {
                        // Reduce noise, only log distinct events if needed
                        // Console.WriteLine($"[BrowserService] Request: {request.Method} {request.Url}");
                    }
                }
                catch {}
            };

            // Response Interception
            _page.Response += async (sender, response) =>
            {
                var url = response.Url;
                var status = response.Status;

                // 1. Generic Network Event (For Monitor)
                var netData = new
                {
                    Type = "Response",
                    Method = "RESPONSE", 
                    Url = url,
                    Status = status,
                    Timestamp = DateTime.Now.ToString("HH:mm:ss")
                };
                await _hubContext.Clients.All.SendAsync("ReceiveNetworkEvent", netData);

                // 2. Specific Interceptions (Only 200 OK)
                if (status == 200)
                {
                    try 
                    {
                        // A. GetUserInfo -> Login Success -> Navigate to WinGo
                        if (url.Contains("/api/webapi/GetUserInfo", StringComparison.OrdinalIgnoreCase))
                        {
                            var bodyBytes = await response.BodyAsync();
                            var bodyString = System.Text.Encoding.UTF8.GetString(bodyBytes);
                            await _hubContext.Clients.All.SendAsync("ReceiveLoginSuccess", bodyString);

                            // Trigger Auto Navigation
                            _ = Task.Run(async () => {
                                try {
                                    await Task.Delay(2000); 
                                    if (_page != null) await _page.GotoAsync("https://387vn.com/#/home/AllLotteryGames/WinGo?id=1");
                                } catch {}
                            });
                        }
                        
                        // B. GetAllGameList
                        if (url.Contains("/api/webapi/GetAllGameList", StringComparison.OrdinalIgnoreCase))
                        {
                             var bodyBytes = await response.BodyAsync();
                             var bodyString = System.Text.Encoding.UTF8.GetString(bodyBytes);
                             await _hubContext.Clients.All.SendAsync("ReceiveGameList", bodyString);
                        }

                        // C. GetTypeList
                        if (url.Contains("/api/webapi/GetTypeList", StringComparison.OrdinalIgnoreCase))
                        {
                             var bodyBytes = await response.BodyAsync();
                             var bodyString = System.Text.Encoding.UTF8.GetString(bodyBytes);
                             await _hubContext.Clients.All.SendAsync("ReceiveGameTypes", bodyString);
                        }

                        // D. GetNoaverageEmerdList OR GetHistoryIssuePage -> Game History
                        if (url.Contains("/api/webapi/GetNoaverageEmerdList", StringComparison.OrdinalIgnoreCase) || 
                            url.Contains("/api/webapi/GetHistoryIssuePage", StringComparison.OrdinalIgnoreCase))
                        {
                             var bodyBytes = await response.BodyAsync();
                             var bodyString = System.Text.Encoding.UTF8.GetString(bodyBytes);
                             
                             // 1. Parse Issue Number for API Betting
                             try {
                                 using (var doc = System.Text.Json.JsonDocument.Parse(bodyString))
                                 {
                                     if(doc.RootElement.TryGetProperty("data", out var dataEl) && 
                                        dataEl.TryGetProperty("list", out var listEl) && 
                                        listEl.GetArrayLength() > 0)
                                     {
                                         // Last completed issue
                                         var lastIssue = listEl[0].GetProperty("issueNumber").GetString(); 
                                         // Current betting issue is usually Next
                                         if(long.TryParse(lastIssue, out var issueVal))
                                         {
                                             _currentIssueNumber = (issueVal + 1).ToString();
                                             Console.WriteLine($"[BrowserService] Next Issue: {_currentIssueNumber}");
                                         }
                                     }
                                 }
                             } catch {}

                             // 2. Broadcast raw JSON
                             await _hubContext.Clients.All.SendAsync("ReceiveGameHistory", bodyString);
                        }

                        // E. GetBalance -> Update UI
                        if (url.Contains("/api/webapi/GetBalance", StringComparison.OrdinalIgnoreCase))
                        {
                             var bodyBytes = await response.BodyAsync();
                             var bodyString = System.Text.Encoding.UTF8.GetString(bodyBytes);
                             await _hubContext.Clients.All.SendAsync("ReceiveBalance", bodyString);
                        }

                        // F. SPY: Scan JS files
                        if (url.EndsWith(".js") || url.Contains(".js?")) 
                        {
                            if(url.Contains("app") || url.Contains("chunk")) 
                            {
                                 try {
                                    var bodyBytes = await response.BodyAsync();
                                    var content = System.Text.Encoding.UTF8.GetString(bodyBytes);
                                    if (content.Contains("signature") && content.Contains("md5"))
                                    {
                                        var idx = content.IndexOf("signature");
                                        var snippet = content.Substring(Math.Max(0, idx - 100), Math.Min(content.Length - idx, 300));
                                        await _hubContext.Clients.All.SendAsync("ReceiveNetworkEvent", new { Type = "CodeSpy", Url = url, Snippet = snippet });
                                    }
                                 } catch {}
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BrowserService] Error reading response body: {ex.Message}");
                    }
                }
            };
            Status = "Navigating...";
            await BroadcastStatus();

            try 
            {
                await _page.GotoAsync(url);
                
                if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
                {
                    Status = "Auto Login...";
                    await BroadcastStatus();
                    await PerformLogin(username, password);
                }

                Status = "Running";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BrowserService] Error: {ex.Message}");
                Status = "Error: " + ex.Message;
            }
            await BroadcastStatus();
        }

        public async Task NavigateAsync(string url)
        {
            if (_page != null)
            {
                Status = "Navigating to " + url;
                await BroadcastStatus();
                try
                {
                    await _page.GotoAsync(url);
                    Status = "Running";
                }
                catch (Exception ex)
                {
                    Status = "Error: " + ex.Message;
                }
                await BroadcastStatus();
            }
        }

        public async Task ClickAsync(string selector)
        {
             if (_page != null)
            {
                Status = "Clicking " + selector;
                await BroadcastStatus();
                try
                {
                    if (selector.StartsWith("/") || selector.StartsWith("("))
                    {
                        selector = "xpath=" + selector;
                    }
                    await _page.ClickAsync(selector);
                    Status = "Clicked";
                }
                catch (Exception ex)
                {
                    Status = "Click Error: " + ex.Message;
                }
                await BroadcastStatus();
            }
        }

        private async Task PerformLogin(string username, string password)
        {
            if (_page == null) return;

            try 
            {
                // 1. Fill Username/Phone
                var userSelector = "input[type='text'], input[type='tel'], input[placeholder*='khoản'], input[placeholder*='Phone']";
                
                try {
                    await _page.WaitForSelectorAsync(userSelector, new PageWaitForSelectorOptions { Timeout = 5000 });
                } catch {
                     Console.WriteLine("Timeout waiting for user input selector");
                }

                if (await _page.QuerySelectorAsync(userSelector) != null)
                {
                    await _page.FillAsync(userSelector, username);
                }
                else
                {
                     Console.WriteLine("Could not find username input");
                }

                // 2. Fill Password
                var passSelector = "input[type='password']";
                if (await _page.QuerySelectorAsync(passSelector) != null)
                {
                   await _page.FillAsync(passSelector, password);
                }

                // 3. Click Login Button
                var loginBtnSelector = "button:has-text('Đăng nhập'), button:has-text('Login'), div[role='button']:has-text('Đăng nhập')";
                 if (await _page.QuerySelectorAsync(loginBtnSelector) != null)
                {
                    await _page.ClickAsync(loginBtnSelector);
                }
                 else
                 {
                     await _page.Keyboard.PressAsync("Enter");
                 }
                 
                 await _page.WaitForTimeoutAsync(2000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoLogin] Failed: {ex.Message}");
            }
        }

        public async Task PlaceBetAsync(string type, int amount)
        {
            try
            {
                // 1. Validate Token
                if (string.IsNullOrEmpty(_cachedToken))
                {
                    // Try to fetch from localStorage if missing
                    try {
                        var localToken = await _page.EvaluateAsync<string>("localStorage.getItem('ar_token')");
                        if (!string.IsNullOrEmpty(localToken)) _cachedToken = "Bearer " + localToken;
                    } catch {}

                    if (string.IsNullOrEmpty(_cachedToken))
                    {
                         Status = "Error: No Token. Please Login.";
                         await BroadcastStatus();
                         return;
                    }
                }

                Status = $"API BET: {type} {amount}...";
                await BroadcastStatus();

                // 2. Prepare Payload
                // Mapping: Big=1, Small=2 (WinGo 1Min)
                // User CURL had selectType: 14. 
                // Let's assume standard 1/2 for Big/Small first. 
                // If 14 is "Small", then maybe 13 is "Big"?
                // Let's TRY 1 and 2. 
                // Mapping: Big/Small
                // 1/2 Failed. User seen 14. 
                // Hypoth: Big=13, Small=14. Or 11/12?
                // Let's try 13 for Big, 14 for Small.
                int selectType = type.Equals("Big", StringComparison.OrdinalIgnoreCase) ? 13 : 14;
                
                // Get TypeID from URL (e.g. ...?id=1)
                // MAPPING FIX: URL ID (1,2,3,4) != API TypeID (30,31,32,33)
                // Log shows WinGo 1Min (ID 1) uses API TypeID 30.
                int urlId = 1; 
                try {
                    var uri = new Uri(_page.Url);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    if(int.TryParse(query["id"], out var idVal)) urlId = idVal;
                    else if (uri.Fragment.Contains("id=")) {
                        // Handle hash based routing
                        var frag = uri.Fragment.Split('?');
                        if(frag.Length > 1) {
                            var q2 = System.Web.HttpUtility.ParseQueryString(frag[1]);
                            if(int.TryParse(q2["id"], out var idVal2)) urlId = idVal2;
                        }
                    }
                } catch {}

                // Map URL ID to API TypeID
                int typeId = 30; // Default WinGo 1Min
                switch(urlId)
                {
                    case 1: typeId = 30; break; // 1 Min
                    case 2: typeId = 31; break; // 3 Min
                    case 3: typeId = 32; break; // 5 Min
                    case 4: typeId = 33; break; // 10 Min
                    default: typeId = 30; break;
                }

                Console.WriteLine($"[AutoBet] URL ID: {urlId} -> API TypeID: {typeId}");

                // Get IssueNumber
                string issueNumber = _currentIssueNumber;

                // Fallback to UI scraping if API spy hasn't caught it yet
                if (string.IsNullOrEmpty(issueNumber))
                {
                     try 
                     {
                         // Try to find ANY element that looks like an issue number (10+ digits)
                         // Wait only 2s max
                         var issueEl = _page.Locator("div:text-matches('^\\d{10,}$')").First; // Regex for digits
                         // Or try specific class if known but with generic fallback
                         if(await _page.Locator(".FDTL__C-l2 > div:first-child").CountAsync() > 0)
                         {
                             issueNumber = await _page.Locator(".FDTL__C-l2 > div:first-child").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 1000 });
                         }
                         else
                         {
                             // Last resort: simple text search
                             // Or just FAIL and let next cycle try
                         }
                     } 
                     catch(Exception ex) { Console.WriteLine($"[AutoBet] UI Scrape Issue Fail: {ex.Message}"); }
                }

                if (!string.IsNullOrEmpty(issueNumber)) issueNumber = issueNumber.Trim();
                
                if (string.IsNullOrEmpty(issueNumber))
                {
                    Status = "Error: No Issue Number (Wait for next game)";
                    await BroadcastStatus();
                    return;
                }

                // 3. Prepare Payload
                // Log: Manual bet uses Integers. Reverting String change.
                // "Betting amount error" likely caused by Issue/GameID mismatch previously.
                // 3. CRITICAL FIX - ROOT CAUSE ANALYSIS
                // Step 1277 SigTest SUCCEEDED with ALPHABETICAL order: {"amount":...,"betCount":...}
                // ALL "Manual Order" signatures FAILED with "Wrong signature"
                // CONCLUSION: Server ALWAYS sorts keys alphabetically before hashing
                // USER'S "Manual Order" request applies to PAYLOAD FORMAT only, NOT signature logic
                
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string rnd = GenerateRandomString(32);

                // CRITICAL FIX: API uses betCount, not amount
                // amount = fixed base unit (1000)
                // betCount = multiplier (requested_amount / 1000)
                int baseAmount = 1000;
                int betCount = amount / baseAmount; // e.g., 2000 / 1000 = 2
                if (betCount < 1) betCount = 1; // Minimum 1

                Console.WriteLine($"[AutoBet] Requested: {amount} -> Amount: {baseAmount}, BetCount: {betCount}");

                // Create SORTED dictionary for CORRECT signature
                var signParams = new System.Collections.Generic.SortedDictionary<string, object>
                {
                    { "typeId", typeId },
                    { "issuenumber", issueNumber },
                    { "amount", baseAmount },      // Fixed 1000
                    { "betCount", betCount },       // Calculated multiplier
                    { "gameType", 2 },
                    { "selectType", selectType },
                    { "language", 2 },
                    { "random", rnd }
                };

                // Generate CORRECT signature (Alphabetical / Sorted)
                string sig = SignPayload(signParams);
                
                // Log for debugging
                Console.WriteLine($"[SigDebug] Signing with SortedDictionary (Alphabetical)");

                // Build Final Payload in Manual Order (User's preferred format)
                var entries = new System.Collections.Generic.List<string>();
                entries.Add($"\"typeId\":{typeId}");
                entries.Add($"\"issuenumber\":\"{issueNumber}\"");
                entries.Add($"\"amount\":{baseAmount}");
                entries.Add($"\"betCount\":{betCount}");
                entries.Add($"\"gameType\":2");
                entries.Add($"\"selectType\":{selectType}");
                entries.Add($"\"language\":2");
                entries.Add($"\"random\":\"{rnd}\"");
                entries.Add($"\"signature\":\"{sig}\"");
                entries.Add($"\"timestamp\":{timestamp}");
                
                var finalJson = "{" + string.Join(",", entries) + "}";

                var content = new System.Net.Http.StringContent(finalJson, System.Text.Encoding.UTF8, "application/json");
                
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", _cachedToken);
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("origin", "https://387vn.com");
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("referer", "https://387vn.com/");
                
                // MISSING HEADERS FIX
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("ar-origin", "https://387vn.com");
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("authority", "vn168api.com");
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("accept-language", "en-US,en;q=0.9");
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("priority", "u=1, i");
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua", "\"Google Chrome\";v=\"120\", \"Chromium\";v=\"120\", \"Not?A_Brand\";v=\"24\"");
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-dest", "empty");
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-mode", "cors");
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-site", "cross-site");

                // Add User-Agent (Dynamic from Browser)
                string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"; 
                try {
                    userAgent = await _page.EvaluateAsync<string>("navigator.userAgent");
                } catch {}

                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);

                // Add Cookies (Critical for some sessions)
                try {
                    if (_context != null)
                    {
                        var cookies = await _context.CookiesAsync();
                        var cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                        if (!string.IsNullOrEmpty(cookieHeader))
                        {
                            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookieHeader);
                        }
                    }
                } catch (Exception ex) { Console.WriteLine($"[AutoBet] Cookie Error: {ex.Message}"); }

                var response = await _httpClient.PostAsync("https://vn168api.com/api/webapi/GameBetting", content);
                var respString = await response.Content.ReadAsStringAsync();

                Console.WriteLine($"[API BET] Response: {response.StatusCode} {respString}");

                if (response.IsSuccessStatusCode && respString.Contains("\"code\":0"))
                {
                    Status = $"API SUCCESS! {type} {amount}";
                }
                else
                {
                    Status = $"API FAIL: {response.StatusCode} {respString}";
                }
            }
            catch (Exception ex)
            {
                Status = $"API Error: {ex.Message}";
                Console.WriteLine($"[API BET] Exception: {ex}");
            }
            await BroadcastStatus();
        }

        private string GenerateRandomString(int length)
        {
            // API requires 32-bit string (likely 32 chars Hex)
            // The argument 'length' is ignored in favor of GUID compliance
            return Guid.NewGuid().ToString("N");
        }

        private string SignPayload(System.Collections.Generic.SortedDictionary<string, object> data)
        {
            var entries = new System.Collections.Generic.List<string>();
            foreach(var kvp in data) 
            {
                var val = kvp.Value is string ? $"\"{kvp.Value}\"" : kvp.Value.ToString();
                entries.Add($"\"{kvp.Key}\":{val}");
            }
            var jsonString = "{" + string.Join(",", entries) + "}";
            Console.WriteLine($"[SigDebug] String to Sign: {jsonString}");
            return CreateMD5(jsonString);
        }

        private string SerializeDictionary(System.Collections.Generic.SortedDictionary<string, object> data)
        {
             var entries = new System.Collections.Generic.List<string>();
            foreach(var kvp in data) 
            {
                var val = kvp.Value is string ? $"\"{kvp.Value}\"" : kvp.Value.ToString();
                entries.Add($"\"{kvp.Key}\":{val}");
            }
            return "{" + string.Join(",", entries) + "}";
        }

        public async Task StopBrowserAsync()
        {
            Status = "Stopping...";
            await BroadcastStatus();

            try { if (_page != null) await _page.CloseAsync(); } catch {}
            try { if (_context != null) await _context.CloseAsync(); } catch {}
            try { if (_browser != null) await _browser.CloseAsync(); } catch {}
            
            _playwright?.Dispose();
            
            _page = null;
            _context = null;
            _browser = null;
            _playwright = null;
            Status = "Ready";
            await BroadcastStatus();
        }

        private async Task BroadcastStatus()
        {
             await _hubContext.Clients.All.SendAsync("ReceiveStatus", Status);
        }

        private void TestSignature()
        {
             try 
             {
                 // CURL Data
                 var data = new System.Collections.Generic.SortedDictionary<string, object>
                 {
                     { "typeId", 30 },
                     { "amount", 1000 },
                     { "betCount", 1 },
                     { "gameType", 2 },
                     { "selectType", 14 },
                     { "language", 2 },
                     { "issuenumber", "20251229100052509" },
                     { "random", "94e27291d18a4094859655fcbae4b6bd" }
                 };

                 // Serialize manually to ensure no spaces and exact format
                 var entries = new System.Collections.Generic.List<string>();
                 foreach(var kvp in data) 
                 {
                     var val = kvp.Value is string ? $"\"{kvp.Value}\"" : kvp.Value.ToString();
                     entries.Add($"\"{kvp.Key}\":{val}");
                 }
                 var jsonString = "{" + string.Join(",", entries) + "}";
                 
                 Console.WriteLine($"[SigTest] String: {jsonString}");
                 var md5 = CreateMD5(jsonString);
                 Console.WriteLine($"[SigTest] MD5: {md5}");
                 Console.WriteLine($"[SigTest] Target: B95DA682C2156738C874D20D7EF76AA6");
                 
                 if(md5 == "B95DA682C2156738C874D20D7EF76AA6") {
                     Console.WriteLine("SUCCESS! Algorithm Found: MD5(SortedJSON)");
                 } else {
                     Console.WriteLine("FAIL! Algorithm Mismatch.");
                     
                     // Try with Key?
                     var withKey = jsonString + "94e27291d18a4094859655fcbae4b6bd"; // random as key?
                     Console.WriteLine($"[SigTest] Try+Random: {CreateMD5(withKey)}");
                 }
             }
             catch(Exception ex)
             {
                 Console.WriteLine($"[SigTest] Error: {ex.Message}");
             }
        }

        private string CreateMD5(string input)
        {
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("X2"));
                }
                return sb.ToString();
            }
        }
    }
}
