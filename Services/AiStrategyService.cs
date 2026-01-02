using System;
using System.Collections.Generic;
using System.Linq;

namespace DropAI.Services
{
    public class AiPrediction
    {
        public string Pred { get; set; } = "";
        public int Confidence { get; set; }
        public string BestStrat { get; set; } = "None";
        public double BestScore { get; set; }
        public string Details { get; set; } = "";
        public int Occurrences { get; set; }
        public string Reason { get; set; } = ""; // Human-readable reasoning
        public List<string> ProjectedPath { get; set; } = new List<string>(); // Next 5 steps
    }

    public class AiStrategyService
    {
        public static AiPrediction? EnsemblePredict(List<GameHistoryItem> history, int targetIndex = -1, bool projectPath = true)
        {
            if (history.Count < 5) return null;

            var strategies = new Dictionary<string, Func<List<GameHistoryItem>, int, object?>>
            {
                { "Streak", PredictStreak },
                { "ZigZag", PredictZigZag },
                { "Frequency", PredictFrequency },
                { "SmartBridge", PredictSmartBridge },
                { "Symmetry", PredictSymmetry },
                { "Mirror", PredictMirror },
                { "Neural", PredictNeural },
                { "Wave", PredictWave },
                { "Bayesian", PredictBayesian },
                { "MarkovO4", PredictMarkovO4 },
                { "BridgeBreak", PredictBridgeBreak }
            };

            var weights = new Dictionary<string, double>();
            int trainStart = targetIndex + 1;
            int trainLen = 1000;
            int testRange = Math.Min(history.Count - trainStart - 2, trainLen);

            if (testRange < 1)
            {
                foreach (var strat in strategies)
                {
                    var p = strat.Value(history, targetIndex);
                    if (p != null) {
                        string predValue = p is string s ? s : ((dynamic)p).Pred;
                        return new AiPrediction { Pred = predValue, Confidence = 51, BestStrat = strat.Key, Details = "Baseline" };
                    }
                }
                return null;
            }

            foreach (var strat in strategies)
            {
                double correct = 0;
                int attempted = 0;
                double weightedScore = 0;

                for (int i = trainStart; i < trainStart + testRange; i++)
                {
                    var p = strat.Value(history, i);
                    if (p != null)
                    {
                        attempted++;
                        double recency = 1.0 / (1.0 + (i - trainStart) * 0.02);
                        string predValue = p is string s ? s : ((dynamic)p).Pred;
                        if (predValue == history[i].Size) { correct++; weightedScore += recency; }
                        else weightedScore -= recency * 0.8;
                    }
                }
                double finalScore = attempted > 0 ? Math.Max(0, Math.Min(1, 0.5 + (weightedScore / attempted))) : 0.5;
                weights[strat.Key] = finalScore;
            }

            // Entropy (Chaos)
            int changes = 0;
            int entDepth = 20;
            int entMax = Math.Min(trainStart + entDepth - 1, history.Count - 1);
            for (int i = trainStart; i < entMax; i++) if (history[i].Size != history[i + 1].Size) changes++;
            double entropy = (double)changes / entDepth;

            double bigVote = 0;
            double smallVote = 0;
            string bestStrat = "None";
            double bestStratScore = -1;
            string details = "";
            object? bestMeta = null;

            double threshold = 0.52;
            foreach (var strat in strategies)
            {
                double score = weights[strat.Key];
                if (score < threshold) continue;

                var prediction = strat.Value(history, targetIndex);
                if (prediction != null)
                {
                    double impact = Math.Pow(score, 4);
                    if (strat.Key == "Symmetry") impact *= 2.0; // Ưu tiên tuyệt đối khi bắt được cầu
                    
                    string predValue = prediction is string s ? s : GetDynamicProp(prediction, "Pred", "-");
                    
                    if (predValue == "Big") bigVote += impact; else smallVote += impact;
                    
                    int occ = GetDynamicProp(prediction, "Occurrences", 0);
                    string text = $"{strat.Key}({Math.Round(score * 100)}%)";
                    if (occ > 0) text += $"[{occ}]";
                    
                    details += text + "; ";
                    
                    if (score > bestStratScore) { 
                        bestStratScore = score; 
                        bestStrat = strat.Key; 
                        bestMeta = prediction;
                    }
                }
            }

            if (bigVote == 0 && smallVote == 0)
            {
                double bestAny = -1;
                object? fallbackPred = null;
                string fallbackStrat = "None";
                foreach (var strat in strategies)
                {
                    var p = strat.Value(history, targetIndex);
                    if (p != null && weights[strat.Key] > bestAny)
                    {
                        bestAny = weights[strat.Key];
                        fallbackPred = p;
                        fallbackStrat = strat.Key;
                    }
                }
                if (fallbackPred != null) {
                    string predValue = fallbackPred is string s ? s : GetDynamicProp(fallbackPred, "Pred", "-");
                    int occ = fallbackPred is string ? 0 : GetDynamicProp(fallbackPred, "Occurrences", 0);
                    return new AiPrediction { Pred = predValue, Confidence = 50, BestStrat = fallbackStrat, BestScore = bestAny, Details = "Fallback", Occurrences = occ };
                }
                return null;
            }

            double totalVotes = bigVote + smallVote;
            double confRatio = totalVotes > 0 ? Math.Max(bigVote, smallVote) / totalVotes : 0.5;
            double finalConf = 52 + (confRatio * 48);
            double chaosFactor = 1 - Math.Abs(0.5 - entropy);
            finalConf *= (0.65 + (chaosFactor * 0.35));

            int finalOccur = GetDynamicProp(bestMeta, "Occurrences", 0);
            string finalReason = GetDynamicProp(bestMeta, "Reason", GetDefaultReason(bestStrat));

            var result = new AiPrediction
            {
                Pred = bigVote > smallVote ? "Big" : "Small",
                Confidence = (int)Math.Round(Math.Min(finalConf, 99)),
                BestStrat = bestStrat,
                BestScore = bestStratScore,
                Details = details,
                Occurrences = finalOccur,
                Reason = finalReason
            };

            // Recursively predict path if this is the top-level call
            if (projectPath && targetIndex == -1)
            {
                result.ProjectedPath = PredictPath(history, 5);
            }

            return result;
        }

        public static List<string> PredictPath(List<GameHistoryItem> history, int steps)
        {
            var path = new List<string>();
            var tempHistory = new List<GameHistoryItem>(history);

            for (int i = 0; i < steps; i++)
            {
                // Call EnsemblePredict with projectPath = false to avoid recursion
                var p = EnsemblePredict(tempHistory, -1, false);
                if (p != null)
                {
                    path.Add(p.Pred);
                    // Add a mock result to history for next step prediction
                    var mock = new GameHistoryItem
                    {
                        IssueNumber = (long.Parse(tempHistory[0].IssueNumber) + 1).ToString(),
                        Size = p.Pred,
                        Number = p.Pred == "Big" ? 7 : 2, // Mock number
                        Parity = (i % 2 == 0) ? "Single" : "Double" // Mock parity
                    };
                    tempHistory.Insert(0, mock);
                }
                else
                {
                    path.Add("-");
                }
            }
            return path;
        }

        private static T GetDynamicProp<T>(object? obj, string propName, T defaultValue)
        {
            if (obj == null || obj is string) return defaultValue;
            try
            {
                var prop = obj.GetType().GetProperty(propName);
                if (prop != null)
                {
                    var val = prop.GetValue(obj);
                    if (val is T t) return t;
                    if (val != null) return (T)Convert.ChangeType(val, typeof(T));
                }
            }
            catch { }
            return defaultValue;
        }

        private static string GetDefaultReason(string strat)
        {
            return strat switch
            {
                "Streak" => "Phát hiện cầu bệt (Streak)",
                "ZigZag" => "Phát hiện cầu 1-1 (ZigZag)",
                "Frequency" => "Dựa trên tần suất xuất hiện (Frequency)",
                "SmartBridge" => "Khớp mẫu hình cầu đối xứng (Bridge)",
                "Symmetry" => "Cầu đối xứng đặc biệt",
                "Mirror" => "Dự đoán theo quy luật đối gương (Mirror)",
                "Neural" => "Phân tích chuỗi lịch sử sâu (Neural)",
                "Wave" => "Dựa trên chu kỳ sóng (Wave)",
                "Bayesian" => "Thống kê xác suất Bayesian",
                "MarkovO4" => "Chuỗi Markov bậc 4 (Xác suất thống kê)",
                "BridgeBreak" => "Phát hiện điểm gãy cầu",
                _ => "Phân tích tổng hợp (Ensemble)"
            };
        }

        #region Strategies

        private static object? PredictStreak(List<GameHistoryItem> history, int index)
        {
            if (index >= history.Count - 1) return null;
            var prev = history[index + 1];
            int s = 1;
            for (int i = index + 2; i < history.Count; i++)
            {
                if (history[i].Size == prev.Size) s++;
                else break;
            }
            if (s >= 2) return new { Pred = prev.Size, Reason = $"Phát hiện cầu bệt {s} cây", Occurrences = s };
            return null;
        }

        private static object? PredictZigZag(List<GameHistoryItem> history, int index)
        {
            if (index >= history.Count - 2) return null;
            var p1 = history[index + 1];
            var p2 = history[index + 2];
            if (p1.Size != p2.Size) return new { Pred = p1.Size == "Big" ? "Small" : "Big", Reason = "Phát hiện cầu 1-1 (ZigZag)", Occurrences = 0 };
            return null;
        }

        private static string? PredictFrequency(List<GameHistoryItem> history, int index)
        {
            int big = 0; int total = 0;
            for (int i = index + 1; i < Math.Min(history.Count, index + 21); i++)
            {
                if (history[i].Size == "Big") big++;
                total++;
            }
            if (total < 5) return null;
            double ratio = (double)big / total;
            if (ratio > 0.6) return "Big";
            if (ratio < 0.4) return "Small";
            return null;
        }

        private static object? PredictSmartBridge(List<GameHistoryItem> history, int index)
        {
            if (index >= history.Count - 8) return null;
            int bigVote = 0; int smallVote = 0; int matches = 0;
            int[] lengths = { 3, 4, 5, 6 };
            foreach (int len in lengths)
            {
                string sig = "";
                for (int i = 1; i <= len; i++) sig += history[index + i].Size[0].ToString();
                for (int i = index + 2; i < history.Count - len; i++)
                {
                    bool match = true;
                    for (int k = 0; k < len; k++) if (history[i + k].Size[0] != sig[k]) { match = false; break; }
                    if (match) { 
                        if (history[i - 1].Size == "Big") bigVote++; else smallVote++;
                        matches++;
                    }
                }
            }
            if (matches < 3) return null;
            if ((double)bigVote / matches > 0.7) return new { Pred = "Big", Reason = "Khớp cầu đối xứng (Bridge)", Occurrences = matches };
            if ((double)smallVote / matches > 0.7) return new { Pred = "Small", Reason = "Khớp cầu đối xứng (Bridge)", Occurrences = matches };
            return null;
        }

        private static object? PredictSymmetry(List<GameHistoryItem> history, int index)
        {
            if (index >= history.Count - 6) return null;
            
            // Get last 6 results as a string of B/S
            string sig = "";
            for (int i = 1; i <= 6; i++) sig += history[index + i].Size[0].ToString();

            // Pattern Definitions (Last 8-10 results for longer bridges)
            string sig8 = "";
            if (index < history.Count - 8) for (int i = 1; i <= 8; i++) sig8 += history[index + i].Size[0].ToString();
            
            string sig10 = "";
            if (index < history.Count - 10) for (int i = 1; i <= 10; i++) sig10 += history[index + i].Size[0].ToString();

            // 3-3 Bridge (N-N-N-L-L-L or L-L-L-N-N-N)
            if (sig.StartsWith("SSSBBB")) return new { Pred = "Big", Reason = "Theo cầu 3-3 (N-N-N-L-L-L)", Occurrences = 6 };
            if (sig.StartsWith("BBBSSS")) return new { Pred = "Small", Reason = "Theo cầu 3-3 (L-L-L-N-N-N)", Occurrences = 6 };
            if (sig.StartsWith("SSSB") || sig.StartsWith("SSSBB")) return new { Pred = "Big", Reason = "Theo cầu 3-3 (N-N-N-L...)", Occurrences = 3 };
            if (sig.StartsWith("BBBS") || sig.StartsWith("BBBSS")) return new { Pred = "Small", Reason = "Theo cầu 3-3 (L-L-L-N...)", Occurrences = 3 };

            // 2-2 Bridge
            if (sig.StartsWith("SSBB")) return new { Pred = "Small", Reason = "Theo cầu 2-2 (N-N-L-L)", Occurrences = 4 };
            if (sig.StartsWith("BBSS")) return new { Pred = "Big", Reason = "Theo cầu 2-2 (L-L-N-N)", Occurrences = 4 };
            if (sig.StartsWith("SSB")) return new { Pred = "Big", Reason = "Theo cầu 2-2 (N-N-L...)", Occurrences = 2 };
            if (sig.StartsWith("BBS")) return new { Pred = "Small", Reason = "Theo cầu 2-2 (L-L-N...)", Occurrences = 2 };

            // 4-4 Bridge
            if (!string.IsNullOrEmpty(sig8))
            {
                if (sig8 == "SSSSBBBB") return new { Pred = "Small", Reason = "Theo cầu 4-4 (N-N-N-N-L-L-L-L)", Occurrences = 8 };
                if (sig8 == "BBBBSSSS") return new { Pred = "Big", Reason = "Theo cầu 4-4 (L-L-L-L-N-N-N-N)", Occurrences = 8 };
            }

            // 1-1 Bridge (ZigZag) - Higher priority here
            if (sig == "BSBSBS") return new { Pred = "Big", Reason = "Bám cầu 1-1 (N-L-N-L-N-L)", Occurrences = 6 };
            if (sig == "SBSBSB") return new { Pred = "Small", Reason = "Bám cầu 1-1 (L-N-L-N-L-N)", Occurrences = 6 };

            // Symmetry patterns
            if (sig == "BBSSBB") return new { Pred = "Small", Reason = "Đối xứng L-L-N-N-L-L", Occurrences = 6 };
            if (sig == "SSBBSS") return new { Pred = "Big", Reason = "Đối xứng N-N-L-L-N-N", Occurrences = 6 };
            
            return null;
        }

        private static string? PredictMirror(List<GameHistoryItem> history, int index)
        {
            if (index >= history.Count - 2) return null;
            return history[index + 2].Size;
        }

        private static string? PredictNeural(List<GameHistoryItem> history, int index)
        {
            for (int len = 24; len >= 4; len--)
            {
                if (index >= history.Count - len - 1) continue;
                string sig = "";
                for (int i = 1; i <= len; i++) sig += history[index + i].Size[0].ToString();
                for (int i = index + 2; i < history.Count - len; i++)
                {
                    bool match = true;
                    for (int k = 0; k < len; k++) if (history[i + k].Size[0] != sig[k]) { match = false; break; }
                    if (match) return history[i - 1].Size;
                }
            }
            return null;
        }

        private static string? PredictWave(List<GameHistoryItem> history, int index)
        {
            if (index >= history.Count - 20) return null;
            var wave = new List<int>();
            for (int i = index + 1; i < index + 31 && i < history.Count; i++) wave.Add(history[i].Size == "Big" ? 1 : -1);
            int bestP = -1; double maxC = -1;
            for (int p = 1; p <= 12; p++)
            {
                int corr = 0; int count = 0;
                for (int i = 0; i < wave.Count - p; i++) { if (wave[i] == wave[i + p]) corr++; count++; }
                if (count > 0 && (double)corr / count > maxC) { maxC = (double)corr / count; bestP = p; }
            }
            if (maxC > 0.72 && bestP > 0) return history[index + bestP].Size;
            return null;
        }

        private static string? PredictBayesian(List<GameHistoryItem> history, int index)
        {
            if (history.Count < 100) return null;
            string seq = string.Join("", history.Skip(index + 1).Take(9).Select(x => x.Size[0]));
            int big = 0; int small = 0;
            for (int i = index + 11; i < history.Count - 9; i++)
            {
                int m = 0;
                for (int k = 0; k < 9; k++) if (history[i + k].Size[0] == seq[k]) m++;
                if (m >= 7) { if (history[i - 1].Size == "Big") big += m; else small += m; }
            }
            if (big == 0 && small == 0) return null;
            return big > small ? "Big" : "Small";
        }

        private static string? PredictMarkovO4(List<GameHistoryItem> history, int index)
        {
            if (index >= history.Count - 5) return null;
            string s = string.Join("", history.Skip(index + 1).Take(4).Select(x => x.Size[0]));
            int big = 0; int small = 0; int total = 0;
            for (int i = index + 2; i < history.Count - 5; i++)
            {
                if (history[i + 3].Size[0] == s[3] && history[i + 2].Size[0] == s[2] && history[i + 1].Size[0] == s[1] && history[i].Size[0] == s[0])
                {
                    if (history[i - 1].Size == "Big") big++; else small++;
                    total++;
                }
            }
            if (total < 2) return null;
            return big > small ? "Big" : "Small";
        }

        private static object? PredictBridgeBreak(List<GameHistoryItem> history, int targetIndex)
        {
            if (targetIndex >= history.Count - 10) return null;

            Func<int, int, string> getRecentPattern = (idx, len) => {
                var p = "";
                for (int i = 1; i <= len; i++) p += history[idx + i].Size == "Big" ? "B" : "S";
                return p;
            };

            var patterns = new[] {
                new { seq = "BBS", next = "B", name = "2-1 Big" },
                new { seq = "SSB", next = "S", name = "2-1 Small" },
                new { seq = "BBSS", next = "B", name = "2-2 Big" },
                new { seq = "SSBB", next = "S", name = "2-2 Small" },
                new { seq = "BBBS", next = "B", name = "3-1 Big" },
                new { seq = "SSSB", next = "S", name = "3-1 Small" },
                new { seq = "BBBSS", next = "B", name = "3-2 Big" },
                new { seq = "SSSBB", next = "S", name = "3-2 Small" },
                new { seq = "BBBSSS", next = "B", name = "3-3 Big" },
                new { seq = "SSSBBB", next = "S", name = "3-3 Small" },
                new { seq = "BBBBS", next = "B", name = "4-1 Big" },
                new { seq = "SSSSB", next = "S", name = "4-1 Small" },
                new { seq = "BBBBSS", next = "B", name = "4-2 Big" },
                new { seq = "SSSSBB", next = "S", name = "4-2 Small" },
                new { seq = "BBBBSSS", next = "B", name = "4-3 Big" },
                new { seq = "SSSSBBB", next = "S", name = "4-3 Small" },
                new { seq = "BBBBSSSS", next = "B", name = "4-4 Big" },
                new { seq = "SSSSBBBB", next = "S", name = "4-4 Small" },
                new { seq = "BBBBBS", next = "B", name = "5-1 Big" },
                new { seq = "SSSSSBB", next = "S", name = "5-1 Small" }
            };

            foreach (var p in patterns)
            {
                int len = p.seq.Length;
                if (getRecentPattern(targetIndex, len) == p.seq)
                {
                    int followCount = 0;
                    int breakCount = 0;
                    int searchLimit = Math.Min(history.Count - len - 1, 1000);

                    for (int i = targetIndex + 1; i < searchLimit; i++)
                    {
                        if (getRecentPattern(i, len) == p.seq)
                        {
                            if (history[i].Size[0].ToString() == p.next) followCount++;
                            else breakCount++;
                        }
                    }

                    if (breakCount > followCount && breakCount > 2)
                        return new { Pred = p.next == "B" ? "Small" : "Big", Occurrences = followCount + breakCount, Reason = $"Gãy cầu {p.name}" };
                    
                    return new { Pred = p.next == "B" ? "Big" : "Small", Occurrences = followCount + breakCount, Reason = $"Theo cầu {p.name}" };
                }
            }
            return null;
        }

        #endregion
    }
}
