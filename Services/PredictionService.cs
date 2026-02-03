using System;
using System.Collections.Generic;
using System.Linq;

namespace DropAI.Services
{
    public class PredictionService
    {
        public class PredictionResult
        {
            public string Pred { get; set; } = "Wait"; 
            public int Confidence { get; set; } = 0;
            public string Reason { get; set; } = "Chờ pattern";
            public string PatternType { get; set; } = "None";
            public bool ShouldSkip => Pred == "Wait";
        }

        private List<GameHistoryEntry> _longHistory = new();
        private Dictionary<string, (int Win, int Lose)> _patternStats = new();

        public void UpdateLongHistory(List<GameHistoryEntry> history)
        {
            _longHistory = history;
            TrainAI();
        }

        private void TrainAI()
        {
            if (_longHistory.Count < 50) return;
            
            _patternStats.Clear();
            var data = _longHistory.AsEnumerable().Reverse().ToList(); 

            for (int i = 10; i < data.Count; i++)
            {
                var slice = data.Skip(i - 10).Take(10).Select(h => h.Size).ToList();
                var result = data[i].Size; 

                for (int len = 2; len <= 7; len++) 
                {
                    string patternKey = string.Join("", slice.Skip(10 - len).Select(s => s[0]));
                    if (!_patternStats.ContainsKey(patternKey)) _patternStats[patternKey] = (0, 0);
                    
                    var stats = _patternStats[patternKey];
                    if (result == "Big") stats.Win++; else stats.Lose++;
                    _patternStats[patternKey] = stats;
                }
            }
            Console.WriteLine($"[AI] Training complete. Patterns learned: {_patternStats.Count}");
        }

        public PredictionResult PredictNext(List<GameApiService.GameHistoryItem> history)
        {
            if (history == null || history.Count < 7)
            {
                return new PredictionResult { Pred = "Wait", Confidence = 0, Reason = "Chưa đủ dữ liệu (Cần 7+)" };
            }

            var candidates = new List<PredictionResult>();
            
            // ===== 1. LOGIC 7 KÝ TỰ (ĐỘ CHÍNH XÁC CAO - PRIORITY 1) =====
            // String s7: index 0 là MỚI NHẤT. Ví dụ "BSBSBSB" nghĩa là Mới nhất là B.
            // Thứ tự thời gian: s7[6] -> s7[5] ... -> s7[0]
            
            var recentSize7 = history.Take(7).Select(h => h.Size).ToList();
            string s7 = string.Join("", recentSize7.Select(sz => sz[0])); 
            
            // --- 1. CẦU 1-1 (PING PONG) ---
            // Chrono: ... B S B S B S B (Mới nhất) => Next: S
            if (s7 == "BSBSBSB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 96, Reason = "Cầu 1-1 (7 phiên)", PatternType = "1-1" });
            if (s7 == "SBSBSBS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 96, Reason = "Cầu 1-1 (7 phiên)", PatternType = "1-1" });

            // --- 2. CẦU 2-2 (DOUBLE PING PONG) ---
            // Chrono: ... B S S B B S S (Mới nhất) => Next: B (Để hoàn thành cặp S S -> B B)
            // String: SSBBSSB
            if (s7 == "SSBBSSB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 94, Reason = "Cầu 2-2 (Gãy S sang B)", PatternType = "2-2" });
            if (s7 == "BBSSBBS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 94, Reason = "Cầu 2-2 (Gãy B sang S)", PatternType = "2-2" });
            
            // Chrono: ... S S B B S S B (Mới nhất) => Next: B (Hoàn thành cặp B)
            // String: BSSBBSS
            if (s7 == "BSSBBSS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 95, Reason = "Cầu 2-2 (Tiếp B)", PatternType = "2-2" });
            if (s7 == "SBBSSBB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 95, Reason = "Cầu 2-2 (Tiếp S)", PatternType = "2-2" });

            // --- 3. CẦU 2-1 (TWO-ONE: BBS / SSB) ---
            // Unit: BBS. Chrono: ... B B S B B S B (Mới nhất) => Next: B (Để tạo BBS B...)
            // String: BSBBSBB
            if (s7 == "BSBBSBB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 93, Reason = "Cầu 2-1 (Tiếp B)", PatternType = "2-1" });
            if (s7 == "SBSSBSS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 93, Reason = "Cầu 2-1 (Tiếp S)", PatternType = "2-1" });
            
            // Unit: BBS. Chrono: ... S B B S B B (Mới nhất) => Next: S (Hoàn thành BBS)
            // String: BBSBBSB
            if (s7 == "BBSBBSB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 93, Reason = "Cầu 2-1 (Đảo S)", PatternType = "2-1" });
            if (s7 == "SSBSSBS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 93, Reason = "Cầu 2-1 (Đảo B)", PatternType = "2-1" });

            // --- 4. CẦU 1-2 (ONE-TWO: BSS / SBB) ---
            // Unit: BSS. Chrono: ... B S S B S S B (Mới nhất) => Next: S
            // String: BSSBSSB
            if (s7 == "BSSBSSB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 92, Reason = "Cầu 1-2 (Tiếp S)", PatternType = "1-2" });
            if (s7 == "SBBSBBS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 92, Reason = "Cầu 1-2 (Tiếp B)", PatternType = "1-2" });

            // --- 5. CẦU 3-1 (THREE-ONE: BBBS / SSSB) ---
            // Chrono: ... B B B S B B B (Mới nhất) => Next: S
            // String: BBBSBBB
            if (s7 == "BBBSBBB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 91, Reason = "Cầu 3-1 (Đảo S)", PatternType = "3-1" });
            if (s7 == "SSSBSSS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 91, Reason = "Cầu 3-1 (Đảo B)", PatternType = "3-1" });
            
            // Matches mid-pattern? Chrono: S B B B S B B (Mới nhất) => Next: B
            // String: BBSBBBS
            if (s7 == "BBSBBBS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 90, Reason = "Cầu 3-1 (Tiếp B)", PatternType = "3-1" });
            if (s7 == "SSBSSSB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 90, Reason = "Cầu 3-1 (Tiếp S)", PatternType = "3-1" });

            // --- 6. CẦU 3-2 (THREE-TWO: BBBSS / SSSBB) ---
            // Chrono: ... S S B B B S S (Mới nhất) => Next: B (Đổi cầu)
            // String: SSBBBSS
            if (s7 == "SSBBBSS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 89, Reason = "Cầu 3-2 (Đảo B)", PatternType = "3-2" });
            if (s7 == "BBSSSBB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 89, Reason = "Cầu 3-2 (Đảo S)", PatternType = "3-2" });

            // --- 7. CẦU 3-3 (THREE-THREE: BBBSSS) ---
            // Chrono: ... S B B B S S S (Mới nhất) => Next: B (Đổi cầu)
            // String: SSSBBBS
            if (s7 == "SSSBBBS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 90, Reason = "Cầu 3-3 (Đảo B)", PatternType = "3-3" });
            if (s7 == "BBBSSSB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 90, Reason = "Cầu 3-3 (Đảo S)", PatternType = "3-3" });

            // --- 8. CẦU 4-1 (FOUR-ONE: BBBBS / SSSSB) ---
            // Chrono: ... S B B B B S B (Mới nhất) => Next: B (Tiếp B 2)
            // String: BSBBBBS
            if (s7 == "BSBBBBS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 88, Reason = "Cầu 4-1 (Tiếp B)", PatternType = "4-1" });
            if (s7 == "SBSSSSB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 88, Reason = "Cầu 4-1 (Tiếp S)", PatternType = "4-1" });

            // --- 9. CẦU 4-2 (FOUR-TWO: BBBBSS / SSSSBB) ---
            // Chrono: ... B B B B S S B (Mới nhất) => Next: B (Tạo cặp BB)
            // String: BSSBBBB
            if (s7 == "BSSBBBB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 89, Reason = "Cầu 4-2 (Tạo cặp B)", PatternType = "4-2" });
            if (s7 == "SBBSSSS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 89, Reason = "Cầu 4-2 (Tạo cặp S)", PatternType = "4-2" });

            // --- 10. CẦU BỆT (STREAK 7+) ---
            if (s7 == "BBBBBBB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 98, Reason = "Siêu Bệt Rồng (7)", PatternType = "Bệt" });
            if (s7 == "SSSSSSS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 98, Reason = "Siêu Bệt Rồng (7)", PatternType = "Bệt" });

            // --- 11. CẦU 1-3 (ONE-THREE: BSSS / SBBB) ---
            // S B B B S B B (Mới: B) - Không rõ
            // ... B S S S B S S (Mới: S) -> Next: S (để tạo 3)
            // String: SSBSSSB
            if (s7 == "SSBSSSB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 88, Reason = "Cầu 1-3 (Tiếp S3)", PatternType = "1-3" });
            if (s7 == "BBSBBBS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 88, Reason = "Cầu 1-3 (Tiếp B3)", PatternType = "1-3" });

            // --- 12. CẦU 2-3 (TWO-THREE: BBSSS / SSBBB) ---
            // ... S S S B B S S (Mới: S) -> Next: S (để tạo 3)
            // String: SSBBSSS
            if (s7 == "SSBBSSS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 89, Reason = "Cầu 2-3 (Tiếp S3)", PatternType = "2-3" });
            if (s7 == "BBSSBBB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 89, Reason = "Cầu 2-3 (Tiếp B3)", PatternType = "2-3" });

            // --- 13. CẦU LEO DỐC 1-2-3 (B SS BBB ...) ---
            // SS B SSS B S
            // ... B S S B B B S (Mới: S - bắt đầu chu kỳ 1) -> Next: B
            // String: SBBBSSB
            if (s7 == "SBBBSSB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 90, Reason = "Cầu Leo dốc 1-2-3 (Next B)", PatternType = "1-2-3" });
            if (s7 == "BSSSBBS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 90, Reason = "Cầu Leo dốc 1-2-3 (Next S)", PatternType = "1-2-3" });

            // --- 14. CẦU XUỐNG DỐC 3-2-1 (BBB SS B) ---
            // ... B B B S S B S (Mới: S - Chu kỳ 1) -> Next: B (Chu kỳ 3 mới)
            // String: SBSSBBB
            if (s7 == "SBSSBBB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 90, Reason = "Cầu Xuống dốc 3-2-1", PatternType = "3-2-1" });
            if (s7 == "BSBBSSS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 90, Reason = "Cầu Xuống dốc 3-2-1", PatternType = "3-2-1" });

            // --- 15. GÃY BỆT (GÃY SAU 5-6) ---
            // Chrono: S S S S S B B (Mới nhất) -> Đã gãy, đang tạo dây mới B? => Tiếp B
            // String: BBSSSSS
            if (s7 == "BBSSSSS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 85, Reason = "Bệt gãy sang dây mới", PatternType = "GãyBệt" });
            if (s7 == "SSBBBBB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 85, Reason = "Bệt gãy sang dây mới", PatternType = "GãyBệt" });

            // Nếu có pattern 7 ký tự -> return ngay
            if (candidates.Count > 0)
            {
                var best = candidates.OrderByDescending(c => c.Confidence).First();
                Console.WriteLine($"[AI] Pattern 7: {s7} → {best.Reason} ({best.Confidence}%) → {best.Pred}");
                return best;
            }

            // ===== 2. FALLBACK 5 KÝ TỰ (PRIORITY 2) =====
            var recentSize5 = history.Take(5).Select(h => h.Size).ToList();
            string s = string.Join("", recentSize5.Select(sz => sz[0])); 
            
            // 1-1
            if (s == "BSBSB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 85, Reason = "Cầu 1-1", PatternType = "1-1" });
            if (s == "SBSBS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 85, Reason = "Cầu 1-1", PatternType = "1-1" });
            
            // 2-1
            if (s == "BBSBB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 82, Reason = "Cầu 2-1", PatternType = "2-1" });
            if (s == "SSBSS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 82, Reason = "Cầu 2-1", PatternType = "2-1" });
            
            // 2-2
            if (s == "BBSSB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 84, Reason = "Cầu 2-2", PatternType = "2-2" });
            if (s == "SSBBS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 84, Reason = "Cầu 2-2", PatternType = "2-2" });

            // 3-1
            if (s == "BBBSB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 80, Reason = "Cầu 3-1", PatternType = "3-1" });
            if (s == "SSSBS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 80, Reason = "Cầu 3-1", PatternType = "3-1" });
            
            // 3-2
            if (s == "BBBSS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 81, Reason = "Cầu 3-2", PatternType = "3-2" });
            if (s == "SSSBB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 81, Reason = "Cầu 3-2", PatternType = "3-2" });
            
            // Bệt 5
            if (s == "BBBBB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 88, Reason = "Bệt 5", PatternType = "Bệt" });
            if (s == "SSSSS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 88, Reason = "Bệt 5", PatternType = "Bệt" });

            // Check AI Stats - Relaxed Rules
            for (int len = 3; len <= 7; len++)
            {
                string key = string.Join("", recentSize7.Take(len).Reverse().Select(x => x[0]));
                if (_patternStats.TryGetValue(key, out var stats))
                {
                    int total = stats.Win + stats.Lose;
                    if (total > 3) // Reduced from 8 to 3
                    {
                        string p = stats.Win > stats.Lose ? "Big" : "Small";
                        int winChance = (int)((double)Math.Max(stats.Win, stats.Lose) / total * 100);
                        if (winChance >= 55) // Reduced from 60 to 55
                        {
                            candidates.Add(new PredictionResult { Pred = p, Confidence = winChance, Reason = "AI Thống kê", PatternType = "Learning" });
                        }
                    }
                }
            }

            if (candidates.Count > 0)
            {
                var best = candidates.OrderByDescending(c => c.Confidence).First();
                Console.WriteLine($"[AI] Pattern 5/Stats: {s} → {best.Reason} ({best.Confidence}%) → {best.Pred}");
                return best;
            }

            // FINAL FALLBACK TO AVOID SKIPPING
            // Simple trend logic: if last result repeated >= 2 times, follow. Else, reverse.
            string last = recentSize7[0];
            bool streak2 = recentSize7[0] == recentSize7[1];
            string trendPred = streak2 ? last : (last == "Big" ? "Small" : "Big");
            
            Console.WriteLine($"[AI] No strong pattern. Fallback to {(streak2 ? "Trend" : "Reverse")}: {trendPred}");
            return new PredictionResult { Pred = trendPred, Confidence = 65, Reason = "Xu hướng ngắn", PatternType = "Fallback" };
        }
    }
}
