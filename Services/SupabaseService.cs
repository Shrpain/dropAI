using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Supabase;
using Postgrest.Attributes;
using Postgrest.Models;
using Supabase.Interfaces;

namespace DropAI.Services
{
    [Table("game_history")]
    public class GameHistoryEntry : BaseModel
    {
        [PrimaryKey("id", false)]
        public string? Id { get; set; }

        [Column("issue_number")]
        public string IssueNumber { get; set; } = "";

        [Column("number")]
        public int Number { get; set; }

        [Column("size")]
        public string Size { get; set; } = ""; // Big/Small

        [Column("parity")]
        public string Parity { get; set; } = ""; // Double/Single

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }

    public class SupabaseService
    {
        private readonly Client _supabase;

        public SupabaseService(string url, string key)
        {
            var options = new SupabaseOptions
            {
                AutoRefreshToken = true,
                AutoConnectRealtime = true
            };
            _supabase = new Client(url, key, options);
        }

        public async Task InitializeAsync()
        {
            await _supabase.InitializeAsync();
        }

        public async Task AddHistoryAsync(GameApiService.GameHistoryItem item)
        {
            try
            {
                var entry = new GameHistoryEntry
                {
                    IssueNumber = item.IssueNumber,
                    Number = item.Number,
                    Size = item.Size,
                    Parity = item.Parity
                };
                await _supabase.From<GameHistoryEntry>().Insert(entry);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Supabase] Insert Error: {ex.Message}");
            }
        }

        public async Task<List<GameHistoryEntry>> GetRecentHistoryAsync(int limit = 1000)
        {
            try
            {
                var response = await _supabase.From<GameHistoryEntry>()
                    .Order(x => x.IssueNumber, Postgrest.Constants.Ordering.Descending)
                    .Limit(limit)
                    .Get();
                return response.Models;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Supabase] Query Error: {ex.Message}");
                return new List<GameHistoryEntry>();
            }
        }

        public async Task RunCleanupAsync()
        {
            try
            {
                await _supabase.Rpc("delete_old_history", null);
                Console.WriteLine("[Supabase] Cleanup executed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Supabase] Cleanup Error: {ex.Message}");
            }
        }
    }
}
