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

                for (int len = 2; len <= 5; len++)
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
            if (history == null || history.Count < 5)
            {
                return new PredictionResult { Pred = "Wait", Confidence = 0, Reason = "Chưa đủ dữ liệu" };
            }

            var recentSize = history.Take(5).Select(h => h.Size).ToList();
            string s = string.Join("", recentSize.Select(sz => sz[0])); // e.g., "BSBSB"
            
            var candidates = new List<PredictionResult>();

            // ===== COMPLETE 5-CHAR BRIDGE PATTERNS =====
            
            // Cầu 1-1: BSBSB hoặc SBSBS → tiếp tục đảo
            if (s == "BSBSB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 90, Reason = "Cầu 1-1", PatternType = "1-1" });
            if (s == "SBSBS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 90, Reason = "Cầu 1-1", PatternType = "1-1" });
            
            // Cầu 2-1: BBSBB, SSBSS, BBSSB, SSBBS → tiếp tục theo pattern
            if (s == "BBSBB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 85, Reason = "Cầu 2-1", PatternType = "2-1" });
            if (s == "SSBSS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 85, Reason = "Cầu 2-1", PatternType = "2-1" });
            if (s == "BSSBS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 85, Reason = "Cầu 2-1", PatternType = "2-1" });
            if (s == "SBBSB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 85, Reason = "Cầu 2-1", PatternType = "2-1" });
            
            // Cầu 2-2: BBSSB, SSBBS → tiếp tục 2-2
            if (s == "BBSSB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 88, Reason = "Cầu 2-2", PatternType = "2-2" });
            if (s == "SSBBS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 88, Reason = "Cầu 2-2", PatternType = "2-2" });
            if (s == "BSSBB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 88, Reason = "Cầu 2-2", PatternType = "2-2" });
            if (s == "SBBSS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 88, Reason = "Cầu 2-2", PatternType = "2-2" });
            
            // Cầu 3-1: BBBSB, SSSBS → tiếp tục
            if (s == "BBBSB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 82, Reason = "Cầu 3-1", PatternType = "3-1" });
            if (s == "SSSBS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 82, Reason = "Cầu 3-1", PatternType = "3-1" });
            if (s == "BSBBB") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 82, Reason = "Cầu 3-1", PatternType = "3-1" });
            if (s == "SBSSS") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 82, Reason = "Cầu 3-1", PatternType = "3-1" });
            
            // Cầu 3-2: BBBSS, SSSBB → đảo sang 2
            if (s == "BBBSS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 84, Reason = "Cầu 3-2", PatternType = "3-2" });
            if (s == "SSSBB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 84, Reason = "Cầu 3-2", PatternType = "3-2" });
            if (s == "SSBBB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 84, Reason = "Cầu 3-2", PatternType = "3-2" });
            if (s == "BBSSS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 84, Reason = "Cầu 3-2", PatternType = "3-2" });
            
            // Cầu 3-3: BBBSS hoặc SSSBB đang hoàn thành 3-3
            if (s == "BSSBB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 86, Reason = "Cầu 3-3", PatternType = "3-3" });
            if (s == "SBBSS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 86, Reason = "Cầu 3-3", PatternType = "3-3" });
            
            // Cầu Bệt (Streak >= 4): BBBBB hoặc SSSSS → tiếp tục
            if (s == "BBBBB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 92, Reason = "Cầu Bệt", PatternType = "Bệt" });
            if (s == "SSSSS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 92, Reason = "Cầu Bệt", PatternType = "Bệt" });
            
            // Cầu Gãy Bệt (4+ rồi đổi): SBBBB, BSSSS → tiếp tục đổi
            if (s == "SBBBB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 78, Reason = "Bệt tiếp", PatternType = "Bệt" });
            if (s == "BSSSS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 78, Reason = "Bệt tiếp", PatternType = "Bệt" });
            if (s == "BBBBS") candidates.Add(new PredictionResult { Pred = "Small", Confidence = 75, Reason = "Gãy Bệt", PatternType = "GãyBệt" });
            if (s == "SSSSB") candidates.Add(new PredictionResult { Pred = "Big", Confidence = 75, Reason = "Gãy Bệt", PatternType = "GãyBệt" });
            
            // ===== AI LEARNING CHECK =====
            for (int len = 3; len <= 5; len++)
            {
                string key = string.Join("", recentSize.Take(len).Reverse().Select(x => x[0]));
                if (_patternStats.TryGetValue(key, out var stats))
                {
                    int total = stats.Win + stats.Lose;
                    if (total > 10)
                    {
                        string p = stats.Win > stats.Lose ? "Big" : "Small";
                        int winChance = (int)((double)Math.Max(stats.Win, stats.Lose) / total * 100);
                        if (winChance >= 60) // Only use if 60%+ confidence
                        {
                            candidates.Add(new PredictionResult { Pred = p, Confidence = winChance, Reason = "AI Thống kê", PatternType = "Learning" });
                        }
                    }
                }
            }

            // ===== SELECTION =====
            if (candidates.Count > 0)
            {
                var best = candidates.OrderByDescending(c => c.Confidence).First();
                Console.WriteLine($"[AI] Pattern: {s} → {best.Reason} ({best.Confidence}%) → {best.Pred}");
                return best;
            }

            // NO FALLBACK - Return Wait
            Console.WriteLine($"[AI] Pattern: {s} → Không khớp, bỏ qua phiên này");
            return new PredictionResult { Pred = "Wait", Confidence = 0, Reason = "Không có pattern", PatternType = "Skip" };
        }
    }
}
