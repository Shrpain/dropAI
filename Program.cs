using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// NEW DEDICATED PROJECT
string supabaseUrl = "https://khboofjxmgaymuzepbwb.supabase.co";
string supabaseKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImtoYm9vZmp4bWdheW11emVwYndiIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NzAxMzYyODcsImV4cCI6MjA4NTcxMjI4N30.oymkhW42xviW1KorAY5lhPXPjGTZLXFwEK4OXo1ixoA";

builder.Services.AddSingleton<DropAI.Services.PredictionService>();
builder.Services.AddSingleton(new DropAI.Services.SupabaseService(supabaseUrl, supabaseKey));
builder.Services.AddSingleton<DropAI.Services.GameApiService>();

// Telegram Bot Service
var botToken = "8556887360:AAFWLcr0bnNSOFvPE_GTMAsmOV4NezXU72k";
if (!string.IsNullOrEmpty(botToken))
{
    builder.Services.AddSingleton(sp => new DropAI.TelegramBot.TelegramBotService(botToken, sp.GetRequiredService<DropAI.Services.GameApiService>()));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DropAI.TelegramBot.TelegramBotService>());
}

using IHost host = builder.Build();

_ = host.Services.GetRequiredService<DropAI.Services.GameApiService>();

await host.RunAsync();

namespace DropAI {
    public partial class Program {
    }
}
