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
        private string? _latestPrediction;
        private string? _latestIssue;
        private string? _latestRawSignal; // Store raw signal text like "🪀 Vào Lệnh - NHỎ 🪐"
        private DateTime _lastUpdateTime;

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
                    var messages = await GetLatestMessagesAsync(5);
                    foreach (var msg in messages)
                    {
                        await ProcessMessage(msg.message);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi trong polling loop");
                }

                await Task.Delay(2000); // Poll every 2 seconds
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

                // Extract Issue Number (last 5 digits)
                var issueMatch = Regex.Match(messageText, @"Kỳ xổ: \((\d+)\)");
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

                // Extract raw signal line (e.g., "🪀 Vào Lệnh - NHỎ 🪐")
                var rawLineMatch = Regex.Match(messageText, @"🪀 Vào Lệnh - (LỚN|NHỎ) 🪐", RegexOptions.IgnoreCase);
                string rawSignal = rawLineMatch.Success ? rawLineMatch.Value : "";

                // Update latest signal
                _latestIssue = last5Digits;
                _latestPrediction = prediction;
                _latestRawSignal = rawSignal;
                _lastUpdateTime = DateTime.Now;

                _logger.LogInformation($"✅ Parsed Signal - Issue: {last5Digits}, Prediction: {prediction}, Raw: {rawSignal}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý tin nhắn từ channel");
            }

            await Task.CompletedTask;
        }

        public AiPrediction? GetLatestSignal(string targetIssue)
        {
            // Match last 5 digits
            string targetLast5 = targetIssue.Length >= 5 ? targetIssue.Substring(targetIssue.Length - 5) : targetIssue;

            if (_latestIssue == targetLast5 && !string.IsNullOrEmpty(_latestPrediction))
            {
                // Signal is fresh (within last 60 seconds)
                if ((DateTime.Now - _lastUpdateTime).TotalSeconds < 60)
                {
                    _logger.LogInformation($"🎯 Sử dụng tín hiệu ngoài cho issue {targetIssue}: {_latestPrediction}");
                    
                    return new AiPrediction
                    {
                        Pred = _latestPrediction,
                        Confidence = 95, // External signal treated as high confidence
                        BestStrat = "ExternalSignal",
                        Reason = "Tín hiệu từ kênh @tinhieu168",
                        Occurrences = 1,
                        RawSignalText = _latestRawSignal ?? "" // Include raw signal text
                    };
                }
                else
                {
                    _logger.LogWarning($"⚠️ Tín hiệu đã cũ ({(DateTime.Now - _lastUpdateTime).TotalSeconds}s). Bỏ qua.");
                }
            }
            else
            {
                _logger.LogWarning($"⚠️ Không tìm thấy tín hiệu cho issue {targetIssue}. Latest: {_latestIssue}");
            }

            return null;
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}
