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
                        item.aiResult = prediction.pred === item.size ? 'HIT' : 'MISS';
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
        localStorage.setItem('dropAI_baseAmount', $(this).val());
        console.log("[Config] Saved Base Amount:", $(this).val());
    });

    $martingaleConfig.on('change', function () {
        localStorage.setItem('dropAI_martingale', $(this).val());
        console.log("[Config] Saved Martingale Values:", $(this).val());
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

                        // Calculate AI prediction using ALL history BEFORE this game
                        const prediction = getEnsemblePrediction(fullGameHistory, index);

                        if (prediction) {
                            item.aiGuess = prediction.pred;

                            // Verify if we have actual result
                            if (item.size && item.size !== '-') {
                                item.aiResult = prediction.pred === item.size ? 'HIT' : 'MISS';
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
                }
            }

        } catch (e) {
            console.error("Error parsing game history", e);
        }
    });

    function renderHistoryTable() {
        const $tableBody = $('#gameHistoryBody');
        const $card = $('#gameHistoryCard');
        const $cardHeader = $card.find('.card-header');

        $tableBody.empty();
        $card.show();

        // Update Header with Count
        // If "Game History" text is there, replace/append count
        const countBadge = `<span class="badge bg-light text-dark float-end">${fullGameHistory.length} Records</span>`;
        if ($cardHeader.find('.badge').length > 0) $cardHeader.find('.badge').replaceWith(countBadge);
        else $cardHeader.append(countBadge);

        // Show massive amount of rows (2000) as requested "Unlimited"
        // Virtualization needed for tens of thousands, but 2000 is fine for now on modern PCs.
        const displayList = fullGameHistory.slice(0, 2000);

        displayList.forEach((item, index) => {
            // Use STORED AI prediction (not recalculating)
            const aiGuess = item.aiGuess || '-';
            const aiResult = item.aiResult || '-';

            const row = `
                <tr>
                    <td>${item.issue}</td>
                    <td><span class="fw-bold text-primary fs-5">${item.number}</span></td>
                     <td><span class="badge ${item.size === 'Big' ? 'bg-danger' : 'bg-success'}">${item.size}</span></td>
                     <td><span class="badge ${item.parity === 'Double' ? 'bg-danger' : 'bg-success'}">${item.parity}</span></td>
                     <td><span class="badge ${aiGuess === 'Big' ? 'bg-danger' : (aiGuess === 'Small' ? 'bg-success' : 'bg-secondary')}">${aiGuess}</span></td>
                     <td><span class="badge ${aiResult === 'HIT' ? 'bg-success' : (aiResult === 'MISS' ? 'bg-danger' : 'bg-secondary')}">${aiResult}</span></td>
                </tr>
             `;
            $tableBody.append(row);
        });
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

        // Strategy 4: Deep Pattern Search
        PatternSearch: {
            name: "Historical Pattern",
            predict: function (history, targetIndex) {
                if (targetIndex >= history.length - 6) return null;
                const patternLen = 5;
                let signature = "";
                for (let i = 1; i <= patternLen; i++) {
                    signature += history[targetIndex + i].size[0];
                }
                let bigNext = 0;
                let smallNext = 0;
                let matches = 0;
                for (let i = targetIndex + 2; i < history.length - patternLen; i++) {
                    let match = true;
                    for (let k = 0; k < patternLen; k++) {
                        if (history[i + k].size[0] !== signature[k]) {
                            match = false;
                            break;
                        }
                    }
                    if (match) {
                        const nextResult = history[i - 1];
                        if (nextResult) {
                            if (nextResult.size === 'Big') bigNext++;
                            else smallNext++;
                            matches++;
                        }
                    }
                }
                if (matches < 3) return null;
                const ratio = bigNext / (bigNext + smallNext);
                if (ratio > 0.65) return 'Big';
                if (ratio < 0.35) return 'Small';
                return null;
            }
        }
    };

    function getEnsemblePrediction(history, targetIndex) {
        if (history.length < 5) return null; // Reduced from 10 to 5
        const weights = {};
        const isMainPred = targetIndex === -1;

        // CORRECTION: User requested "Use All History (Max 500)".
        // Since we now clamp history at 500, we can safely allow the AI 
        // to scan as much as possible without lagging too much.
        const trainStart = targetIndex + 1;

        // If Main Prediction, look at EVERYTHING available (up to 500).
        // If History Table row, increased from 50 to 100 for better coverage
        const trainLen = isMainPred ? 450 : 100;

        const testRange = Math.min(history.length - trainStart - 2, trainLen);

        if (testRange < 3) return null; // Reduced from 5 to 3

        for (const key in Strategies) {
            let correct = 0;
            let attempted = 0;
            for (let i = trainStart; i < trainStart + testRange; i++) {
                const p = Strategies[key].predict(history, i);
                const a = history[i].size;
                if (p) {
                    attempted++;
                    if (p === a) correct++;
                }
            }
            const acc = attempted > 0 ? (correct / attempted) : 0;
            weights[key] = { score: acc, games: attempted, accPct: Math.round(acc * 100) };
        }

        let bigVote = 0;
        let smallVote = 0;
        let bestStrat = "None";
        let bestStratScore = -1;
        let details = "";

        for (const key in Strategies) {
            const strat = Strategies[key];
            const weight = weights[key];
            if (weight.games < 3 || weight.score < 0.45) continue;
            const prediction = strat.predict(history, targetIndex);
            if (prediction) {
                const impact = weight.score;
                if (prediction === 'Big') bigVote += impact;
                else smallVote += impact;
                if (isMainPred) details += `${strat.name} (${weight.accPct}%); `;
                if (weight.score > bestStratScore) {
                    bestStratScore = weight.score;
                    bestStrat = strat.name;
                }
            }
        }

        const conf = Math.max(bigVote, smallVote) / (bigVote + smallVote || 1);
        if (bigVote === 0 && smallVote === 0) return null;

        return {
            pred: bigVote > smallVote ? 'Big' : 'Small',
            confidence: Math.round(Math.min(50 + (conf * 50), 98)),
            bestStrat: bestStrat,
            bestScore: bestStratScore,
            details: details
        };
    }

    function evaluateAutoBet() {
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
        const configStr = $('#martingaleConfig').val() || "1,2,3,8,16";
        const multipliers = configStr.split(',').map(x => parseInt(x.trim())).filter(x => !isNaN(x));
        if (multipliers.length === 0) multipliers.push(1);

        // 2. Check Previous Win/Loss (if History allows)
        if (fullGameHistory.length < 2) {
            console.log("[AutoBet] Not enough history (<2)");
            return;
        }

        const completedGame = fullGameHistory[0];
        console.log(`[AutoBet] Completed Game Issue: ${completedGame.issue}, Size: ${completedGame.size}`);

        const prevPred = getEnsemblePrediction(fullGameHistory, 0); // Re-run for index 0

        // Logic: Did we bet on the completed game?
        if (prevPred) {
            const isWin = prevPred.pred === completedGame.size;
            if (isWin) {
                if (martingaleStep !== 0) console.log("[AutoBet] Win detected, resetting Martingale.");
                martingaleStep = 0; // Reset
            } else {
                if (martingaleStep < multipliers.length) console.log("[AutoBet] Loss detected, increasing Martingale.");
                martingaleStep++;
                if (martingaleStep >= multipliers.length) martingaleStep = 0;
            }
        }

        // 3. Predict NEXT
        const nextPred = getEnsemblePrediction(fullGameHistory, -1);
        if (!nextPred) {
            console.log("[AutoBet] AI skipped: Low confidence or no pattern.");
            $('#aiReasoning').append(" [AutoBet: Skipped (Low Conf)]");
            return;
        }

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
        // Update UI
        console.log(`[AutoBet] EXECUTING BET: ${nextPred.pred} on ${nextIssue} amount ${betAmount}`);
        $('#aiReasoning').append(` [AutoBet: ${nextPred.pred} ${betAmount}]`);

        $.ajax({
            url: '/api/browser/bet',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ type: nextPred.pred, amount: betAmount }),
            success: function () {
                lastBetIssue = nextIssue;
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

        const summary = `Trusting: ${result.bestStrat} (${Math.round(result.bestScore * 100)}% acc).`;
        $('#aiReasoning').text(summary).attr('title', result.details);
        $('#aiStatusBadge').text("Self-Learning Active").removeClass('bg-dark text-warning').addClass('bg-primary text-white');

        // TRIGGER AUTO BET
        evaluateAutoBet();
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
            const aiResult = getEnsemblePrediction(fullGameHistory, i);

            // If no prediction possible, skip or handle? 
            // Ideally we need continuous data. If we can't predict, we assume NO BET (Skip).
            if (!aiResult) continue;

            const actual = fullGameHistory[i].size;
            const isWin = aiResult.pred === actual;

            if (isWin) {
                // WIN implies the whole session is Correct (D)
                // Regardless if it was step 1, 2, or 3.
                beadList.push('D');
                currentMisses = 0; // Reset cycle
            } else {
                // LOSS
                currentMisses++;
                if (currentMisses === 3) {
                    // 3 strikes -> Session Failed (S)
                    beadList.push('S');
                    currentMisses = 0; // Reset cycle
                } else {
                    // Pending misses (1 or 2). 
                    // IMPORTANT: We do NOT push to beadList yet.
                    // Because if next one is Win, this 'Pending' disappears and becomes 'D'.
                    // These are only visible if we run out of history (End of Loop).
                }
            }

            // Store for table
            // For table, we still want to show specific result per row
            if (isWin) {
                resultsMap[i] = { type: 'win', step: currentMisses + 1 }; // currentMisses was just reset to 0, so this logic is tricky. 
                // Actually, if isWin, it was a win at step (pre-reset misses + 1).
                // But we reset it instantly.
            } else {
                resultsMap[i] = { type: 'loss', step: currentMisses };
            }
        }

        // Handle the "Tail" (Current incomplete session)
        if (currentMisses > 0) {
            beadList.push(currentMisses.toString()); // "1" or "2"
        }

        // 2. RENDER BEADS
        // Show last 10 beads (User Request)
        const showBeads = beadList.slice(-10);
        $sessionContainer.empty();

        let dCount = 0;
        let sCount = 0;

        showBeads.forEach(b => {
            let cls = 'bg-secondary';
            let txt = b;

            if (b === 'D') {
                cls = 'bg-primary text-white'; // Correct 
                dCount++;
            }
            else if (b === 'S') {
                cls = 'bg-danger text-white fw-bold shadow-sm'; // Wrong
                txt = '<i class="bi bi-x-lg"></i>'; // Animation effect? Using icon for clear S
                sCount++;
            }
            else {
                // "1" or "2"
                cls = 'bg-warning text-dark border border-dark';
            }

            const bead = `<span class="badge ${cls} rounded-pill m-1 shadow-sm d-flex align-items-center justify-content-center" 
                                style="width: 30px; height: 30px; font-size: 0.85rem;">${txt}</span>`;
            $sessionContainer.append(bead);
        });

        // 3. STATS HEADER
        // Win Rate based on D vs S
        const totalClosedSessions = dCount + sCount; // Ignore pending 1/2
        if (totalClosedSessions > 0) {
            const rate = Math.round((dCount / totalClosedSessions) * 100);
            const statsBadge = `<span class="badge bg-warning text-dark me-2 border border-dark">🎯 D/S Rate: ${rate}% (${dCount}/${totalClosedSessions})</span>`;
            $cardHeader.find('.bg-warning').remove();
            $cardHeader.prepend(statsBadge);
        }

        // 4. RENDER TABLE
        const displayList = fullGameHistory.slice(0, 500);
        displayList.forEach((item, index) => {
            // Re-calc specific row details for visual debugging (optional) or keep simple
            // Using basic comparison for table rows to be raw truth
            // Note: bead logic aggregates rows, but table shows raw rows.

            // Check if we calculate prediction for this row
            let aiBadge = '<span class="text-muted small">-</span>';
            let resBadge = '<span class="text-muted small">-</span>';

            // Simple individual check for table context
            if (index < 50 && index < fullGameHistory.length - 10) {
                const p = getEnsemblePrediction(fullGameHistory, index);
                if (p) {
                    const isWin = p.pred === item.size;
                    aiBadge = `<span class="badge bg-${p.pred === 'Big' ? 'danger' : 'success'} bg-opacity-75">${p.pred}</span>`;

                    // For table, let's just show raw WIN/LOSS to correspond with row
                    const c = isWin ? 'success' : 'danger';
                    resBadge = `<span class="badge bg-${c}">${isWin ? 'OK' : 'MISS'}</span>`;
                }
            }

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
});
