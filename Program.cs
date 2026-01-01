var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddSingleton<DropAI.Services.GameApiService>();
// builder.Services.AddSingleton<DropAI.Services.BrowserService>();

// Telegram Bot Service
var botToken = "8556887360:AAFWLcr0bnNSOFvPE_GTMAsmOV4NezXU72k";
if (!string.IsNullOrEmpty(botToken))
{
    // Register as Singleton for injection
    builder.Services.AddSingleton(sp => new DropAI.TelegramBot.TelegramBotService(botToken));
    // Register as HostedService for auto-start (using the same singleton instance)
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DropAI.TelegramBot.TelegramBotService>());
}
// ...

var app = builder.Build();
DropAI.Program.App = app;

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    // app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();
app.MapHub<DropAI.Hubs.BrowserHub>("/browserHub");

app.Run();

namespace DropAI {
    public partial class Program {
        public static WebApplication? App { get; set; }
    }
}
