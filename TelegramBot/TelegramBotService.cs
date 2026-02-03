using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Polling;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Telegram.Bot.Types.ReplyMarkups;
using System.Text;
using System.Text.Json;

namespace DropAI.TelegramBot
{
    public class TelegramBotService : BackgroundService
    {
        private readonly ITelegramBotClient _bot;
        private ConcurrentDictionary<long, long> _activeChats = new(); 
        private readonly ConcurrentDictionary<long, string> _userStates = new(); 
        private readonly Services.GameApiService _api;
        private const string CHATS_FILE = "active_chats.json";

        public TelegramBotService(string botToken, Services.GameApiService api)
        {
            _bot = new TelegramBotClient(botToken);
            _api = api;
            _api.SetBotService(this);
            LoadActiveChats();
        }

        private void LoadActiveChats()
        {
            try
            {
                if (System.IO.File.Exists(CHATS_FILE))
                {
                    var json = System.IO.File.ReadAllText(CHATS_FILE);
                    var list = JsonSerializer.Deserialize<List<long>>(json);
                    if (list != null)
                    {
                        foreach (var id in list) _activeChats.TryAdd(id, id);
                        Console.WriteLine($"[TelegramBot] Loaded {_activeChats.Count} active chats.");
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[TelegramBot] LoadChats Error: {ex.Message}"); }
        }

        private void SaveActiveChats()
        {
            try
            {
                var json = JsonSerializer.Serialize(_activeChats.Keys.ToList());
                System.IO.File.WriteAllText(CHATS_FILE, json);
            }
            catch (Exception ex) { Console.WriteLine($"[TelegramBot] SaveChats Error: {ex.Message}"); }
        }

        private ReplyKeyboardMarkup GetMainMenu()
        {
            string loginText = _api.IsLoggedIn ? "🔓 Đăng xuất" : "🔐 Đăng nhập";
            string actionText = _api.IsPolling ? "⏸ Tạm dừng" : "▶ Kích hoạt";
            string autoBetText = _api.AutoBetEnabled ? "⏸ Tắt Auto" : "▶ Bật Auto";

            var rows = new List<KeyboardButton[]>
            {
                new KeyboardButton[] { "📊 Trạng thái" },
                new KeyboardButton[] { actionText, autoBetText },
                new KeyboardButton[] { "⚙ Cấu hình", "💰 Cấu hình Vốn", "🎯 Cài Target" },
                new KeyboardButton[] { loginText }
            };
            return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
        }

        protected override async Task ExecuteAsync(CancellationToken stopToken)
        {
            try 
            {
                var receiverOptions = new ReceiverOptions
                {
                    AllowedUpdates = new[] { UpdateType.Message },
                    ThrowPendingUpdates = true
                };

                _bot.StartReceiving(
                    updateHandler: HandleUpdateAsync,
                    pollingErrorHandler: HandleErrorAsync,
                    receiverOptions: receiverOptions,
                    cancellationToken: stopToken
                );

                var me = await _bot.GetMeAsync(stopToken);
                Console.WriteLine($"[TelegramBot] Bot is ONLINE: @{me.Username}");
                
                await Task.Delay(-1, stopToken);
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
                if (_activeChats.TryAdd(chatId, chatId))
                {
                    SaveActiveChats();
                }

                text = text.Trim();
                var lowerText = text.ToLower();

                if (lowerText == "/start")
                {
                    await bot.SendTextMessageAsync(chatId, 
                        "🤖 *DropAI Login Bot*\nNhấn nút bên dưới để bắt đầu.",
                        parseMode: ParseMode.Markdown,
                        replyMarkup: GetMainMenu(),
                        cancellationToken: ct);
                    return;
                }

                // Handle State
                if (_userStates.TryGetValue(chatId, out string? state))
                {
                    if (state == "WAIT_LOGIN")
                    {
                        var parts = text.Split('&');
                        if (parts.Length == 2)
                        {
                            await bot.SendTextMessageAsync(chatId, "⏳ Đang tiến hành đăng nhập...");
                            var success = await _api.LoginAsync(parts[0].Trim(), parts[1].Trim());
                            if (success)
                            {
                                await bot.SendTextMessageAsync(chatId, "✅ Đăng nhập THÀNH CÔNG!", replyMarkup: GetMainMenu());
                            }
                            else
                            {
                                await bot.SendTextMessageAsync(chatId, "❌ Đăng nhập THẤT BẠI. Vui lòng kiểm tra lại thông tin.", replyMarkup: GetMainMenu());
                            }
                        }
                        else
                        {
                            await bot.SendTextMessageAsync(chatId, "⚠️ Định dạng sai. Vui lòng gửi theo mẫu: `sốđiệnthoại&mậtkhẩu`", parseMode: ParseMode.Markdown);
                        }
                        _userStates.TryRemove(chatId, out _);
                        return;
                    }
                    else if (state == "WAIT_BASE_BET")
                    {
                        if (int.TryParse(text, out int val) && val > 0)
                        {
                            _api.BaseBetAmount = val;
                            _api.SaveConfig();
                            await bot.SendTextMessageAsync(chatId, $"✅ Đã đặt cược gốc: `{val:N0} đ`", parseMode: ParseMode.Markdown, replyMarkup: GetMainMenu());
                        }
                        else await bot.SendTextMessageAsync(chatId, "❌ Số tiền không hợp lệ.");
                        _userStates.TryRemove(chatId, out _);
                        return;
                    }
                    else if (state == "WAIT_MARTINGALE")
                    {
                        try {
                            var multipliers = text.Split(',').Select(int.Parse).ToArray();
                            if (multipliers.Length > 0) {
                                _api.MartingaleMultipliers = multipliers;
                                _api.SaveConfig();
                                await bot.SendTextMessageAsync(chatId, $"✅ Đã cập nhật Martingale: `{text}`", parseMode: ParseMode.Markdown, replyMarkup: GetMainMenu());
                            }
                        } catch {
                            await bot.SendTextMessageAsync(chatId, "❌ Định dạng sai (VD: 1,2,5,12,28,65)");
                        }
                        _userStates.TryRemove(chatId, out _);
                        return;
                    }
                    else if (state == "WAIT_TARGET_PROFIT")
                    {
                        if (decimal.TryParse(text, out decimal val) && val >= 0)
                        {
                            _api.TargetProfit = val;
                            _api.SaveConfig();
                            await bot.SendTextMessageAsync(chatId, $"✅ Đã đặt mục tiêu lợi nhuận: `{val:N0} đ`", parseMode: ParseMode.Markdown, replyMarkup: GetMainMenu());
                        }
                        else await bot.SendTextMessageAsync(chatId, "❌ Giá trị không hợp lệ.");
                        _userStates.TryRemove(chatId, out _);
                        return;
                    }
                }

                // Handle Buttons
                if (lowerText.Contains("đăng nhập"))
                {
                    _userStates[chatId] = "WAIT_LOGIN";
                    await bot.SendTextMessageAsync(chatId, 
                        "📝 Vui lòng nhập thông tin đăng nhập theo định dạng:\n\n`sốđiệnthoại&mậtkhẩu`",
                        parseMode: ParseMode.Markdown);
                }
                else if (lowerText.Contains("đăng xuất"))
                {
                    _api.Logout();
                    await bot.SendTextMessageAsync(chatId, "🔓 Đã ĐĂNG XUẤT và hủy phiên làm việc.", replyMarkup: GetMainMenu());
                }
                else if (lowerText == "📊 trạng thái")
                {
                    var balance = await _api.GetBalanceAsync();
                    var sb = new StringBuilder();
                    sb.AppendLine("📋 *TRẠNG THÁI HỆ THỐNG*");
                    sb.AppendLine($"💰 Số dư: `{balance:N0} đ`");
                    sb.AppendLine($"📡 Theo dõi: `{( _api.IsPolling ? "Đang chạy" : "Đã dừng" )}`");
                    sb.AppendLine($"🤖 Auto cược: `{( _api.AutoBetEnabled ? "BẬT" : "TẮT" )}`");
                    sb.AppendLine($"💵 Cược gốc: `{_api.BaseBetAmount:N0} đ`");
                    sb.AppendLine($"� Martingale: `{string.Join(",", _api.MartingaleMultipliers)}` (Bước: {_api.CurrentMartingaleStep + 1})");
                    
                    if (_api.AutoBetEnabled)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"🎯 *TIẾN ĐỘ CHỐT LỜI*");
                        sb.AppendLine($"⛳ Vốn ban đầu: `{_api.InitialBalance:N0} đ`");
                        sb.AppendLine($"📈 Lợi nhuận hiện tại: `{_api.CurrentProfit:N0} đ`");
                        sb.AppendLine($"🏁 Mục tiêu: `{( _api.TargetProfit > 0 ? _api.TargetProfit.ToString("N0") + " đ" : "Không giới hạn" )}`");
                    }
                    
                    await bot.SendTextMessageAsync(chatId, sb.ToString(), parseMode: ParseMode.Markdown, replyMarkup: GetMainMenu());
                }
                else if (lowerText == "▶ bật auto")
                {
                    var balance = await _api.GetBalanceAsync();
                    _api.InitialBalance = balance; // Capture start balance
                    _api.AutoBetEnabled = true;
                    _api.SaveConfig();
                    await bot.SendTextMessageAsync(chatId, $"✅ Đã BẬT tự động đặt cược!\n💰 Vốn đầu: `{balance:N0} đ`", parseMode: ParseMode.Markdown, replyMarkup: GetMainMenu());
                }
                else if (lowerText == "⏸ tắt auto")
                {
                    _api.AutoBetEnabled = false;
                    _api.SaveConfig();
                    await bot.SendTextMessageAsync(chatId, "🛑 Đã TẮT tự động đặt cược.", replyMarkup: GetMainMenu());
                }
                else if (lowerText == "🎯 cài target")
                {
                    _userStates[chatId] = "WAIT_TARGET_PROFIT";
                    await bot.SendTextMessageAsync(chatId, "🎯 Nhập mức lợi nhuận muốn chốt (VD: 200000). Gửi 0 để bỏ giới hạn:");
                }
                else if (lowerText == "💰 cấu hình vốn")
                {
                    _userStates[chatId] = "WAIT_BASE_BET";
                    await bot.SendTextMessageAsync(chatId, "💰 Nhập số tiền cược gốc (VD: 1000):");
                }
                else if (lowerText == "⚙ cấu hình")
                {
                    _userStates[chatId] = "WAIT_MARTINGALE";
                    await bot.SendTextMessageAsync(chatId, "⚙ Nhập dãy Martingale (VD: 1,2,5,12,28,65):");
                }
                else if (lowerText == "▶ kích hoạt")
                {
                    if (!_api.IsLoggedIn)
                    {
                        await bot.SendTextMessageAsync(chatId, "⚠️ Vui lòng đăng nhập trước khi kích hoạt!");
                        return;
                    }
                    _api.StartPolling();
                    await bot.SendTextMessageAsync(chatId, "✅ Đã KÍCH HOẠT theo dõi kết quả!", replyMarkup: GetMainMenu());
                }
                else if (lowerText == "⏸ tạm dừng")
                {
                    _api.StopPolling();
                    await bot.SendTextMessageAsync(chatId, "🛑 Đã TẠM DỪNG theo dõi kết quả.", replyMarkup: GetMainMenu());
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

        public async Task BroadcastResultAsync(string balance, string issue, string number, string size, string historyJson, string nextPred, int confidence, string reason)
        {
            var sb = new StringBuilder();
            sb.AppendLine("🔔 *KẾT QUẢ MỚI*");
            sb.AppendLine($"💰 Số dư: `{balance}`");
            sb.AppendLine($"📅 Phiên: `{issue}`");
            sb.AppendLine($"🎯 Kết quả: *{number} ({size})*");
            
            if (_api.LastBetIssue == issue)
            {
                bool win = _api.LastBetSide == size;
                sb.AppendLine($"🎰 Cược: `{_api.LastBetSide}` ({_api.LastBetAmount:N0} đ) -> {(win ? "✅ THẮNG" : "❌ THUA")}");
            }
            
            sb.AppendLine();
            
            if (nextPred == "Wait") {
                sb.AppendLine("💡 *Dự đoán AI:* `Đang chờ tín hiệu...` ⏳");
            } else {
                string betInfo = _api.AutoBetEnabled ? $" (🤖 Đã cược: `{_api.BaseBetAmount * _api.MartingaleMultipliers[_api.CurrentMartingaleStep]:N0}đ`)" : "";
                sb.AppendLine($"💡 *Dự đoán AI:* `{nextPred}` ({confidence}%){betInfo}");
                
                if (_api.AutoBetEnabled && _api.TargetProfit > 0)
                {
                    sb.AppendLine($"📈 Lợi nhuận: `+{_api.CurrentProfit:N0}` / `{_api.TargetProfit:N0}đ` ⛳");
                }
                else if (_api.AutoBetEnabled)
                {
                    sb.AppendLine($"📈 Lợi nhuận hiện tại: `+{_api.CurrentProfit:N0} đ` 🚀");
                }
            }
            
            sb.AppendLine($"🧬 *Lý do:* _{reason}_");
            sb.AppendLine();
            sb.AppendLine("📊 *Lịch sử 10 phiên:*");
            sb.AppendLine("`Phiên    | Số | Sz | P | AI` ");
            sb.AppendLine("`----------------------------` ");

            try
            {
                var historyItems = JsonSerializer.Deserialize<List<HistoryDisplayItem>>(historyJson);
                if (historyItems != null)
                {
                    foreach (var item in historyItems)
                    {
                        string issueShort = item.issue.Length > 8 ? item.issue[^8..] : item.issue;
                        string sz = item.sz.StartsWith("B") ? "B" : "S";
                        string p = item.p.StartsWith("D") ? "C" : "L";
                        string ai = string.IsNullOrEmpty(item.res) ? "  " : item.res;
                        sb.AppendLine($"`{issueShort} | {item.num}  | {sz} | {p} | {ai}`");
                    }
                }
            }
            catch { }

            string finalMsg = sb.ToString();
            foreach (var chatId in _activeChats.Keys)
            {
                try { await _bot.SendTextMessageAsync(chatId, finalMsg, parseMode: ParseMode.Markdown); }
                catch { }
            }
        }

        private class HistoryDisplayItem {
            public string issue { get; set; } = "";
            public int num { get; set; }
            public string sz { get; set; } = "";
            public string p { get; set; } = "";
            public string pred { get; set; } = "";
            public string res { get; set; } = "";
        }

        public async Task BroadcastSimpleAsync(string message)
        {
            foreach (var chatId in _activeChats.Keys)
            {
                try { await _bot.SendTextMessageAsync(chatId, message, parseMode: ParseMode.Markdown); }
                catch { }
            }
        }
    }
}
