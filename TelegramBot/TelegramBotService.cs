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
                new KeyboardButton[] { "⚙ Cấu hình Martingale", "💰 Cấu hình Vốn" }
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
                    string autoBet = api.IsAutoBetEnabled ? "✅ Bật" : "❌ Tắt";
                    var balance = await api.GetBalanceAsync();
                    
                    await bot.SendTextMessageAsync(chatId, 
                        $"📊 *TRẠNG THÁI HỆ THỐNG*\n" +
                        $"👤 KH: {loginStatus}\n" +
                        $"💰 Số dư: {balance:N0} đ\n" +
                        $"🤖 Auto: {autoBet}\n" +
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

        public async Task BroadcastResultAsync(string balance, string issue, string number, string size, string aiGuess, string aiResult, string betAmount, string historyJson, int occurrences = 0, string reason = "")
        {
            if (_activeChats.IsEmpty) return;

            // 1. Current Result Message
            string status = aiResult == "Thắng" ? "✅ Thắng" : "❌ Thua";
            string patternInfo = occurrences > 0 ? $"\n📊 *Dấu hiệu:* {occurrences} lần" : "";
            string reasonInfo = !string.IsNullOrEmpty(reason) ? $"\n💡 *Lý do:* {reason}" : "";
            
            var msg = $"💰 *Tiền:* {balance}\n" +
                      $"📅 *Phiên:* {issue}\n" +
                      $"🔢 *Số:* {number} ({size})\n" +
                      $"🤖 *AI:* {aiGuess} | {status}{patternInfo}{reasonInfo}\n" +
                      $"💵 *Cược:* {betAmount}";

            // 2. Format History Table (Last 10)
            string tableMsg = "";
            try 
            {
                var history = System.Text.Json.JsonSerializer.Deserialize<List<HistoryItem>>(historyJson);
                if (history != null && history.Count > 0)
                {
                    int winCount = 0;
                    int lossCount = 0;
                    
                    tableMsg = "📊 *10 KẾT QUẢ GẦN NHẤT:*\n`" +
                               "P.Hiên  | Số | Sz | P | AI    | KQ\n" +
                               "--------|----|----|-|-------|---\n";
                    
                    foreach (var item in history.Take(10))
                    {
                        string iss = (item.issue?.Length > 5 ? item.issue.Substring(item.issue.Length - 5) : item.issue) ?? "-----";
                        string num = item.number?.PadRight(2) ?? "--";
                        string sz = item.size == "Big" ? "L" : "N";
                        string parity = item.parity == "Double" ? "C" : "L";
                        string guess = item.aiGuess == "Big" ? "Big  " : (item.aiGuess == "Small" ? "Small" : "-----");
                        string resStr = "---";

                        if (item.aiResult == "Thắng") {
                            resStr = "✅";
                            winCount++;
                        } else if (item.aiResult == "Thua") {
                            resStr = "❌";
                            lossCount++;
                        }

                        // Alignment adjustments for the table
                        tableMsg += $"{iss.PadRight(7)} | {num} | {sz}  | {parity} | {guess} | {resStr}\n";
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
                    // If Win, animation
                    if (aiResult == "Thắng")
                    {
                        try {
                            await _bot.SendAnimationAsync(chatId, InputFile.FromUri("https://media.giphy.com/media/v1.Y2lkPTc5MGI3NjExM2I4YTI4MHBubmZ0OW1ueGZqbmZqbmZqbmZqbmZqJmVwPXYxX2ludGVybmFsX2dpZl9ieV9pZCZjdD1n/kyLYXonQpkUsS6rMUK/giphy.gif"));
                        } catch { }
                    }

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
