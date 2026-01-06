using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Polling;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Telegram.Bot.Types.ReplyMarkups;

namespace DropAI.TelegramBot
{
    public class TelegramBotService : BackgroundService
    {
        private readonly ITelegramBotClient _bot;
        private readonly ConcurrentDictionary<long, long> _activeChats = new(); // Store ChatIDs
        private readonly ConcurrentDictionary<long, string> _userStates = new(); // ChatId -> State (e.g. "WAIT_MARTINGALE")

        // Bot Menu Keyboard (Dynamic)
        private static ReplyKeyboardMarkup GetMainMenu(string? savedUser = null)
        {
            var rows = new List<KeyboardButton[]>
            {
                new KeyboardButton[] { "📊 Trạng thái", "▶ Bật Auto", "⏸ Tắt Auto" },
                new KeyboardButton[] { "⚙ Cấu hình Martingale", "💰 Cấu hình Vốn", "🎯 Cài Target" }
            };

            if (!string.IsNullOrEmpty(savedUser))
            {
                rows.Insert(0, new KeyboardButton[] { $"🔐 Đăng nhập lại ({savedUser})" });
            }

            return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
        }

        public TelegramBotService(string botToken)
        {
            _bot = new TelegramBotClient(botToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stopToken)
        {
            try 
            {
                using var cts = new CancellationTokenSource();

                Console.WriteLine("[TelegramBot] Initializing receiver options...");
                var receiverOptions = new ReceiverOptions
                {
                    AllowedUpdates = new[] { UpdateType.Message },
                    ThrowPendingUpdates = true
                };

                Console.WriteLine("[TelegramBot] Calling StartReceiving...");
                _bot.StartReceiving(
                    updateHandler: HandleUpdateAsync,
                    pollingErrorHandler: HandleErrorAsync,
                    receiverOptions: receiverOptions,
                    cancellationToken: cts.Token
                );

                var me = await _bot.GetMeAsync(cts.Token);
                Console.WriteLine($"[TelegramBot] Bot is ONLINE: @{me.Username} (ID: {me.Id})");
                
                await Task.Delay(-1, cts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramBot] FATAL STARTUP ERROR: {ex}");
            }
        }

        private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            try 
            {
                if (update.Message is not { } message) return;
                if (message.Text is not { } text) return;

                var chatId = message.Chat.Id;
                //_activeChats.TryAdd(chatId, chatId); // Auto-subscribe on any message
                Console.WriteLine($"[TelegramBot] [MSG] From {chatId} (@{message.From?.Username}): {text}");

                text = text.Trim();
                var lowerText = text.ToLower();

                var api = Program.App?.Services.GetRequiredService<Services.GameApiService>();

                if (lowerText.StartsWith("/start"))
                {
                    _activeChats.TryAdd(chatId, chatId);
                    
                    var savedInfo = api?.GetSavedLogin();
                    var menu = GetMainMenu(savedInfo?.User);

                    await bot.SendTextMessageAsync(chatId, 
                        "🤖 *DropAI Bot - Control Panel*\n" +
                        "Sử dụng các nút bên dưới để điều khiển hệ thống.",
                        parseMode: ParseMode.Markdown,
                        replyMarkup: menu,
                        cancellationToken: ct);
                    return;
                }

                if (api == null) return;

                // 1. Handle Input States
                if (_userStates.TryGetValue(chatId, out string state))
                {
                    if (state == "WAIT_MARTINGALE")
                    {
                        if (api.TrySetMartingaleConfig(text))
                        {
                            var savedInfo = api.GetSavedLogin();
                            await bot.SendTextMessageAsync(chatId, $"✅ Cấu hình Martingale MỚI: {string.Join(" -> ", api.MartingaleConfig)}", replyMarkup: GetMainMenu(savedInfo?.User));
                        }
                        else
                        {
                            var savedInfo = api.GetSavedLogin();
                            await bot.SendTextMessageAsync(chatId, "❌ Định dạng sai. Vui lòng nhập dãy số (VD: 2,4,8,16)", replyMarkup: GetMainMenu(savedInfo?.User));
                        }
                        _userStates.TryRemove(chatId, out _);
                        return;
                    }
                    else if (state == "WAIT_AMOUNT")
                    {
                        if (decimal.TryParse(text, out decimal amt) && amt > 0)
                        {
                            api.BaseAmount = amt;
                            var savedInfo = api.GetSavedLogin();
                            await bot.SendTextMessageAsync(chatId, $"✅ Đã đặt mức cược gốc: {amt:N0} đ", replyMarkup: GetMainMenu(savedInfo?.User));
                        }
                        else 
                        {
                            var savedInfo = api.GetSavedLogin();
                            await bot.SendTextMessageAsync(chatId, "❌ Số tiền không hợp lệ.", replyMarkup: GetMainMenu(savedInfo?.User));
                        }
                        _userStates.TryRemove(chatId, out _);
                        return;
                    }
                    else if (state == "WAIT_TARGET")
                    {
                        if (decimal.TryParse(text, out decimal target) && target >= 0)
                        {
                            api.ProfitTarget = target;
                            var savedInfo = api.GetSavedLogin();
                            await bot.SendTextMessageAsync(chatId, $"✅ Đã cài đặt mục tiêu lợi nhuận: {target:N0} đ", replyMarkup: GetMainMenu(savedInfo?.User));
                        }
                        else
                        {
                            var savedInfo = api.GetSavedLogin();
                            await bot.SendTextMessageAsync(chatId, "❌ Số tiền không hợp lệ.", replyMarkup: GetMainMenu(savedInfo?.User));
                        }
                        _userStates.TryRemove(chatId, out _);
                        return;
                    }
                }

                // 2. Handle Commands / Buttons
                if (lowerText.StartsWith("/login"))
                {
                    var parts = text.Split(' ');
                    if (parts.Length < 3) {
                        await bot.SendTextMessageAsync(chatId, "⚠️ Cú pháp: `/login <username> <password>`", parseMode: ParseMode.Markdown);
                        return;
                    }
                    await bot.SendTextMessageAsync(chatId, "⏳ Đang đăng nhập...");
                    var success = await api.LoginAsync(parts[1], parts[2]);
                    if (success) 
                    {
                         var savedInfo = api.GetSavedLogin();
                         await bot.SendTextMessageAsync(chatId, "✅ Đăng nhập thành công! Đang bắt đầu lấy dữ liệu...", replyMarkup: GetMainMenu(savedInfo?.User));
                    }
                    else await bot.SendTextMessageAsync(chatId, "❌ Đăng nhập thất bại. Kiểm tra lại tài khoản.");
                }
                else if (lowerText == "📊 trạng thái" || lowerText.StartsWith("/status"))
                {
                    string loginStatus = api.IsLoggedIn ? "✅ Đã đăng nhập" : "❌ Chưa đăng nhập";
                    string autoBet = api.IsAutoBetEnabled ? "✅ Đang bật" : "⏸ Đang tắt";
                    var balance = await api.GetBalanceAsync();
                    var saved = api.GetSavedLogin();
                    
                    await bot.SendTextMessageAsync(chatId, 
                        $"📊 *TRẠNG THÁI HỆ THỐNG*\n" +
                        $"👤 *Tài khoản:* `{saved?.User ?? "N/A"}` ({loginStatus})\n" +
                        $"💰 *Số dư:* `{balance:N0} đ`\n" +
                        $"🤖 Auto: {autoBet}\n" +
                        $"🎯 Target: {api.ProfitTarget:N0} đ\n" +
                        $"💵 Cược gốc: {api.BaseAmount:N0} đ\n" +
                        $"📈 Chuỗi thắng: {api.WinStreak}\n" +
                        $"⚙ Config: {string.Join(",", api.MartingaleConfig)}",
                        parseMode: ParseMode.Markdown,
                        replyMarkup: GetMainMenu(api.GetSavedLogin()?.User));
                }
                else if (lowerText == "▶ bật auto" || lowerText.Contains("/autobet on"))
                {
                    api.IsAutoBetEnabled = true; 
                    await bot.SendTextMessageAsync(chatId, "✅ Đã BẬT tự động đặt cược.", replyMarkup: GetMainMenu(api.GetSavedLogin()?.User));
                }
                else if (lowerText == "⏸ tắt auto" || lowerText.Contains("/autobet off"))
                {
                    api.IsAutoBetEnabled = false; 
                    await bot.SendTextMessageAsync(chatId, "❌ Đã TẮT tự động đặt cược.", replyMarkup: GetMainMenu(api.GetSavedLogin()?.User));
                }
                else if (lowerText.StartsWith("/mode"))
                {
                    var parts = text.Split(' ');
                    if (parts.Length < 2)
                    {
                        string currentMode = api.UseExternalSignal ? "external" : "ai";
                        await bot.SendTextMessageAsync(chatId, 
                            $"🤖 *Chế độ hiện tại:* `{currentMode}`\n\n" +
                            $"📝 *Cú pháp:*\n" +
                            $"`/mode ai` - Sử dụng AI nội bộ\n" +
                            $"`/mode external` - Sử dụng tín hiệu từ @tinhieu168",
                            parseMode: ParseMode.Markdown);
                        return;
                    }

                    string mode = parts[1].ToLower();
                    if (mode == "ai")
                    {
                        api.UseExternalSignal = false;
                        await bot.SendTextMessageAsync(chatId, "✅ Đã chuyển sang chế độ *AI nội bộ*", parseMode: ParseMode.Markdown);
                    }
                    else if (mode == "external")
                    {
                        api.UseExternalSignal = true;
                        await bot.SendTextMessageAsync(chatId, 
                            "✅ Đã chuyển sang chế độ *Tín hiệu ngoài*\n\n" +
                            "📡 Bot sẽ theo dõi channel @tinhieu168 và đặt cược theo tín hiệu của họ.",
                            parseMode: ParseMode.Markdown);
                    }
                    else
                    {
                        await bot.SendTextMessageAsync(chatId, "⚠️ Mode không hợp lệ. Chọn `ai` hoặc `external`", parseMode: ParseMode.Markdown);
                    }
                }
                else if (lowerText.StartsWith("🔐 đăng nhập lại"))
                {
                    var saved = api.GetSavedLogin();
                    if (saved != null)
                    {
                        await bot.SendTextMessageAsync(chatId, $"⏳ Đang đăng nhập lại với user {saved.User}...");
                        var success = await api.LoginAsync(saved.User, saved.Pass);
                        if (success) await bot.SendTextMessageAsync(chatId, "✅ Đăng nhập thành công!", replyMarkup: GetMainMenu(saved.User));
                        else await bot.SendTextMessageAsync(chatId, "❌ Đăng nhập thất bại. Vui lòng đăng nhập lại thủ công.");
                    }
                    else
                    {
                        await bot.SendTextMessageAsync(chatId, "❌ Không tìm thấy thông tin lưu trữ.", replyMarkup: GetMainMenu());
                    }
                }
                else if (lowerText == "⚙ cấu hình martingale")
                {
                    _userStates[chatId] = "WAIT_MARTINGALE";
                    await bot.SendTextMessageAsync(chatId, 
                        $"⚙ *Nhập cấu hình Martingale mới*\n" +
                        $"Hiện tại: {string.Join(", ", api.MartingaleConfig)}\n\n" +
                        $"Nhập dãy số cách nhau bởi dấu phẩy (VD: 1, 2, 4, 8, 17...)",
                        parseMode: ParseMode.Markdown);
                }
                else if (lowerText == "💰 cấu hình vốn")
                {
                    _userStates[chatId] = "WAIT_AMOUNT";
                    await bot.SendTextMessageAsync(chatId, 
                        $"💰 *Nhập mức cược gốc mới (VNĐ)*\n" +
                        $"Hiện tại: {api.BaseAmount:N0} đ",
                        parseMode: ParseMode.Markdown);
                }
                else if (lowerText == "🎯 cài target")
                {
                    _userStates[chatId] = "WAIT_TARGET";
                    await bot.SendTextMessageAsync(chatId, 
                        $"🎯 *Nhập số tiền lời mục tiêu (VNĐ)*\n" +
                        $"VD: 15000\n" +
                        $"Hiện tại: {api.ProfitTarget:N0} đ\n\n" +
                        $"_Nhập 0 để tắt chức năng Target._",
                        parseMode: ParseMode.Markdown);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramBot] Handler Error: {ex}");
            }
        }

        private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
        {
            Console.WriteLine($"[TelegramBot] POLLING ERROR: {ex.Message}");
            return Task.CompletedTask;
        }

        public async Task BroadcastSimpleAsync(string message)
        {
            if (_activeChats.IsEmpty) return;
            foreach (var chatId in _activeChats.Keys)
            {
                try
                {
                    await _bot.SendTextMessageAsync(chatId, message, parseMode: ParseMode.Markdown);
                }
                catch { }
            }
        }

        public async Task BroadcastResultAsync(string balance, string issue, string number, string size, string aiGuess, string aiResult, string betAmount, string historyJson, int occurrences = 0, string reason = "")
        {
            if (_activeChats.IsEmpty) return;

            // Strictly formatted message as requested
            var msg = $"💰 *Tiền:* {balance}\n" +
                      $"📅 *Phiên:* {issue}\n" +
                      $"🔢 *Số:* {number} ({size})\n" +
                      $"{betAmount}"; // betAmount here contains the raw signal text from GameApiService

            // 2. Format History Table (Last 10)
            string tableMsg = "";
            try 
            {
                var history = System.Text.Json.JsonSerializer.Deserialize<List<HistoryItem>>(historyJson);
                if (history != null && history.Count > 0)
                {
                    int winCount = 0;
                    int lossCount = 0;
                    
                    tableMsg = "📊 *LỊCH SỬ KẾT QUẢ GẦN NHẤT:*\n`" +
                               "Phiên   | Số | Sz | P | Lệnh  | KQ\n" +
                               "--------|----|----|-|-------|---\n";
                    
                    foreach (var item in history.Take(10))
                    {
                        string iss = (item.issue?.Length > 5 ? item.issue.Substring(item.issue.Length - 5) : item.issue) ?? "-----";
                        string num = item.number?.PadRight(2) ?? "--";
                        string sz = item.size == "Big" ? "L" : "N";
                        string parity = item.parity == "Double" ? "C" : "L";
                        string guess = item.aiGuess == "Big" ? "Big  " : (item.aiGuess == "Small" ? "Small" : "-----");
                        string resStr = "---";

                        if (item.aiResult == "Thắng" || item.aiResult == "✅") {
                            resStr = "✅";
                            winCount++;
                        } else if (item.aiResult == "Thua" || item.aiResult == "❌") {
                            resStr = "❌";
                            lossCount++;
                        }

                        // Alignment adjustments for the table
                        tableMsg += $"{iss.PadRight(7)} | {num} | {sz}  | {parity} | {guess.PadRight(5)} | {resStr}\n";
                    }
                    tableMsg += "`";

                    // Add Summary
                    string summary = $"\n📈 *Thắng:* {winCount} | 📉 *Thua:* {lossCount}";
                    if (winCount + lossCount > 0)
                    {
                        summary += $" (*{Math.Round((double)winCount / (winCount + lossCount) * 100)}%*)";
                    }
                    tableMsg += summary;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramBot] Table Error: {ex.Message}");
            }

            foreach (var chatId in _activeChats.Keys)
            {
                try
                {
                    await _bot.SendTextMessageAsync(chatId, msg, parseMode: ParseMode.Markdown);
                    
                    if (!string.IsNullOrEmpty(tableMsg))
                    {
                        await _bot.SendTextMessageAsync(chatId, tableMsg, parseMode: ParseMode.Markdown);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TelegramBot] Send Error to {chatId}: {ex.Message}");
                }
            }
        }

        public class HistoryItem
        {
            public string? issue { get; set; }
            public string? number { get; set; }
            public string? size { get; set; }
            public string? parity { get; set; }
            public string? aiGuess { get; set; }
            public string? aiResult { get; set; }
        }
    }
}
