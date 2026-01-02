using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TL;
using WTelegram;

namespace DropAI.Services
{
    public class ExternalSignalService
    {
        private readonly Client _client;
        private readonly ILogger<ExternalSignalService> _logger;
        private Channel? _targetChannel;
        private DateTime _lastUpdateTime;
        private int _lastProcessedId = 0;
        private string _lastProcessedText = "";
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AiPrediction> _signalCache = new();

        public ExternalSignalService(ILogger<ExternalSignalService> logger)
        {
            _logger = logger;
            
            // Configure WTelegram
            _client = new Client(Config);
            _lastUpdateTime = DateTime.MinValue;
            
            // Auto-initialize with phone number
            _ = Task.Run(async () =>
            {
                try
                {
                    await InitializeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Không thể khởi tạo ExternalSignalService tự động.");
                }
            });
        }

        private string? Config(string what)
        {
            switch (what)
            {
                case "api_id": return "29084135";
                case "api_hash": return "fc82abcc4e1577d0a5552fba651e7593";
                case "phone_number": return "+84369533653"; // User's Telegram phone
                case "verification_code": 
                    Console.Write("Enter verification code: ");
                    return Console.ReadLine();
                case "session_pathname": return "telegram_session.dat";
                default: return null;
            }
        }

        public async Task InitializeAsync()
        {
            try
            {
                _logger.LogInformation("Đang kết nối với Telegram...");
                
                // Login
                var myself = await _client.LoginUserIfNeeded();
                _logger.LogInformation($"Đã đăng nhập với tài khoản: {myself?.MainUsername ?? myself?.phone ?? "Unknown"}");

                // Join channel @tinhieu168
                await SubscribeToChannel("tinhieu168");
                
                // Start polling channel messages
                _ = Task.Run(PollChannelAsync);
                
                _logger.LogInformation("ExternalSignalService khởi tạo thành công!");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi khởi tạo ExternalSignalService");
                throw;
            }
        }

        private async Task SubscribeToChannel(string channelUsername)
        {
            try
            {
                _logger.LogInformation($"Đang subscribe vào channel @{channelUsername}...");
                
                var resolved = await _client.Contacts_ResolveUsername(channelUsername);
                if (resolved.chats.TryGetValue(resolved.peer.ID, out var chat) && chat is Channel channel)
                {
                    _targetChannel = channel;
                    _logger.LogInformation($"✅ Đã subscribe vào channel: {channel.Title}");
                }
                else
                {
                    _logger.LogError($"Không tìm thấy channel @{channelUsername}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Lỗi khi subscribe channel @{channelUsername}");
            }
        }


        public async Task<List<Message>> GetLatestMessagesAsync(int limit = 10)
        {
            var messages = new List<Message>();
            if (_targetChannel == null) return messages;

            try
            {
                var result = await _client.Messages_GetHistory(_targetChannel, limit: limit);
                
                foreach (var msg in result.Messages)
                {
                    if (msg is Message message)
                    {
                        messages.Add(message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy tin nhắn từ channel");
            }

            return messages;
        }

        public async Task PollChannelAsync()
        {
            while (true)
            {
                try
                {
                    var messages = await GetLatestMessagesAsync(20); // Check more messages (20)
                    foreach (var msg in messages.AsEnumerable().Reverse()) // Chronological order: oldest to newest
                    {
                        // Process if it's a new ID OR if the text in the same ID has changed (handling edits)
                        if (msg.id < _lastProcessedId) continue; 
                        
                        if (msg.message != null)
                        {
                            if (msg.id == _lastProcessedId && msg.message == _lastProcessedText) continue;

                            await ProcessMessage(msg.message);
                            _lastProcessedText = msg.message;
                        }
                        
                        _lastProcessedId = msg.id;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi trong polling loop");
                }

                await Task.Delay(1000); // Poll every 1 second
            }
        }

        private async Task ProcessMessage(string messageText)
        {
            try
            {
                _logger.LogInformation($"📨 Nhận tin nhắn mới: {messageText}");

                // Parse message format:
                // VN168 WINGO 30 GIÂY
                // Kỳ xổ: (100052437)
                // 🪀 Vào Lệnh - NHỎ 🪐

                // Extract Issue Number (looking for long digits, optionally in parentheses)
                // Format could be: Kỳ xổ: (100052437) [9 digits] or 20260102100052437 [17 digits]
                var issueMatch = Regex.Match(messageText, @"(?:Kỳ xổ:\s*)?\(?(\d{8,})\)?");
                if (!issueMatch.Success)
                {
                    // Fallback: search for any sequence of 8+ digits
                    issueMatch = Regex.Match(messageText, @"(\d{8,})"); 
                }

                if (!issueMatch.Success)
                {
                    _logger.LogWarning("⚠️ Không tìm thấy số kỳ xổ trong tin nhắn");
                    return;
                }

                string fullIssue = issueMatch.Groups[1].Value;
                string last5Digits = fullIssue.Length >= 5 ? fullIssue.Substring(fullIssue.Length - 5) : fullIssue;

                // Extract Prediction (LỚN/NHỎ)
                var predictionMatch = Regex.Match(messageText, @"Vào Lệnh\s*-\s*(LỚN|NHỎ)", RegexOptions.IgnoreCase);
                if (!predictionMatch.Success)
                {
                    _logger.LogWarning("⚠️ Không tìm thấy dự đoán trong tin nhắn");
                    return;
                }

                string prediction = predictionMatch.Groups[1].Value.ToUpper() == "LỚN" ? "Big" : "Small";

                // Extract raw signal line (looking for "Vào Lệnh - LỚN/NHỎ")
                // We'll try to find the line that contains the prediction words
                string rawSignal = "";
                var lines = messageText.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("Vào Lệnh", StringComparison.OrdinalIgnoreCase) && 
                       (line.Contains("LỚN", StringComparison.OrdinalIgnoreCase) || line.Contains("NHỎ", StringComparison.OrdinalIgnoreCase)))
                    {
                        rawSignal = line.Trim();
                        break;
                    }
                }

                // Update Cache
                var predictionObj = new AiPrediction
                {
                    Pred = prediction,
                    Confidence = 95,
                    BestStrat = "ExternalSignal",
                    Reason = "Tín hiệu từ kênh @tinhieu168",
                    Occurrences = 1,
                    RawSignalText = rawSignal
                };

                _signalCache[last5Digits] = predictionObj;
                _lastUpdateTime = DateTime.Now;

                _logger.LogInformation($"✅ Saved Signal to Cache - Issue: {last5Digits}, Prediction: {prediction}, Raw: {rawSignal}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý tin nhắn từ channel");
            }

            await Task.CompletedTask;
        }

        public AiPrediction? GetSignal(string targetIssue)
        {
            string targetLast5 = targetIssue.Length >= 5 ? targetIssue.Substring(targetIssue.Length - 5) : targetIssue;
            return _signalCache.TryGetValue(targetLast5, out var signal) ? signal : null;
        }

        public AiPrediction? GetLatestSignal(string targetIssue)
        {
            // Match last 5 digits
            string targetLast5 = targetIssue.Length >= 5 ? targetIssue.Substring(targetIssue.Length - 5) : targetIssue;

            if (_signalCache.TryGetValue(targetLast5, out var signal))
            {
                _logger.LogInformation($"🎯 Found cached signal for issue {targetIssue}: {signal.Pred}");
                return signal;
            }

            // Also try to find a signal that might have a slightly different issue format if possible
            // But usually 5 digits is stable
            
            return null;
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}
