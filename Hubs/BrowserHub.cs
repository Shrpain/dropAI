using Microsoft.AspNetCore.SignalR;

namespace DropAI.Hubs
{
    public class BrowserHub : Hub
    {
        private readonly Services.GameApiService _gameApiService;
        private readonly TelegramBot.TelegramBotService _botService;

        public BrowserHub(Services.GameApiService gameApiService, TelegramBot.TelegramBotService botService)
        {
            _gameApiService = gameApiService;
            _botService = botService;
        }

        public void SendClientPrediction(string issue, string guess)
        {
            _gameApiService.StorePrediction(issue, guess);
        }

        public async Task NotifyBotResult(string balance, string issue, string number, string size, string aiGuess, string aiResult, string betAmount, string historyJson)
        {
             // Forward to Telegram Service
             await _botService.BroadcastResultAsync(balance, issue, number, size, aiGuess, aiResult, betAmount, historyJson);
        }
    }
}
