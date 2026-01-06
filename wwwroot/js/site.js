$(document).ready(function () {
    const $urlInput = $('#urlInput');
    const $btnPlay = $('#btnPlay');
    const $btnStop = $('#btnStop');
    const $statusBadge = $('#statusBadge');
    const $trafficTableBody = $('#trafficTableBody');
    const $detailsCode = $('#detailsCode');
    const $btnClearLog = $('#btnClearLog');
    const $searchInput = $('#searchInput');

    let requestsMap = {};
    const connection = new signalR.HubConnectionBuilder().withUrl("/browserHub").build();

    connection.on("ReceiveStatus", status => updateStatusUI(status));

    // --- AI & HISTORY STORAGE ---
    // Load Pending Predictions (Live snapshots)
    let pendingPredictions = {};
    try {
        const storedPreds = localStorage.getItem('dropAI_pending_preds');
        if (storedPreds) pendingPredictions = JSON.parse(storedPreds);
    } catch (e) { console.error("Load Pending Preds Error", e); }

    // Load from LocalStorage if available on load
    let fullGameHistory = [];
    try {
        const stored = localStorage.getItem('dropAI_history');
        if (stored) {
            fullGameHistory = JSON.parse(stored);
            console.log(`[AI] Loaded ${fullGameHistory.length} games from localStorage`);
        }
    } catch (e) {
        console.error("Load History Error", e);
    }

    // CRITICAL FIX: Fill AI predictions for items loaded from localStorage
    function fillAIPredictions() {
        if (fullGameHistory.length === 0) return;

        let filled = 0;
        fullGameHistory.forEach((item, index) => {
            // Ensure fields exist
            if (!item.hasOwnProperty('aiGuess')) item.aiGuess = null;
            if (!item.hasOwnProperty('aiResult')) item.aiResult = null;

            // Fill if missing or previously failed (marked as '-')
            if (!item.aiGuess || item.aiGuess === '-' || item.aiGuess === null || item.aiGuess === undefined) {
                const prediction = getEnsemblePrediction(fullGameHistory, index);

                if (prediction) {
                    item.aiGuess = prediction.pred;
                    filled++;

                    if (item.size && item.size !== '-') {
                        item.aiResult = prediction.pred === item.size ? 'Thắng' : 'Thua';
                    } else {
                        item.aiResult = '-';
                    }
                } else {
                    item.aiGuess = '-';
                    item.aiResult = '-';
                }
            }
        });

        if (filled > 0) {
            console.log(`[AI] Filled ${filled} missing predictions on page load`);
            localStorage.setItem('dropAI_history', JSON.stringify(fullGameHistory));
        }
    }

    // --- AUTO BET CONFIG STORAGE ---
    // Load Base Amount and Martingale Values from localStorage
    const $baseAmountInput = $('#baseAmountInput');
    const $martingaleConfig = $('#martingaleConfig');

    try {
        const savedBaseAmount = localStorage.getItem('dropAI_baseAmount');
        const savedMartingale = localStorage.getItem('dropAI_martingale');

        if (savedBaseAmount) $baseAmountInput.val(savedBaseAmount);
        if (savedMartingale) $martingaleConfig.val(savedMartingale);
    } catch (e) {
        console.error("Load AutoBet Config Error", e);
    }

    // Save when user changes input
    $baseAmountInput.on('change', function () {
        const val = $(this).val();
        localStorage.setItem('dropAI_baseAmount', val);
    });

    $martingaleConfig.on('change', function () {
        const val = $(this).val();
        localStorage.setItem('dropAI_martingale', val);
    });

    const $targetProfitInput = $('#targetProfitInput');
    try {
        const savedTarget = localStorage.getItem('dropAI_targetProfit');
        if (savedTarget) $targetProfitInput.val(savedTarget);
    } catch (e) { }

    $targetProfitInput.on('change', function () {
        localStorage.setItem('dropAI_targetProfit', $(this).val());
    });

    // Toggle Handler
    $('#autoBetToggle').on('change', function () {
        evaluateAutoBet();
    });

    // Removed Traffic Monitor Logic
    /*
    connection.on("ReceiveNetworkEvent", function (data) {
        // Traffic Log Removed as per request.
    });
    */

    connection.on("ReceiveGameHistory", function (jsonString) {
        try {
            const response = JSON.parse(jsonString);
            const data = response.data || response;
            let list = [];
            if (Array.isArray(data)) list = data;
            else if (data && Array.isArray(data.list)) list = data.list;

            if (list.length > 0) {
                // 1. Accumulate History (Dedup based on issueNumber)
                let hasNew = false;
                list.forEach(newItem => {
                    // Normalize Issue Number
                    const issueNum = newItem.issueNumber || newItem.issue || newItem.period || newItem.number;
                    if (!issueNum) return;

                    // Check if exists
                    const exists = fullGameHistory.some(h =>
                        (h.issueNumber || h.issue || h.period || h.number) == issueNum
                    );

                    if (!exists) {
                        hasNew = true;
                        // Normalize Data for Storage
                        const result = newItem.openNumber || newItem.number || newItem.result;
                        let size = newItem.size;
                        let parity = newItem.parity;

                        // Auto-calc if missing
                        if ((!size || size === '-') && !isNaN(result)) {
                            const n = parseInt(result);
                            size = n >= 5 ? 'Big' : 'Small';
                            parity = n % 2 === 0 ? 'Double' : 'Single';
                        }

                        fullGameHistory.push({
                            issue: issueNum,
                            number: result,
                            size: size,
                            parity: parity,
                            aiGuess: null,      // Will be filled by AI
                            aiResult: null,     // Will be filled after verification
                            raw: newItem
                        });
                    }
                });

                if (hasNew) {
                    // Sort History Descending (Newest First)
                    fullGameHistory.sort((a, b) => {
                        try {
                            const ia = BigInt(a.issue);
                            const ib = BigInt(b.issue);
                            return ia < ib ? 1 : -1;
                        } catch {
                            return a.issue.toString().localeCompare(b.issue.toString()) * -1;
                        }
                    });

                    // AI PREDICTION STORAGE LOGIC
                    // CRITICAL: Process ALL items every time to ensure consistency
                    fullGameHistory.forEach((item, index) => {
                        // Ensure fields exist (migrate old localStorage data)
                        if (!item.hasOwnProperty('aiGuess')) item.aiGuess = null;
                        if (!item.hasOwnProperty('aiResult')) item.aiResult = null;

                        // CRITICAL: Also retry if previously marked as '-' (failed prediction)
                        // This ensures we keep trying as more data becomes available

                        // CRITICAL: Check if we have a LIVE captured prediction for this issue first
                        let prediction = null;
                        const pendingEntry = pendingPredictions[item.issue];
                        if (pendingEntry) {
                            if (typeof pendingEntry === 'string') {
                                prediction = { pred: pendingEntry };
                            } else if (pendingEntry.pred) {
                                prediction = pendingEntry;
                            }
                        }

                        // ONLY re-calculate if we have NO captured record
                        if (!prediction) {
                            prediction = getEnsemblePrediction(fullGameHistory, index);
                        }

                        // DEBUG MATCHING
                        if (!prediction && index < 5) {
                            console.log(`[AI Debug] No prediction for ${item.issue}. Pending keys:`, Object.keys(pendingPredictions).slice(-3));
                        }

                        if (prediction) {
                            item.aiGuess = prediction.pred;

                            // Verify if we have actual result
                            if (item.size && item.size !== '-') {
                                item.aiResult = prediction.pred === item.size ? 'Thắng' : 'Thua';
                            }
                        } else {
                            item.aiGuess = '-';
                            item.aiResult = '-';
                        }
                    });

                    // Save to LocalStorage with AI predictions
                    localStorage.setItem('dropAI_history', JSON.stringify(fullGameHistory));

                    // 2. Update UI Table
                    renderHistoryTable();

                    // 3. Trigger AI Prediction for NEXT game
                    updateAIPrediction();

                    // 4. NOTIFY BOT (Result + Prediction + Bet)
                    const latest = fullGameHistory[0];
                    if (latest) {
                        const curIssue = latest.issue;
                        const curNumber = latest.number;
                        const curSize = latest.size;
                        const curAiGuess = latest.aiGuess || "-";
                        const curAiResult = latest.aiResult || "-";
                        const curBalance = $('#uiAmount').text() || "Unknown";

                        const streakMsg = winStreak > 0 ? `\n🔥 Chuỗi thắng hiện tại: ${winStreak}` : "";

                        // Check if we placed a bet on THIS issue
                        let betAmtStr = "0 đ";
                        if (lastFinishedBetIssue === curIssue) {
                            betAmtStr = lastFinishedBetAmount.toLocaleString() + " đ" + streakMsg;
                        } else {
                            betAmtStr += streakMsg; // Still show streak if available
                        }

                        // Send to Server
                        connection.invoke("NotifyBotResult",
                            curBalance,
                            String(curIssue),
                            String(curNumber),
                            String(curSize),
                            String(curAiGuess),
                            String(curAiResult),
                            betAmtStr,
                            JSON.stringify(fullGameHistory.slice(0, 10))
                        ).catch(err => console.error("Bot Notify Error:", err));
                    }
                }
            }

        } catch (e) {
            console.error("Error parsing game history", e);
        }
    });

    // Generic render placeholder before we load the full one below
    function renderHistoryTable() {
        // This is a placeholder, the full version is at the end of the file.
        // It's called after connection is established.
    }


    connection.on("ReceiveLoginSuccess", function (jsonString) {
        try {
            const response = JSON.parse(jsonString);
            const data = response.data;
            if (data) {
                $('#uiUserName').text(data.userName || data.nickName || "Unknown");
                $('#uiUserId').text(data.userId || "-");
                $('#uiAmount').text(data.amount ? parseFloat(data.amount).toLocaleString() + ' đ' : "0");
                $('#uiSign').val(data.sign || "No Signature Found");

                $('#msgBadge').text(response.msg || "Succeed");
                $('#loginStatusBadge').removeClass('bg-light text-dark').addClass('bg-success').text(data.userName || "User");

                updateStatusUI('Logged In');
            }
        } catch (e) {
            console.error(e);
        }
    });

    connection.on("ReceiveBalance", function (jsonString) {
        try {
            const response = JSON.parse(jsonString);
            const data = response.data;
            // Response format for GetBalance might be simpler, let's assume standard data.amount
            if (data && data.amount) {
                $('#uiAmount').text(parseFloat(data.amount).toLocaleString() + ' đ');
                // Could also animate color to show update
                $('#uiAmount').addClass('text-warning').delay(500).queue(function (next) {
                    $(this).removeClass('text-warning').addClass('text-success');
                    next();
                });
            }
        } catch (e) {
            console.error("Balance Update Error", e);
        }
    });

    connection.on("ReceiveGameTypes", function (jsonString) {
        const $container = $('#gameCategoriesContainer');
        try {
            const response = JSON.parse(jsonString);
            const data = response.data;

            $container.empty();

            if (!data || !Array.isArray(data)) {
                $container.append('<div class="text-muted small">No game types found.</div>');
                return;
            }

            data.forEach(gameType => {
                const name = gameType.typeName || "Unknown Type";
                const id = gameType.typeID || "?";
                const saasId = gameType.saasGameId || "";
                const scope = gameType.scope || "";
                const betMultiple = gameType.betMultiple || "";
                const tooltipText = `Mức cược: ${scope}\nHệ số: ${betMultiple}`;

                const card = `
                    <div class="card text-center p-2 shadow-sm border-primary mb-2 game-card" 
                         data-id="${id}" 
                         title="${tooltipText}"
                         style="width: 140px; cursor: pointer; transition: all 0.2s;">
                        <div class="card-body p-1">
                            <h6 class="card-title fw-bold text-primary mb-1">${name}</h6>
                             <div class="d-flex justify-content-between px-2 mb-1">
                                <span class="badge bg-secondary" style="font-size:0.65rem">ID: ${id}</span>
                                <span class="badge bg-light text-dark border" style="font-size:0.65rem">${saasId}</span>
                             </div>
                             <div class="text-muted text-truncate" style="font-size: 0.6rem;">
                                Cược: ${scope.split('|')[0]}...
                             </div>
                        </div>
                    </div>
                `;
                $container.append(card);
            });

            $container.removeClass('d-block').addClass('d-flex flex-wrap gap-2 justify-content-center');

        } catch (e) {
            console.error("Error parsing game types", e);
            $container.html('<div class="text-danger small">Error parsing types.</div>');
        }
    });

    // --- ADAPTIVE AI ENGINE ---

    // GLOBAL STATE for Auto-Bet
    let martingaleStep = 0;
    let lastBetIssue = "";
    let lastBetType = ""; // Track what we actually bet on
    let lastBetAmount = 0; // Track amount for Bot reporting
    let lastFinishedBetIssue = ""; // For Bot reporting of COMPLETED game
    let lastFinishedBetAmount = 0; // For Bot reporting of COMPLETED game
    let winStreak = 0; // Infinite win streak counter
    let currentAiPrediction = null; // Store latest ensemble result for sync
    let currentAiPredictionMeta = null; // Store metadata like occurrences for sync

    // Auto-Bet State Extended
    let consecutiveLosses = 0;
    let pauseUntil = null;
    let isInRecoveryMode = false;
    let startingBalance = 0;
    let targetProfit = 0;

    const Strategies = {
        // Strategy 1: Streak Follower (Bệt)
        Streak: {
            name: "Streak Follower",
            predict: function (history, targetIndex) {
                if (targetIndex >= history.length - 1) return null;
                const prev = history[targetIndex + 1];
                if (!prev) return null;
                let s = 1;
                for (let i = targetIndex + 2; i < history.length; i++) {
                    if (history[i].size === prev.size) s++;
                    else break;
                }
                if (s >= 2) return prev.size;
                return null;
            }
        },

        // Strategy 2: ZigZag (Ping Pong)
        ZigZag: {
            name: "ZigZag Detector",
            predict: function (history, targetIndex) {
                if (targetIndex >= history.length - 1) return null;
                const prev = history[targetIndex + 1];
                const prev2 = history[targetIndex + 2];
                if (!prev || !prev2) return null;
                if (prev.size !== prev2.size) {
                    return prev.size === 'Big' ? 'Small' : 'Big';
                }
                return null;
            }
        },

        // Strategy 3: Frequency (Markov)
        Frequency: {
            name: "Trend Frequency",
            predict: function (history, targetIndex) {
                let bigCount = 0;
                let total = 0;
                for (let i = targetIndex + 1; i < Math.min(history.length, targetIndex + 21); i++) {
                    if (history[i].size === 'Big') bigCount++;
                    total++;
                }
                if (total < 5) return null;
                const ratio = bigCount / total;
                if (ratio > 0.6) return 'Big';
                if (ratio < 0.4) return 'Small';
                return null;
            }
        },

        // Strategy 4: Smart Bridge Inspector (Cầu Tự Động)
        // Detects patterns like 2-2, 1-2, 1-3, 3-3 by checking multiple signature lengths
        SmartBridge: {
            name: "Smart Bridge (Cầu)",
            predict: function (history, targetIndex) {
                if (targetIndex >= history.length - 8) return null;

                let bestMatchCount = 0;
                let voteBig = 0;
                let voteSmall = 0;

                // Check patterns of length 3 to 6 (covers 1-2, 2-2, 3-3, etc)
                const lengths = [3, 4, 5, 6];

                lengths.forEach(len => {
                    let signature = "";
                    for (let i = 1; i <= len; i++) {
                        signature += history[targetIndex + i].size[0];
                    }

                    // Scan history
                    for (let i = targetIndex + 2; i < history.length - len; i++) {
                        let match = true;
                        for (let k = 0; k < len; k++) {
                            if (history[i + k].size[0] !== signature[k]) {
                                match = false;
                                break;
                            }
                        }
                        if (match) {
                            const nextResult = history[i - 1];
                            if (nextResult) {
                                if (nextResult.size === 'Big') voteBig++;
                                else voteSmall++;
                                bestMatchCount++;
                            }
                        }
                    }
                });

                if (bestMatchCount < 3) return null;
                const total = voteBig + voteSmall;
                const ratio = voteBig / total;
                if (ratio > 0.7) return 'Big';
                if (ratio < 0.3) return 'Small';
                return null;
            }
        },

        // Strategy 5: Mirror (A-B-A)
        MirrorPattern: {
            name: "Mirror Logic (X-2)",
            predict: function (history, targetIndex) {
                if (targetIndex >= history.length - 2) return null;
                const prev2 = history[targetIndex + 2];
                if (!prev2) return null;
                return prev2.size;
            }
        },

        // Strategy 6: Extreme Neural Pattern Matcher
        DynamicPatternMatcher: {
            name: "Neural Pattern Matcher",
            predict: function (history, targetIndex) {
                if (targetIndex >= history.length - 2) return null;
                // High resolution: scan from length 24 down to 4
                for (let seqLen = 24; seqLen >= 4; seqLen--) {
                    if (targetIndex >= history.length - seqLen - 1) continue;
                    let signature = "";
                    for (let i = 1; i <= seqLen; i++) signature += history[targetIndex + i].size[0];
                    for (let i = targetIndex + 2; i < history.length - seqLen; i++) {
                        let match = true;
                        for (let k = 0; k < seqLen; k++) if (history[i + k].size[0] !== signature[k]) { match = false; break; }
                        if (match) return history[i - 1].size;
                    }
                }
                return null;
            }
        },

        // Strategy 7: Wave Resonance (Harmonic Signal Analysis)
        WaveResonance: {
            name: "Wave Resonance",
            predict: function (history, targetIndex) {
                if (targetIndex >= history.length - 20) return null;
                const wave = [];
                for (let i = targetIndex + 1; i < targetIndex + 31 && i < history.length; i++) wave.push(history[i].size === 'Big' ? 1 : -1);
                let bestPeriod = -1; let maxCorr = -1;
                for (let p = 1; p <= 12; p++) {
                    let corr = 0; let count = 0;
                    for (let i = 0; i < wave.length - p; i++) { if (wave[i] === wave[i + p]) corr++; count++; }
                    if (count > 0 && corr / count > maxCorr) { maxCorr = corr / count; bestPeriod = p; }
                }
                if (maxCorr > 0.72 && bestPeriod > 0) return history[targetIndex + bestPeriod].size;
                return null;
            }
        },

        // Strategy 8: Bayesian Correlator (Long-Range Fuzzy)
        LongRangeBayesian: {
            name: "Bayesian Correlator",
            predict: function (history, targetIndex) {
                if (history.length < 100) return null;
                const seq = history.slice(targetIndex + 1, targetIndex + 10).map(x => x.size[0]).join('');
                const scores = { 'Big': 0, 'Small': 0 };
                for (let i = targetIndex + 11; i < history.length - 9; i++) {
                    let matches = 0;
                    for (let k = 0; k < 9; k++) if (history[i + k].size[0] === seq[k]) matches++;
                    if (matches >= 7) scores[history[i - 1].size] += matches;
                }
                if (scores['Big'] === 0 && scores['Small'] === 0) return null;
                return scores['Big'] > scores['Small'] ? 'Big' : 'Small';
            }
        },

        // Strategy 9: Markov Chain Order 4
        MarkovOrder4: {
            name: "Advanced Markov (O4)",
            predict: function (history, targetIndex) {
                if (targetIndex >= history.length - 5) return null;
                const s = [1, 2, 3, 4].map(idx => history[targetIndex + idx].size[0]);
                const counts = { 'Big': 0, 'Small': 0 }; let total = 0;
                for (let i = targetIndex + 2; i < history.length - 5; i++) {
                    if (history[i + 4].size[0] === s[3] && history[i + 3].size[0] === s[2] &&
                        history[i + 2].size[0] === s[1] && history[i + 1].size[0] === s[0]) {
                        counts[history[i].size]++; total++;
                    }
                }
                if (total < 2) return null;
                return counts['Big'] > counts['Small'] ? 'Big' : 'Small';
            }
        },

        // Strategy 10: Bridge & Break Engine (Cầu & Gãy)
        BridgePatternEngine: {
            name: "Bridge & Break Engine",
            predict: function (history, targetIndex) {
                if (targetIndex >= history.length - 10) return null;

                const getRecentPattern = (idx, len) => {
                    let p = "";
                    for (let i = 1; i <= len; i++) {
                        p += history[idx + i].size === 'Big' ? 'B' : 'S';
                    }
                    return p;
                };

                // Define patterns to follow
                const patterns = [
                    { seq: "BBS", next: "B", name: "2-1 Big" },
                    { seq: "SSB", next: "S", name: "2-1 Small" },
                    { seq: "BBSS", next: "B", name: "2-2 Big" },
                    { seq: "SSBB", next: "S", name: "2-2 Small" },
                    { seq: "BBBS", next: "B", name: "3-1 Big" },
                    { seq: "SSSB", next: "S", name: "3-1 Small" },
                    { seq: "BBBSS", next: "B", name: "3-2 Big" },
                    { seq: "SSSBB", next: "S", name: "3-2 Small" },
                    { seq: "BBBSSS", next: "B", name: "3-3 Big" },
                    { seq: "SSSBBB", next: "S", name: "3-3 Small" },
                    { seq: "BBBBS", next: "B", name: "4-1 Big" },
                    { seq: "SSSSB", next: "S", name: "4-1 Small" },
                    { seq: "BBBBSS", next: "B", name: "4-2 Big" },
                    { seq: "SSSSBB", next: "S", name: "4-2 Small" },
                    { seq: "BBBBSSS", next: "B", name: "4-3 Big" },
                    { seq: "SSSSBBB", next: "S", name: "4-3 Small" },
                    { seq: "BBBBSSSS", next: "B", name: "4-4 Big" },
                    { seq: "SSSSBBBB", next: "S", name: "4-4 Small" },
                    { seq: "BBBBBS", next: "B", name: "5-1 Big" },
                    { seq: "SSSSSBB", next: "S", name: "5-1 Small" }
                ];

                for (const p of patterns) {
                    const len = p.seq.length;
                    if (getRecentPattern(targetIndex, len) === p.seq) {
                        // Logic "Gãy": Check history if this pattern usually breaks now
                        let followCount = 0;
                        let breakCount = 0;
                        const searchLimit = Math.min(history.length - len - 1, 1000);

                        for (let i = targetIndex + 1; i < searchLimit; i++) {
                            if (getRecentPattern(i, len) === p.seq) {
                                if (history[i].size[0] === p.next) followCount++;
                                else breakCount++;
                            }
                        }

                        // If historical breakage is high at this point, predict the break
                        if (breakCount > followCount && breakCount > 2) {
                            return { pred: p.next === 'B' ? 'Small' : 'Big', occurrences: followCount + breakCount, name: p.name, isBreak: true };
                        }
                        return { pred: p.next === 'B' ? 'Big' : 'Small', occurrences: followCount + breakCount, name: p.name, isBreak: false };
                    }
                }
                return null;
            }
        }
    };

    function getEnsemblePrediction(history, targetIndex) {
        if (history.length < 5) return null;
        const weights = {};
        const isMainPred = targetIndex === -1;

        // ANALYSIS DEPTH: Consistent depth for deterministic results
        const trainStart = targetIndex + 1;
        const trainLen = 1000; // Updated to 1000 sessions for deeper learning
        const testRange = Math.min(history.length - trainStart - 2, trainLen);

        // RELAX for Historical Table: If tiny history, don't just fail.
        if (!isMainPred && testRange < 1) {
            for (const key in Strategies) {
                const p = Strategies[key].predict(history, targetIndex);
                if (p) return { pred: p, confidence: 51, bestStrat: Strategies[key].name, bestScore: 0.5, details: "Baseline" };
            }
            return null;
        }
        if (testRange < 1) return null;

        for (const key in Strategies) {
            let correct = 0; let attempted = 0; let weightedScore = 0;
            for (let i = trainStart; i < trainStart + testRange; i++) {
                const p = Strategies[key].predict(history, i);
                if (p) {
                    attempted++;
                    const recency = 1 / (1 + (i - trainStart) * 0.02);
                    if (p === history[i].size) { correct++; weightedScore += recency; }
                    else weightedScore -= recency * 0.8;
                }
            }
            const finalScore = attempted > 0 ? Math.max(0, Math.min(1, 0.5 + (weightedScore / attempted))) : 0.5;
            weights[key] = {
                score: finalScore,
                rawAcc: attempted > 0 ? (correct / attempted) : 0,
                games: attempted,
                accPct: Math.round((attempted > 0 ? (correct / attempted) : 0) * 100)
            };
        }

        // MARKET ENTROPY (Chaos Detection)
        let entropy = 0;
        const entDepth = 20; // Fixed depth for entropy
        let changes = 0;
        const entMax = Math.min(trainStart + entDepth - 1, history.length - 1);
        for (let i = trainStart; i < entMax; i++) if (history[i].size !== history[i + 1].size) changes++;
        entropy = changes / entDepth;

        let bigVote = 0; let smallVote = 0; let bestStrat = "None"; let bestStratScore = -1; let details = "";
        const topStrats = [];

        const scoreThreshold = 0.52; // UNIFIED THRESHOLD for total sync <!-- sync_fix -->
        for (const key in Strategies) {
            const weight = weights[key];
            if (weight.score < scoreThreshold) continue;

            const prediction = Strategies[key].predict(history, targetIndex);
            if (prediction) {
                const impact = Math.pow(weight.score, 4);
                const predValue = typeof prediction === 'object' ? prediction.pred : prediction;

                if (predValue === 'Big') bigVote += impact; else smallVote += impact;
                topStrats.push({ name: Strategies[key].name, score: weight.score, meta: prediction });

                if (isMainPred) {
                    let text = `${Strategies[key].name} (${Math.round(weight.rawAcc * 100)}%)`;
                    if (prediction.occurrences) text += ` [Xuất hiện ${prediction.occurrences} lần]`;
                    details += text + "; ";
                }

                if (weight.score > bestStratScore) {
                    bestStratScore = weight.score;
                    bestStrat = Strategies[key].name;
                    if (typeof prediction === 'object') currentAiPredictionMeta = prediction;
                }
            }
        }

        if (bigVote === 0 && smallVote === 0) {
            // DEEP FALLBACK: Pick the absolute best performing strategy regardless of threshold
            let bestAnyScore = -1;
            let fallbackPred = null;
            let fallbackStrat = "None";
            for (const key in Strategies) {
                const w = weights[key];
                const p = Strategies[key].predict(history, targetIndex);
                if (p && w.score > bestAnyScore) {
                    bestAnyScore = w.score;
                    fallbackPred = p;
                    fallbackStrat = Strategies[key].name;
                }
            }
            if (fallbackPred) return {
                pred: fallbackPred, confidence: 50, bestStrat: fallbackStrat,
                bestScore: bestAnyScore, details: "Fast Fallback (Low Conf)"
            };
            return null;
        }
        const totalVotes = bigVote + smallVote;
        const confRatio = Math.max(bigVote, smallVote) / totalVotes;

        let finalConf = 52 + (confRatio * 48);
        const chaosFactor = 1 - Math.abs(0.5 - entropy);
        finalConf *= (0.65 + (chaosFactor * 0.35));

        if (topStrats.length >= 3) {
            topStrats.sort((a, b) => b.score - a.score);
            if (topStrats[0].score > 0.8 && topStrats[1].score > 0.7) finalConf += 5;
        }

        return {
            pred: bigVote > smallVote ? 'Big' : 'Small',
            confidence: Math.round(Math.min(finalConf, 99)),
            bestStrat: bestStrat,
            bestScore: bestStratScore,
            details: details
        };
    }

    function evaluateAutoBet(predictionResult) {
        console.log("[AutoBet] Evaluating...");
        const $toggle = $('#autoBetToggle');
        const isChecked = $toggle.is(':checked');

        if (!isChecked) {
            console.log("[AutoBet] Toggle is OFF");
            $('#autoBetStatus').text('OFF').removeClass('bg-success bg-danger').addClass('bg-secondary');
            return;
        }

        $('#autoBetStatus').text('ON').removeClass('bg-secondary bg-danger').addClass('bg-success');

        // 1. Config
        const baseAmount = parseInt($('#baseAmountInput').val()) || 1000;
        const configStr = $('#martingaleConfig').val() || "2,4,8,19,40,90";
        const multipliers = configStr.split(',').map(x => parseInt(x.trim())).filter(x => !isNaN(x));
        if (multipliers.length === 0) multipliers.push(1);

        // 2. Check Previous Win/Loss (Use ACTUAL BET data)
        const completedGame = fullGameHistory[0];
        console.log(`[AutoBet] Completed Game Issue: ${completedGame.issue}, Size: ${completedGame.size}`);

        if (lastBetIssue === completedGame.issue) {
            // Capture for Bot Notification before we overwrite lastBetIssue
            lastFinishedBetIssue = lastBetIssue;
            lastFinishedBetAmount = lastBetAmount;

            const isWin = lastBetType === completedGame.size;
            if (isWin) {
                if (martingaleStep !== 0) console.log("[AutoBet] WIN detected on bet! Resetting Martingale.");
                martingaleStep = 0;
                winStreak++;
                consecutiveLosses = 0;
            } else {
                console.log("[AutoBet] LOSS detected on bet. Increasing Martingale.");
                martingaleStep++;
                consecutiveLosses++;

                if (martingaleStep >= multipliers.length) {
                    martingaleStep = 0;
                    winStreak = 0;
                }

                if (consecutiveLosses >= 3) {
                    console.log("[AutoBet] 3 consecutive losses! Pausing for 3 minutes.");
                    pauseUntil = Date.now() + (3 * 60 * 1000);
                    consecutiveLosses = 0;
                    isInRecoveryMode = true;
                    martingaleStep = 0; // Reset as requested

                    // UI Alert
                    showStatusBadge("Paused: 3 Losses", "warning");
                }
            }
            // Clear used bet tracking to prevent double-processing same game
            lastBetIssue = "";
        }

        // 2.1 Target Profit Check
        const rawBalance = $('#uiAmount').text().replace(/[^0-9]/g, '');
        const currentBalance = parseInt(rawBalance) || 0;
        targetProfit = parseInt($targetProfitInput.val()) || 0;

        if (targetProfit > 0) {
            if (startingBalance === 0) startingBalance = currentBalance;
            const profit = currentBalance - startingBalance;

            $('#profitStatus').show();
            $('#currentProfitVal').text(profit.toLocaleString() + ' đ');
            $('#targetProfitVal').text(targetProfit.toLocaleString() + ' đ');

            if (profit >= targetProfit) {
                console.log("[AutoBet] Target Profit Reached! Stopping.");
                $toggle.prop('checked', false).trigger('change');
                startingBalance = 0;
                return;
            }
        } else {
            $('#profitStatus').hide();
        }

        // 2.2 Pause & Recovery Check
        if (pauseUntil) {
            const now = Date.now();
            if (now < pauseUntil) {
                const remains = Math.ceil((pauseUntil - now) / 1000);
                $('#autoBetStatus').text(`PAUSED (${remains}s)`).addClass('bg-warning text-dark');
                return;
            } else {
                if (isInRecoveryMode) {
                    // Check if the latest result (completedGame) was a win for our AI
                    const historicalAI = completedGame.aiGuess;
                    if (historicalAI && historicalAI !== '-') {
                        const historicalWin = historicalAI === completedGame.size;
                        if (historicalWin) {
                            console.log("[AutoBet] Recovery win detected! Resuming.");
                            isInRecoveryMode = false;
                            pauseUntil = null;
                            martingaleStep = 0;
                        } else {
                            $('#autoBetStatus').text("WAITING WIN").addClass('bg-warning text-dark');
                            return;
                        }
                    } else {
                        return; // Wait for one game with prediction
                    }
                } else {
                    pauseUntil = null;
                }
            }
        }

        // 3. Use passed prediction OR late-bound global
        const nextPred = predictionResult || currentAiPrediction;
        if (!nextPred) {
            console.warn("[AutoBet] AI skipped: No prediction available yet.");
            return;
        }

        // DOUBLE-CHECK UI SYNC: Ensure what we are betting matches what is displayed
        const uiDisplayedPred = $('#aiPredictSize').text().trim();
        if (uiDisplayedPred && nextPred.pred !== uiDisplayedPred) {
            console.error(`[AutoBet] CRITICAL SYNC ERROR: AI Result (${nextPred.pred}) != UI Display (${uiDisplayedPred}). Aborting bet.`);
            $('#aiStatusBadge').text("SYNC ERROR").addClass('bg-danger');
            return;
        }

        console.log(`[AutoBet] Using prediction: ${nextPred.pred} (Conf: ${nextPred.confidence || '?'}) Source: ${predictionResult ? 'Direct' : 'Global'}`);

        // 4. Check Duplicate
        let nextIssue = "Unknown";
        try { nextIssue = (BigInt(completedGame.issue) + 1n).toString(); } catch { nextIssue = completedGame.issue + "+"; }

        if (lastBetIssue === nextIssue) {
            console.log(`[AutoBet] Already bet on issue ${nextIssue}. Waiting for result...`);
            return;
        }

        // 5. Calculate Amount
        const multiplier = multipliers[martingaleStep] || 1;
        const betAmount = baseAmount * multiplier;

        // 6. Send Request
        console.log(`[AutoBet] EXECUTING REQUEST: ${nextPred.pred} on ${nextIssue} (Step: ${martingaleStep})`);

        // CRITICAL SYNC: Mark as bet IMMEDIATELY to prevent race conditions
        lastBetIssue = nextIssue;
        lastBetType = nextPred.pred;
        lastBetAmount = betAmount;

        $.ajax({
            url: '/api/browser/bet',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ type: nextPred.pred.trim(), amount: betAmount }),
            success: function () {
                console.log(`[AutoBet] API SUCCESS: Bet confirmed for ${nextIssue}`);
                const toast = `<div class="position-fixed top-0 end-0 p-3" style="z-index: 9999">
                    <div class="toast show bg-dark text-white" role="alert">
                        <div class="toast-body">
                           🤖 AutoBet: ${nextPred.pred} - ${betAmount.toLocaleString()}đ (Issue ${nextIssue})
                        </div>
                    </div>
                 </div>`;
                $('body').append(toast);
                setTimeout(() => $('.toast').remove(), 3000);
            },
            error: function (err) {
                console.error("AutoBet Fail", err);
                $('#aiReasoning').append(` [Bet Error: ${err.statusText}]`);
                alert("AutoBet Failed. Check Console.");
            }
        });
    }

    function updateAIPrediction() {
        const result = getEnsemblePrediction(fullGameHistory, -1);
        currentAiPrediction = result; // SYNC SOURCE

        // Update Next Issue UI
        const latest = fullGameHistory[0];
        if (!latest) return;

        let nextIssue = "Unknown";
        try { nextIssue = (BigInt(latest.issue) + 1n).toString(); } catch { nextIssue = latest.issue + "+"; }
        $('#aiNextIssue').text(nextIssue);

        if (!result) {
            $('#aiStatusBadge').text("Analyzing...");
            $('#aiReasoning').text("Not enough clear patterns.");
            return;
        }

        const finalPred = result.pred;
        const predParity = latest.parity === 'Double' ? 'Single' : 'Double';

        $('#aiPredictSize').text(finalPred).removeClass('text-danger text-success').addClass(finalPred === 'Big' ? 'text-danger' : 'text-success');
        $('#aiPredictParity').text(predParity).removeClass('text-danger text-success').addClass(predParity === 'Double' ? 'text-danger' : 'text-success');
        $('#aiConfidenceBar').css('width', result.confidence + '%').text(result.confidence + '%');

        const isFallback = result.details.includes("Fallback");
        let summary = `${isFallback ? '⚠️ Low Conf: ' : ''}Trusting ${result.bestStrat}`;

        if (currentAiPredictionMeta && currentAiPredictionMeta.occurrences) {
            summary += ` (${currentAiPredictionMeta.occurrences} lần)`;
        }

        $('#aiReasoning').text(summary).attr('title', result.details);

        if (isFallback) {
            $('#aiStatusBadge').text("Low Conf / Fallback").removeClass('bg-primary').addClass('bg-warning text-dark');
        } else {
            $('#aiStatusBadge').text("Self-Learning Active").removeClass('bg-dark bg-warning text-dark').addClass('bg-primary text-white');
        }

        // SAVE PREDICTION SNAPSHOT
        if (result && nextIssue !== "Unknown") {
            // Normalize Key
            const key = String(nextIssue).trim();
            pendingPredictions[key] = {
                pred: result.pred,
                confidence: result.confidence,
                strat: result.bestStrat,
                ts: Date.now()
            };

            // SYNC TO SERVER FOR BOT
            connection.invoke("SendClientPrediction", String(nextIssue).trim(), result.pred).catch(err => console.error(err));
            // Limit pending size (keep last 50)
            const keys = Object.keys(pendingPredictions);
            if (keys.length > 50) delete pendingPredictions[keys[0]];

            localStorage.setItem('dropAI_pending_preds', JSON.stringify(pendingPredictions));
        }

        // TRIGGER AUTO BET with the SAME prediction result
        evaluateAutoBet(result);
    }



    function renderHistoryTable() {
        const $tableBody = $('#gameHistoryBody');
        const $card = $('#gameHistoryCard');
        const $cardHeader = $card.find('.card-header');
        const $sessionContainer = $('#aiSessionHistory');

        $tableBody.empty();
        $card.show();

        // 1. PROCESS AI HISTORY (Beads D/S Logic)
        const calcDepth = Math.min(fullGameHistory.length, 100);
        const resultsMap = {};

        let beadList = []; // Stores 'D', 'S', '1', '2'
        let currentMisses = 0;

        // Loop Oldest -> Newest to build the bead chain
        for (let i = calcDepth - 1; i >= 0; i--) {
            // Check if we already have a prediction for this row
            let prediction = fullGameHistory[i].aiGuess;
            if (!prediction || prediction === '-') {
                const aiResult = getEnsemblePrediction(fullGameHistory, i);
                prediction = aiResult ? aiResult.pred : null;
            }

            if (!prediction) continue;

            const actual = fullGameHistory[i].size;
            const isWin = prediction === actual;

            if (isWin) {
                // WIN implies the whole session is Correct (D)
                beadList.push('D');
                currentMisses = 0; // Reset cycle
            } else {
                // LOSS
                currentMisses++;
                if (currentMisses === 3) {
                    // 3 strikes -> Session Failed (S)
                    beadList.push('S');
                    currentMisses = 0; // Reset cycle
                }
            }
        }

        // Handle the "Tail" (Current incomplete session)
        if (currentMisses > 0) {
            beadList.push(currentMisses.toString()); // "1" or "2"
        }

        // 2. RENDER BEADS
        const showBeads = beadList.slice(-10);
        $sessionContainer.empty();

        let dCount = 0;
        let sCount = 0;

        showBeads.forEach(b => {
            let cls = 'bg-secondary';
            let txt = b;

            if (b === 'D') {
                cls = 'bg-primary text-white';
                dCount++;
            }
            else if (b === 'S') {
                cls = 'bg-danger text-white fw-bold shadow-sm';
                txt = '<i class="bi bi-x-lg"></i>';
                sCount++;
            }
            else {
                cls = 'bg-warning text-dark border border-dark';
            }

            const bead = `<span class="badge ${cls} rounded-pill m-1 shadow-sm d-flex align-items-center justify-content-center" 
                                style="width: 30px; height: 30px; font-size: 0.85rem;">${txt}</span>`;
            $sessionContainer.append(bead);
        });

        // 3. STATS HEADER
        const totalClosedSessions = dCount + sCount;
        if (totalClosedSessions > 0) {
            const rate = Math.round((dCount / totalClosedSessions) * 100);
            const statsBadge = `<span class="badge bg-warning text-dark me-2 border border-dark ds-stats-badge">🎯 D/S Rate: ${rate}% (${dCount}/${totalClosedSessions})</span>`;
            $cardHeader.find('.ds-stats-badge').remove();
            $cardHeader.prepend(statsBadge);
        }

        // 4. RENDER TABLE (Show stored predictions first)
        const displayList = fullGameHistory.slice(0, 500);
        displayList.forEach((item, index) => {
            let aiGuess = item.aiGuess || '-';
            let aiResult = item.aiResult || '-';

            // If missing in record but in last 100 rows, try to calculate for visual (backfill)
            if (index < 100 && (aiGuess === '-' || !aiGuess)) {
                const p = getEnsemblePrediction(fullGameHistory, index);
                if (p) {
                    aiGuess = p.pred;
                    const isWin = aiGuess === item.size;
                    aiResult = isWin ? 'Thắng' : 'Thua';
                }
            }

            const aiBadge = `<span class="badge bg-${aiGuess === 'Big' ? 'danger' : (aiGuess === 'Small' ? 'success' : 'secondary')} bg-opacity-75">${aiGuess}</span>`;
            const resBadge = `<span class="badge bg-${aiResult === 'Thắng' ? 'success' : (aiResult === 'Thua' ? 'danger' : 'secondary')}">${aiResult}</span>`;

            const row = `
                <tr>
                    <td>${item.issue}</td>
                    <td><span class="fw-bold text-primary fs-5">${item.number}</span></td>
                     <td><span class="badge ${item.size === 'Big' ? 'bg-danger' : 'bg-success'}">${item.size}</span></td>
                     <td><span class="badge ${item.parity === 'Double' ? 'bg-danger' : 'bg-success'}">${item.parity}</span></td>
                     <td>${aiBadge}</td>
                     <td>${resBadge}</td>
                </tr>
             `;
            $tableBody.append(row);
        });
    }

    connection.start().catch(err => console.error("SignalR: ", err));

    $searchInput.on('input', function () {
        const query = $(this).val().toLowerCase();
        $trafficTableBody.children('.traffic-row').each(function () {
            const data = requestsMap[$(this).data('id')];
            if (data) $(this).toggle(JSON.stringify(data).toLowerCase().includes(query));
        });
    });

    $trafficTableBody.on('click', '.traffic-row', function () {
        $trafficTableBody.find('.table-active').removeClass('table-active');
        $(this).addClass('table-active');
        const data = requestsMap[$(this).data('id')];
        if (data) {
            $detailsCode.text(JSON.stringify(data, null, 2));
        }
    });

    $btnClearLog.click(function () {
        $trafficTableBody.empty();
        requestsMap = {};
        $detailsCode.text('Select a request to view details...');
    });

    function updateStatusUI(status) {
        $statusBadge.text(status);
        const cls = status === 'Running' || status === 'Logged In' ? 'bg-success' : (status.startsWith('Error') ? 'bg-danger' : 'bg-secondary');
        $statusBadge.removeClass('bg-success bg-danger bg-secondary').addClass(cls);
    }

    function getMethodClass(m) { return !m ? 'bg-secondary' : (m.toUpperCase() == 'GET' ? 'bg-primary' : (m.toUpperCase() == 'POST' ? 'bg-success' : 'bg-secondary')); }
    function getStatusClass(s) { return (!s) ? 'bg-secondary' : (s >= 200 && s < 300 ? 'bg-success' : (s >= 400 ? 'bg-danger' : 'bg-warning text-dark')); }

    $btnPlay.click(function () {
        const url = $urlInput.val();
        if (!url) return alert('Enter URL');
        updateStatusUI('Launching...');
        $.ajax({
            url: '/api/browser/start', type: 'POST', contentType: 'application/json',
            data: JSON.stringify({ url: url, username: $('#usernameInput').val(), password: $('#passwordInput').val() }),
            error: xhr => alert('Start Fail: ' + xhr.responseText)
        });
    });

    $btnStop.click(() => $.post('/api/browser/stop').fail(xhr => alert('Stop Fail: ' + xhr.responseText)));

    // RESET ALL BUTTON - Clear all data and reload
    $('#btnResetAll').click(function () {
        if (!confirm('⚠️ XÓA TOÀN BỘ dữ liệu?\n\n- Lịch sử game\n- Cấu hình Auto Bet\n- Session hiện tại\n\nWeb sẽ khởi động lại như mới. Tiếp tục?')) {
            return;
        }

        console.log('[Reset] Clearing all localStorage data...');

        // Clear all localStorage keys
        localStorage.removeItem('dropAI_history');
        localStorage.removeItem('dropAI_baseAmount');
        localStorage.removeItem('dropAI_martingale');

        // In case there are other keys, clear everything with dropAI prefix
        Object.keys(localStorage).forEach(key => {
            if (key.startsWith('dropAI_')) {
                localStorage.removeItem(key);
            }
        });

        console.log('[Reset] All data cleared. Reloading page...');

        // Reload page to reset everything
        location.reload();
    });

    // Switch Game Logic via Event Delegation (More Robust)
    $('#gameCategoriesContainer').on('click', '.game-card', function () {
        // Visual feedback
        $('.game-card').removeClass('border-warning bg-light');
        $(this).addClass('border-warning bg-light');

        const id = $(this).data('id');
        if (!id || id === '?') {
            alert("Invalid Game ID");
            return;
        }

        // --- CLEAR HISTORY ON SWITCH ---
        fullGameHistory = [];
        $('#gameHistoryBody').empty();
        $('#aiSessionHistory').html('<span class="text-muted small fst-italic">Waiting for new game data...</span>');
        $('#aiStatusBadge').text('Waiting...');
        $('#aiReasoning').text('');
        localStorage.removeItem('dropAI_history'); // Clear persistence for fresh start
        renderHistoryTable(); // Refresh UI (empty)

        switchGame(id);
    });

    function switchGame(id) {
        // Map IDs to XPaths (provided by user)
        const xpathMap = {
            30: '/html/body/div[1]/div[2]/div[4]/div[1]/div', // 30s
            1: '/html/body/div[1]/div[2]/div[4]/div[2]/div',  // 1m
            2: '/html/body/div[1]/div[2]/div[4]/div[3]/div',  // 3m
            3: '/html/body/div[1]/div[2]/div[4]/div[4]/div'   // 5m
        };

        const xpath = xpathMap[id];

        if (xpath) {
            updateStatusUI(`Clicking Game ID ${id}...`);
            $.ajax({
                url: '/api/browser/click',
                type: 'POST',
                contentType: 'application/json',
                data: JSON.stringify({ url: xpath }),
                error: xhr => alert('Click Fail: ' + xhr.responseText)
            });
        } else {
            const targetUrl = `https://387vn.com/#/home/AllLotteryGames/WinGo?id=${id}`;
            updateStatusUI(`Navigating to ID ${id}...`);
            $.ajax({
                url: '/api/browser/navigate',
                type: 'POST',
                contentType: 'application/json',
                data: JSON.stringify({ url: targetUrl }),
                error: xhr => alert('Navigate Fail: ' + xhr.responseText)
            });
        }
    }

    // CRITICAL: Fill AI predictions for loaded history on page load
    fillAIPredictions();
    if (fullGameHistory.length > 0) {
        renderHistoryTable();
    }
    // START CONNECTION
    connection.start().catch(err => console.error(err));
});
