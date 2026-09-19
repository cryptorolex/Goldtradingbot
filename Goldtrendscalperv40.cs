#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// =====================================================================
// GoldTrendScalperV40 (2026-09-18) - FastPeriod 9 → 5 (EMA(5)/EMA(21) on 1-min trigger).
// Based on V39. One variable change only; all else at V39 defaults.
// Baseline to beat: net $19,418, PF 1.93, 756 trades, maxDD -$903.
//
// V38 result (barBodyATR gate): REJECTED - net fell at every threshold
// (0.50 -> $18,526 / 1.91 / 735; 0.75 and 1.00 worse still). The V33-era
// barBodyATR separator (1.38 vs 1.01) does not persist into the post-
// MaxFullTakeAtrLong population. Both remaining V33-era candidates
// (DIspread, barBodyATR) are now exhausted. Long-side regime gate search
// complete; only MaxFullTakeAtrLong=8 survived. V38 deleted.
//
// FOMC CHANGE: added 2027 dates. VERIFY against federalreserve.gov -
// dates are estimated from the prior-year pattern. UseFomcBlackout stays
// default OFF for backtest parity; turn ON for live.
//
// =====================================================================
//
// (V37 header, retained below for continuity)
//
// =====================================================================
// GoldTrendScalperV37 (2026-09-18) - LIVE-SAFETY FIX. No trading-logic
// change: at defaults this MUST still reproduce V35/V36 exactly -
// net $19,418, PF 1.93, 756 trades, maxDD -$903, MinDiSpreadLong still 0.
//
// *** THIS FIXES A BUG THAT CAN SILENTLY STOP THE LIVE BOT FROM TRADING.
// Update any live/sim instance to V37; do not keep running V33-V36 live. ***
//
// WHAT HAPPENED (2026-09-18, real incident on this machine):
//   09:38:55  V35 enabled as a REAL-TIME strategy on APEX5995790000003.
//             Its StreamWriter holds GTS_debug_log.txt open for the whole
//             session.
//   11:56 / 11:58 / 12:04  Three Strategy Analyzer runs of V36 each died:
//             "Strategy 'GoldTrendScalperV36': Error on calling
//             'OnStateChange' method: The process cannot access the file
//             'C:\...\GTS_debug_log.txt' because it is being used by
//             another process."
//             Symptom in the Summary: 0 trades, $0.00, PF 1.00, on BOTH
//             sides - and no entry in GTS_debug_log.txt to explain it,
//             because the failure happened AT the line that opens the log.
//   12:06:43  Live strategy disabled -> file freed.
//
// The dangerous direction is the REVERSE of what happened: a backtest (or
// an editor, or OneDrive syncing this folder) holding that file when the
// live strategy is enabled would make the LIVE BOT FAIL TO START, with
// nothing in the Strategy Analyzer to notice and only a line in the
// NinjaTrader log to explain it. A logging convenience must never be able
// to take the bot offline. That is the whole justification for this
// version existing.
//
// THE TWO FIXES (both in State.DataLoaded):
//   1. SEPARATE FILE PER INSTANCE. Backtests keep GTS_debug_log.txt (every
//      analysis script and every prior header references that path).
//      Live/sim writes GTS_debug_log_<account>.txt - e.g.
//      GTS_debug_log_APEX5995790000003.txt - so a live session and a
//      backtest can never contend for the same handle. Account name is
//      sanitised against Path.GetInvalidFileNameChars(). The RUN START
//      line now also records which file it opened.
//   2. try/catch AROUND THE OPEN. Any IO failure now disables FILE logging
//      only: debugLogFile stays null, LogLine's existing null-guard makes
//      every call a plain Print(), and the strategy trades normally. It
//      prints one "file logging DISABLED" line saying why. State.Terminated
//      is likewise defensive, so a throw on shutdown can't surface as a
//      strategy error either.
//
// OPERATIONAL NOTE: because live now writes a per-account file, the
// analysis tooling and every "read GTS_debug_log.txt" instruction still
// points at BACKTEST output only, which is what those instructions have
// always assumed. To review live behaviour, read the per-account file.
// =====================================================================
//
// (V36 header, retained below for continuity)
//
// GoldTrendScalperV36 (2026-09-18) - ONE new test knob vs V35:
// MinDiSpreadLong, a LONG-ONLY directional-conviction gate. Default 0 =
// OFF, so a 31mo run as shipped MUST reproduce V35: net $19,418, PF 1.93,
// 756 trades, Long $6,650/1.90/303, Short $12,768/1.95/453, maxDD -$903.
//
// WHAT IT GATES ON: DI+ minus DI- on the 5-min series at the entry bar.
// This is a DIFFERENT dimension from MaxFullTakeAtrLong (confirmed at 8 in
// V35). That gate asks "given volatility, is the fixed-dollar FullTake
// even reachable?" - this one asks "is the move actually directional, or
// just wide?" Both are long-only; both can be active at once.
//
// WHY THIS ONE NEXT: in the V33-era entry-context analysis that produced
// the ATR gate, DIspread was the clear runner-up separator - FullTake
// longs median 33.4 vs 25.9 for the rest (n=116). barBodyATR was third
// (1.38 vs 1.01) and remains untested after this.
//
// *** SIZING IS PROVISIONAL - RE-MEASURE BEFORE SWEEPING. *** The
// 33.4/25.9 medians are from the PRE-ATR-gate population. V35's gate has
// since removed the lowest-ATR longs (310 -> 303 trade-rows), so the
// separation among the survivors may be weaker. Worse, if DIspread
// correlates with ATR the two gates overlap and the combined effect will
// be less than additive - the honest test is V36-with-DI-gate against
// V35, not against the old V33 numbers. Run once at MinDiSpreadLong = 0
// first: that both confirms the no-op AND regenerates a full 31mo
// GTS_debug_log.txt to re-size from. (The log currently on disk covers
// only 2026-06-05 to 2026-09-16 - a short run overwrote the full one.)
//
// PROVISIONAL SWEEP after re-sizing: 15 / 20 / 25 / 30.
// FALSIFIER: net falls with no PF gain and no drawdown improvement.
//
// ONE INTERACTION TO WATCH: Path A longs already require diBull
// (DI+ > DI-), so this is a magnitude floor on an already-true condition,
// not a new direction requirement. But Path B longs qualify via a DI
// CROSS and can legitimately show a small spread right at the crossover -
// Path B is a separately confirmed-profitable entry path (V10: long PF
// 1.45 -> 1.58). The block lines tag each blocked entry PathA/PathB;
// if this gate disproportionately kills PathB, that is a reason to
// reject it even if headline net improves.
// =====================================================================
//
// (V35 header, retained below for continuity)
//
// GoldTrendScalperV35 (2026-09-18) - CONFIRMED VALUE build. One change vs
// V34: MaxFullTakeAtrLong default 0 (off) -> 8.
//
// *** NEW CONFIRMED BASELINE - this file's defaults do NOT reproduce the
// old $18,854 figure, and that is intentional (first time since V31): ***
//   net $19,418, PF 1.93, 756 trades, maxDD -$903
//   Long  $6,650, PF 1.90, 303 trade-rows
//   Short $12,768, PF 1.95, 453 trade-rows  (UNCHANGED - gate is long-only,
//   identical short numbers across all nine sweep runs confirm that)
// Setting MaxFullTakeAtrLong = 0 reverts exactly to V33: $18,854 / 1.89 / 763.
//
// 31mo SWEEP (correct RTH template, one value at a time, everything else
// at V33 defaults). Implied ATR floor = 6.99 / threshold:
//   thresh   net      PF    long net  long PF  long rows  maxDD
//   off    $18,854   1.89    $6,086    1.78      310     -$915
//   10     $18,993   1.90    $6,225    1.82      304     -$903
//    9     $18,993   1.90    $6,225    1.82      304     -$903   (= 10 exactly)
//    8     $19,418   1.93    $6,650    1.90      303     -$903   <- CONFIRMED
//    7     $19,265   1.94    $6,497    1.90      284     -$903
//    6     $18,489   1.90    $5,721    1.80      259     -$1,046
//    4     $18,036   2.00    $5,268    2.15      192     -$1,020
//    3.5   $16,974   1.96    $4,206    2.00      154     -$1,070
//    3     $17,367   2.05    $4,599    2.46      134     -$892
//    2.5   $17,762   2.18    $4,994    4.13       99     -$892
//
// PER-ATR-BAND DECOMPOSITION. Each threshold blocks a strict superset of
// the one above it, so subtracting adjacent runs isolates each slice's
// contribution to long P&L:
//   ATR < 0.70      -$139
//   ATR 0.70-0.78     $0
//   ATR 0.78-0.87   -$425   <- ONE trade (largest single loss is -$417)
//   ATR 0.87-1.00   +$153
//   ATR 1.00-1.17   +$776   <- why 6 and below are net-negative
//
// HONEST READ OF THE CONFIRMED VALUE - do not oversell this in future
// sessions. Everything below ATR 1.00 sums to just -$411 across ~26
// trade-rows in 31 months, and the band signs alternate (-139 / 0 / -425 /
// +153), which is what noise around zero looks like, not a systematic
// edge. The -$425 slice separating 8 from 9 is a single max-size loser.
// Strip it and the sub-1.0-ATR longs are roughly break-even. So: 8 is
// adopted as a cheap SANITY FILTER - it blocks ~7 trade-rows in 31 months
// in setups where FullTake sits 8+ ATRs away and is realistically
// unreachable - not as a $564/period edge. The defensible forward
// expectation is nearer the +$139 that 9 and 10 produce. Every value from
// 7 to 10 is directionally positive on net, PF AND drawdown, which is why
// this is adopted at all rather than rejected.
//
// HARD FLOOR: never set below 7. The profitable ATR 1.00-1.17 band is the
// reason - and this also independently re-confirms the V15-era finding
// that a long ATR floor up at ~2.0 destroys value (threshold 3.5 = ATR
// floor 2.00 is the worst net in the entire sweep).
//
// PF-vs-net tradeoff, if priorities ever change: the tight end buys
// profit factor by selling net profit. 2.5 gives long PF 4.13 and the best
// long-side drawdown (-$665) for -$1,092 of net. NOT adopted - the stated
// goal is net profit, and total maxDD barely moves across the whole sweep
// (-$892 to -$1,070) because it is dominated by the short book (-$1,185,
// untouched by this gate).
// =====================================================================
//
// (V34 header, retained below for continuity)
//
// GoldTrendScalperV34 (2026-09-18) - ONE new test knob vs V33:
// MaxFullTakeAtrLong, a LONG-ONLY regime gate. Default 0 = OFF, so a
// 31mo run as shipped MUST reproduce V33/V31: net $18,854, PF 1.89, 763
// trades, Long $6,086/310, Short $12,768/453.
//
// THE PROBLEM IT TARGETS: the long book is regime-dependent in a way the
// short book is not. In 2024 (the non-trending year) longs were
// break-even - 94 trade-rows, +$153, PF 1.05 - while shorts carried the
// year at PF 3.80. 2024 also holds the whole period's max drawdown.
//
// THE MECHANISM (measured, not assumed): the rungs are FIXED DOLLARS, so
// what they cost in market movement is set entirely by volatility.
// FullTake for a long = 312 * RungScaleLong = $349.44 = 6.99 points on 5
// contracts. That is 5.9 ATRs away at 2024's median long-entry ATR of
// 1.18, but only 1.8 ATRs at 2026's median of 3.88. Low ATR does not
// make longs lose - it makes their targets unreachable, so they bleed
// out through stall exits and stops instead.
//
// EVIDENCE from the 31mo baseline debug log (116 long entries):
//   - Long FullTake rate by year: 2024 6.1% / 2025 10.9% / 2026 19.0% -
//     tracks median entry ATR (1.18 / 1.80 / 3.88) exactly.
//   - Median entry ATR, FullTake longs vs the rest: 3.09 vs 1.62. This
//     is the strongest separator of any logged entry feature.
//   - ADX does NOT separate (38.3 vs 39.5, marginally backwards) - so
//     the obvious "raise AdxThresholdLong in chop" idea is unsupported
//     and is NOT what this gate does. Runners-up that did separate, both
//     left for a later test: DIspread (33.4 vs 25.9), barBodyATR (1.38
//     vs 1.01).
//
// WHY DISTANCE-IN-ATRs RATHER THAN A RAW ATR FLOOR: it stays correct if
// Contracts, FullTake or RungScaleLong ever change, and it is exactly
// the regime-relative formulation the V15 header flagged as the better
// next step for this class of filter.
//
// HONEST PRIOR - READ BEFORE INTERPRETING THE SWEEP: a raw sub-2.0 ATR
// floor for LONGS was tested around V15 and REJECTED, because those
// cycles netted about +$740 and dropping them cost that. Expect the same
// shape here: the blocked cycles are mostly break-even, so net may fall
// slightly while PF, trade count and exposure improve. Judge on PF, max
// drawdown and net TOGETHER - the APEX trailing drawdown is the hard
// fail condition, and 2024 (the year this gate mostly empties of longs)
// is where the period's max drawdown lives.
//
// SWEEP PLAN (one value at a time, everything else at V33 defaults):
//   5.0 -> blocks 40 of 116 longs (ATR < 1.40); those 40 produced ZERO
//          FullTakes in 31 months - the cleanest cut available
//   4.0 -> blocks 62 (ATR < 1.75); by year 2024:42 / 2025:20 / 2026:0
//   3.5 -> blocks 72 (ATR < 2.00)
//   3.0 -> blocks 80 (ATR < 2.33)
//   2.5 -> blocks 90 (ATR < 2.80); kept-long FullTake rate rises to 26.9%
// FALSIFIER: net falls with no PF gain AND no max-drawdown improvement.
// Every blocked long prints a "Blocked: LONG regime" line - count them
// in GTS_debug_log.txt to confirm the gate fired as sized above.
// =====================================================================
//
// (V33 header, retained below for continuity)
//
// GoldTrendScalperV33 (2026-09-18) - CANDIDATE LIVE BUILD. Closes out the
// V32 test build: one knob removed after a clean reject, one kept as
// live-only insurance, all V32 instrumentation kept, one live-readiness
// fix to the log file. At defaults this MUST reproduce V31/V32: net
// $18,854, PF 1.89, 763 trades, Long $6,086/310, Short $12,768/453.
//
// V32 TEST RESULTS (all 31mo, correct RTH template):
//   K1 UseFullTakeFastClock = REJECTED and REMOVED. On: net $16,500, PF
//      1.77, 718 trades (vs $18,854 / 1.89 / 763); largest winner fell
//      $1,143 -> $718. Mechanism, from the baseline log: 51 FullTakes
//      averaged $427 (median $364) and 32 of 51 blew through $312 by
//      $100+ on the 1-minute clock. The fast clock capped every one of
//      those at ~$315 - ~$5,700 of blow-through given up - and the
//      "touched $312, closed the minute below it" cycles it rescued were
//      worth less than that. Net -$2,354. Same shape as V7's rung-level
//      reject: the 1-minute close IS the useful confirmation window, for
//      FullTake too. Falsifier met (avg FullTake $ down, net down).
//   K2 UseFomcBlackout = CONFIRMED backtest NO-OP (763 trades, $18,854
//      exactly). The bot had zero entries at/after 13:30 on any of the 22
//      FOMC days in the window and traded on only 4 of them. KEPT, default
//      OFF: what it covers is live stop-market slippage into the 14:00
//      statement, which no backtest can price. Turn ON for live; extend
//      fomcDates first.
//   I2 (BE/TRAIL instrumentation) - hypothesis "runner-protection layer
//      is inert" was OVERSTATED: the $400 cap set the stop on only 23 of
//      276 entries (8%; 15% in 2026). For those, the arithmetic held
//      exactly (trail fired 0/23, BE 17% vs 44%) - but for the other 92%
//      the layer is active (BE moved the stop in 43% of all cycles, 68%
//      of those right after TP1). Regime gradient is real (BE rate 57% /
//      46% / 30% across 2024/25/26). No redesign warranted. Bonus: cap-
//      bound (highest-ATR) entries reach FullTake at 43.5% vs 20.9% -
//      the bot's best trades, matching V9's finding from the other side.
//   I3 (DAY lines) - size step-down after a losing day: FALSIFIED. 9
//      LOSS-halt days in 31mo; the day after was negative 0/9, averaging
//      +$238 (+$536 on the 4 that traded) vs +$87 for all active days. A
//      step-down would cut size on above-average days. Not built.
//   H1/H2 (date-based rollover, template logging) - confirmed no-ops
//      under RTH (V32 at defaults reproduced V31 to the dollar).
//
// V33 CHANGE: GTS_debug_log.txt is OVERWRITTEN when Account.Name ==
// "Backtest" (Strategy Analyzer - preserves the V25 "a log can only be
// the run that produced it" guarantee) and APPENDED on any other account,
// so a live restart doesn't erase the session so far. Every run logs a
// RUN START line with timestamp, account and mode.
//
// STATUS: candidate for live. Known open items, deliberately NOT in this
// build: (a) the long book was breakeven in the non-trending 2024 (94
// trades, +$153, PF 1.05) while shorts carried the year (PF 3.80) - a
// long-side regime gate is the next strategy test, its own version;
// (b) before live, confirm the Strategy Analyzer commission template
// matches the APEX fee schedule, and extend fomcDates.
// =====================================================================
//
// (V32 header, retained below for continuity)
//
// GoldTrendScalperV32 (2026-09-18) - TEST BUILD from a code review of V31.
// Two hardening changes (expected no-ops under the RTH template), three
// pieces of instrumentation (Print/log only), and two NEW test knobs,
// both OFF by default. As shipped, a 31mo run MUST reproduce V31 to the
// dollar: net $18,854, PF 1.89, 763 trades, Long $6,086/310, Short
// $12,768/453. That reproduction run is step one - it proves the
// hardening changes are the no-ops they're meant to be. Then flip the
// knobs ONE AT A TIME.
//
// REGIME CHECK (the review's item 1, run before this build): V31 on
// 2024 ALONE under correct RTH = net $2,007, PF 1.55, 131 trades, maxDD
// -$915 (the whole-period max DD lives in 2024). Passes the falsifier
// (PF >= 1.2, positive) but thin - shorts carried it (PF 3.80, $1,854 on
// 37 trades), longs were flat (PF 1.05, $153 on 94 trades). ~89% of the
// 31mo profit came from the trending 2025-2026 stretch. Largest 2024
// loss was only -$222: low ATR, so the $400 cap never set the stop -
// the same code behaves differently by regime (see item 2).
//
// HARDENING (always on - no behavioural change under RTH):
//   H1. Day rollover is now calendar-date based (lastRolloverDate), not
//       Bars.IsFirstBarOfSession. The old check fires wherever the
//       session TEMPLATE says a session starts - with overnight bars in
//       scope that's the Globex open, so dayStartCum/haltedForDay reset
//       at the wrong time. This strategy is always flat outside
//       10:02-15:00, so "first bar of a new date" is correct under any
//       template. Under RTH it's the same bar -> identical results.
//   H2. State.DataLoaded logs the trading-hours template name, primary
//       BarsPeriod and the session window, and prints a loud WARNING if
//       the template name doesn't contain "RTH". The silent switch from
//       "CME US Index Futures RTH" to "Custom" that collapsed the profit
//       factor earlier in this project is now visible in every log.
//       Note: that RTH template is an EQUITY-INDEX session (09:30-16:15
//       ET) applied to gold - it works because the 10:02-15:00 window
//       sits inside it, and IsExitOnSessionCloseStrategy's close time
//       comes from it. Don't change the template without re-baselining.
//
// INSTRUMENTATION (log only):
//   I1. Entry line gains `capBound=` (1 if the $400 MaxTradeLossUSD cap,
//       not the ATR multiple, set the stop) and `riskPts=`. capDist =
//       400 / (5 contracts x $10) = 8 pts; the cap binds for longs above
//       ATR ~4.2 (1.9x) and shorts above ~6.2 (1.3x) - routine in 2026.
//   I2. RunnerTrail logs `BE SET` / `TRAIL MOVE` whenever it actually
//       moves the stop. HYPOTHESIS TO TEST: when capBound=1, 1R = 8 pts
//       = ~$400 on 5 contracts, which is PAST FullTake ($312), so
//       neither branch can fire before the cycle is closed -
//       BreakEvenR / TrailActivationR / TrailAtrMult are inert for those
//       entries, and the native RunnerTpAtrMult (8xATR) target fired 3
//       times in 791 trades. Count BE/TRAIL lines by year and by
//       capBound. If ~zero in 2026, the "runner" is nominal and this is
//       effectively a fixed-dollar-target scalper - which is where a
//       real runner redesign would live (prior rejected "protection"
//       ideas were pre-rung floors, a different layer).
//   I3. A `DAY yyyy-mm-dd pl= halt= cycles=` line at every rollover.
//       This is the instrument-first step for the review's item 5C
//       (size step-down after a losing day): from the log alone, is the
//       day AFTER a LOSS-halt day worse than average? If not, there is
//       nothing for a step-down to catch and it should not be built.
//       Note the interaction that makes an intra-day version moot:
//       MaxTradeLossUSD (400) == DailyLossHalt (400), so one full
//       stop-out IS the halt - a "streak" can only exist across days.
//       (The last day of a run gets no DAY line - no rollover after it.)
//
// TEST KNOBS (default OFF):
//   K1. UseFullTakeFastClock - FullTake checked in UpdatePeakAndCheck-
//       Stall() on the 3-second clock instead of only at the 1-minute
//       close. Losses are cut at tick precision by the native stop;
//       profits are taken with up to 60s lag. Log shows FullTakes at
//       +635/+930/+1135 (blew through $312 mid-minute); the mirror image
//       (touched $312, closed the minute below it) is invisible. V7
//       rejected a RUNG-level fast check as catching non-durable
//       touches; V11 built a FullTake-only version and never tested it.
//       FALSIFIER: FullTake count up, average FullTake $ down, net flat
//       or down -> reject (same shape as V7). Requires UseStallFastConfirm
//       (the 3s series only exists when that's on).
//   K2. UseFomcBlackout / FomcBlackoutFromTime (1330) - on FOMC
//       statement days (14:00 ET; static fomcDates list, 2024-2026,
//       VERIFY and EXTEND before live use), flatten anything open and
//       take no entries from 13:30. CPI/NFP are 08:30, before the entry
//       window - no exposure. FOMC is the one scheduled event that hits
//       an open 5-contract position under a stop-market order; the
//       backtest already shows tick-through past the cap (-$437 largest
//       loss). ~8 sessions/year - expect a small net effect; the point
//       is live slippage, which the backtest can't see. FALSIFIER: if
//       the blocked 13:30-15:00 FOMC windows were net-positive in the
//       baseline, the blackout costs more than it protects.
//
// NOT BUILT, deliberately: 5C's size step-down mechanism (instrument
// first via I3), any rung-dollar retune (rungs shrink to ~0.2 ATR in
// the 2026 regime and the holdout says that HELPS), and any pre-rung
// stop tightening (RungFloorProtection / FixedTP1Floor were tested and
// rejected). Known harmless: ManageCycle runs twice per minute (from
// BarsInProgress 0 and 3); every check in it is idempotent and the fast
// clock owns barsSincePeak at defaults.
//
// FOLLOW-UP CANDIDATE surfaced by the 2024 run, not built here: the
// long book was breakeven in the non-trending year (94 trades, +$153).
// Long gates (AdxThresholdLong 22 / ERThresholdLong 0.47) may be too
// permissive in chop - a long-side regime gate is a separate test.
// =====================================================================
//
// (V31 header, retained below for continuity)
//
// GoldTrendScalperV31 (2026-09-18) - REMOVED ENTIRELY: UseVolumeDecayExit
// and its four parameters (VolumeDecayMinRatio, VolumeDecayStartSec,
// VolumeDecayEndSec, VolumeDecayConfirmBars), plus the volDecayBarsUnder
// state field and its OnBarUpdate logic - not left as an off toggle,
// matching this project's practice for rejected mechanisms (cf.
// StopCooldownBars, ProtectionLadder, MinVolRatioShort).
//
// WHY: two tested variants, both rejected. V28's single-bar version
// clipped winners outright (77 of 158 firings hit a POSITIVE cyclePL,
// forfeiting ~$5,795). V29 added a `cyclePLv < 0` gate - confirmed
// working (0 of 93 firings hit a winner) - but short net still fell
// ($4,479 -> $3,561 under, it later turned out, a broken Custom session
// template rather than RTH). V30 added VolumeDecayConfirmBars=2 to
// filter momentary dips from sustained decay. Once the session template
// was fixed back to CME US Index Futures RTH (see below - this was the
// real cause of the whole-session profit-factor collapse earlier in this
// project, not a data revision as first suspected) and V30 was retested
// on a clean, apples-to-apples baseline (long side pixel-identical
// either way, isolating the effect purely to shorts): short net
// $12,768 (decay exit off) -> $9,060 (decay exit on, 2-bar confirm), PF
// 1.95 -> 1.59. Still net-negative by a wide margin. The underlying
// VOL15 post-entry pattern (FullTake shorts sustain volRatio, fast-
// reversal shorts drain) was real in the historical data, but doesn't
// survive as a real-time decision rule - too many temporarily-dipping,
// ultimately-fine trades get caught alongside genuine fast losers, and
// the winners given up cost more than the losers saved.
//
// ALSO FIXED THIS VERSION: NinjaTrader's Trading Hours template had
// been silently switched from "CME US Index Futures RTH" to "Custom" at
// some point around V28/V29 (cause unknown - user-caught, not
// diagnosed by me) - this quietly changed which bars were in scope for
// the ENTIRE 31mo backtest, which is what actually explained the
// profit-factor collapse this project spent significant effort
// diagnosing as a possible historical-data revision (see the V27
// header's "updated MGC historical data" reasoning - that explanation
// is now understood to be wrong, or at least incomplete; the session
// template was the real, or at least dominant, cause). With RTH
// correctly restored, the TRUE confirmed baseline as of this version is:
// net $18,854, PF 1.89, 763 trades, Long $6,086/310 trades, Short
// $12,768/453 trades (31mo, decay exit off / removed). This supersedes
// every net/PF figure quoted anywhere earlier in this project's history
// - none of them were run against the correct session scope.
// =====================================================================
//
// (V30 header, retained below for continuity)
//
// GoldTrendScalperV30 (2026-09-17) - ONE change to V29's
// UseVolumeDecayExit: added VolumeDecayConfirmBars (default 2), requiring
// the ratio to stay under VolumeDecayMinRatio for that many CONSECUTIVE
// 5-second bars, not just one, before exiting. Still off by default,
// still not confirmed-good.
//
// WHY: V29's P&L gate worked exactly as intended - all 93 firings in its
// 31mo test hit an already-losing short, zero hit a winner. But short
// net still went DOWN vs decay-exit-off ($4,479 -> $3,561, PF 1.22 ->
// 1.19), and FullTake count dropped slightly (~47 -> ~45). The mechanism
// can't tell "this is heading to a real loss" apart from "this dipped
// for 5 seconds and is about to turn around" from a single bar - a
// currently-losing trade that's only temporarily down looks identical,
// for one bar, to one that's genuinely dying. Requiring the condition to
// hold for multiple consecutive bars (same idea as StallExit's
// StallBars, applied to a different signal) should filter out the
// momentary dips and keep only sustained decay.
//
// (V29 header, retained below for continuity)
//
// GoldTrendScalperV29 (2026-09-17) - ONE fix to V28's UseVolumeDecayExit:
// added a `cyclePLv < 0` gate, so it can now ONLY fire on an
// ALREADY-LOSING short. Still off by default, still not confirmed-good -
// this is a diagnosed fix to a proven failure mode, not a validated
// mechanism yet.
//
// WHY: V28's first 31mo test at defaults (0.9 ratio, 20-55s window) was
// a clear reject - short PF collapsed to 0.99, net went to -$174, avg
// winning short trade shrank to $90.60. Root cause was visible directly
// in GTS_debug_log.txt: of 158 VOL DECAY EXIT firings, 77 (49%) hit
// while cyclePL was ALREADY POSITIVE (examples: cyclePL=50, cyclePL=160,
// cyclePL=75) - forfeiting roughly $5,795 of unrealized gains, while the
// other 81 genuinely cut losing trades short (~$5,475 of downside). The
// mechanism had no concept of trade direction - it fired on the raw
// volRatio alone, unlike StallExit, which only ever acts after a profit
// floor is already locked in. This is the same "clips winners" failure
// signature this project has seen from every prior early-exit mechanism
// with no profit/loss gate (RungFloorProtection, FixedTP1Floor,
// PartialFloorExit, the old rung-only fast confirm).
//
// V29's fix restricts the check to `cyclePLv < 0` - it can now only cut
// a currently-losing short short, never touch one that's up. Everything
// else (0.9 ratio, 20-55s window, single-bar breach, no confirm-bars
// requirement) is UNCHANGED from V28 - isolating the one new variable.
// Defaults still a clean no-op: 31mo at UseVolumeDecayExit=false MUST
// still reproduce V27 exactly.
// =====================================================================
//
// (V28 header, retained below for continuity)
//
// GoldTrendScalperV28 (2026-09-17) - ONE new mechanism vs V27: a
// real-time volume-decay exit, SHORTS ONLY, OFF by default
// (UseVolumeDecayExit=false). NOT YET TESTED at any active value.
//
// WHY: V27's 5-second VOL15 resampling (n=18 FullTake / 29 FastLoser
// shorts, the largest clean sample this project has produced for this
// question) showed the entry-bar volRatio split is gone (see V27
// header), but a POST-entry split reappears and holds: FullTake shorts
// sustain volRatio ~1.1-1.3 from ~25s to ~55s after entry; fast-reversal
// shorts settle to ~0.6-0.9 in that same window. The split isn't present
// in the first ~20s (both sides still shedding the entry bar's own
// volume spike), which is why the window is gated to start at 20s, not 0.
//
// WHAT THIS ADDS: inside the existing volSeriesIdx (5-sec) branch, while
// scaledCount==0 (before the first rung, same pre-TP1 window VOL15 was
// already restricted to) and only for SHORT positions, if volRatio drops
// below VolumeDecayMinRatio while secSinceEntry is within
// [VolumeDecayStartSec, VolumeDecayEndSec], ExitShort() the whole
// position immediately rather than waiting for the stop. Deliberately
// the SIMPLEST version for a first test - single-bar breach, no
// multi-bar confirm requirement (StallExit's StallBars does that for a
// different mechanism; add a confirm-bars parameter here later ONLY if
// testing shows this one's too twitchy, not ahead of data).
//
// NOT applied to longs - the entry-bar and post-entry volume signal for
// longs was never usable (FullTake n=3 throughout this whole VOL15
// analysis), same asymmetric-evidence principle as MinAtrShort,
// ShortAdxCeiling, and MaxBreakoutDistATRShort.
//
// Defaults are a clean no-op: a 31mo run of V28 as shipped MUST
// reproduce V27 to the dollar (net $7,616, PF 1.25, 791 trades).
// Recommended first test: sweep VolumeDecayMinRatio only (e.g.
// 0.7/0.8/0.9/1.0), everything else held, watching short PF, short gross
// loss, and whether it's clipping any FullTake shorts it shouldn't.
// =====================================================================
//
// (V27 header, retained below for continuity)
//
// GoldTrendScalperV27 (2026-09-17) - REMOVED ENTIRELY: MinVolRatioShort
// (property, SetDefaults, OnBarUpdate filter block), not left as an off
// toggle - matches this project's practice for rejected mechanisms (cf.
// StopCooldownBars, UseMinAdxSlopeLong, ProtectionLadder).
//
// WHY: it was motivated by a partial-sample (144/275) finding that
// entry-bar volRatio separated FullTake vs fast-reversal SHORTS (2.45 vs
// 1.97 median). A fresh 31mo run on updated MGC historical data (V25,
// after fixing a truncated Strategy Analyzer date range) gave a much
// larger, more reliable short-side sample (38 FullTake vs 59 FastLoser)
// and the entry-bar volRatio split vanished: 2.04 vs 2.12 - essentially
// identical, if anything backwards from what a volume floor would need.
// The premise MinVolRatioShort was built on does not replicate.
//
// NOT thrown out entirely, though: the SAME analysis on POST-entry
// volume trajectory (first ~45s, VOL15 fastBar 0-3) showed real
// divergence even on the larger sample - FullTake shorts hold volRatio
// ~1.2-1.3, fast-reversal shorts drain from 1.43 to 0.66. That's a
// different mechanism (a real-time decay check, not a static entry
// gate) and still too thin to act on (n=18 FullTake / 29 FastLoser,
// longs n=3) - left for a future version once more VOL15 episodes have
// accumulated (V26's 5-second sampling, kept in this file, exists to
// help with that).
// =====================================================================
//
// (V26 header, retained below for continuity)
//
// GoldTrendScalperV26 (2026-09-11) - ONE change vs V25: the post-entry
// volume-tracking series is now 5-second bars instead of 15-second,
// with every dependent constant rescaled to preserve the same real-world
// windows (SMA period 20->60 keeps the rolling average at ~5 min;
// warmup 21->61; the fastBar<16 cap ->48, keeping the observed window at
// ~4 min). volSeriesIdx itself, the LogLine/debug-file mechanism, and
// all trading logic are unchanged from V25.
//
// WHY: V22/V24/V25 all produced byte-identical VOL15 output - 144 of
// 275 entries got zero samples. Re-reading the volSeriesIdx branch shows
// this is structural, not a bug: a cycle that goes flat (stopped out) or
// scales its first rung before the series' OWN first tick since entry
// can never be observed - `entryFastBarNum` never even gets stamped.
// At 15-second bars, anything resolving in <~15s is invisible by
// construction. Tightening to 5 seconds should recover some of that
// population (anything resolving in 5-15s becomes visible) at the cost
// of finer-grained, noisier early samples - this is a coverage
// experiment, not a confirmed improvement; the 31mo run's ENTRY/EXIT
// numbers must still match V22/V24/V25 exactly (this touches only the
// diagnostic series, never a trading decision).
//
// (V25 header, retained below for continuity)
//
// GoldTrendScalperV25 (2026-09-11) - ONE change vs V24: every LogLine()
// call (renamed from Print()) now ALSO writes directly to a fixed file,
// C:\Users\kunna\OneDrive\Documents\GTS_debug_log.txt, opened fresh
// (overwrite, not append) each time State.DataLoaded runs and closed in
// State.Terminated - bypassing the NinjaScript Output window entirely.
//
// WHY: V24's Output-window saves were checked seven times across two
// filenames - each one had a genuinely fresh Windows "modified"
// timestamp (proving Save As was real and not a stale/cached read on my
// side) but byte-identical content (MD5 63ce34756cc0e4cac17d0722664f9cda
// every time), even after the Strategy Analyzer Summary grid confirmed
// GoldTrendScalperV24 was the loaded/selected/compiled strategy and
// reproduced the exact expected baseline numbers. That combination -
// correct trade engine, frozen Print log - points at the Output window
// itself (or its routing) being stuck, not at the strategy code or at a
// compile/cache/file-sync problem. Writing straight to a file in
// OVERWRITE mode sidesteps the question entirely: whatever is in
// GTS_debug_log.txt after a run can only be from that run - there is no
// mechanism by which stale content could survive being overwritten.
//
// This is a debugging aid only - LogLine still also calls Print(), so
// the Output window keeps getting the same content it always did, in
// case it starts working again. No trading logic touched.
// =====================================================================
//
// (V24 header, retained below for continuity)
//
// GoldTrendScalperV24 (2026-09-11) - three changes vs V22 (the confirmed
// baseline actually on disk - a "V23" was pasted into the editor and
// tested directly without ever being saved as its own file, then
// deleted before it could be verified; this version supersedes it and
// is the one actually saved to disk):
//
//   1. REMOVED ENTIRELY: Use5MinBreakoutGate / FiveMinBreakoutBars
//      (property, SetDefaults, OnBarUpdate gate block). Tested at
//      FiveMinBreakoutBars = 4/6/10 on the 31mo window - all three
//      net-negative vs off. Removed rather than left as an off toggle,
//      matching this project's practice for rejected mechanisms (cf.
//      StopCooldownBars, UseMinAdxSlopeLong, ProtectionLadder).
//
//   2. FIXED: the V22 post-entry volume-tracking (VOL15) coverage bug.
//      V22 armed its window via `fastBarsSinceEntry = 0` inside
//      OnPositionUpdate's `quantity == Contracts` branch - callback
//      timing vs. the volSeriesIdx bar loop meant only 144 of 275
//      entries ever got episode data (52%). Replaced with a
//      self-contained state machine entirely inside the volSeriesIdx
//      branch: `entryFastBarNum` stamps CurrentBars[volSeriesIdx] the
//      first time it sees a non-flat position, re-arms to -1 the
//      moment Position.MarketPosition goes Flat, and fastBar is
//      computed as the difference - no dependency on OnPositionUpdate
//      firing in any particular order relative to OnBarUpdate. NOT YET
//      VERIFIED to actually reach ~275/275 - that's the first thing to
//      check in the next Output save.
//
//   3. NEW, UNTESTED: MinVolRatioShort (points multiple of volAvg[0],
//      0=off). SHORTS ONLY. Rejects a short when Volumes[0][0] /
//      volAvg[0] is below this ratio, sitting alongside MinAtrShort in
//      the short-filter block. Motivated by the partial V22 VOL15 data:
//      entry volRatio separated FullTake vs fast-reversal SHORTS
//      (2.45 vs 1.97 median) but showed no separation for longs - hence
//      short-only, mirroring MinAtrShort's own asymmetric justification.
//      Defaults to 0/off - a 31mo run of V24 at defaults MUST reproduce
//      V22/V21-at-defaults/V18 to the dollar: net $19,303, PF 1.93,
//      maxDD -$915, MTR 203d, Long $6,353/1.85/309, Short $12,950/1.98/452
//      (Use5MinBreakoutGate's removal is a no-op at its own default-off,
//      and the VOL15 fix is Print-only - neither changes a single trade).
// =====================================================================
//
// (V22 header, retained below for continuity)
//
// GoldTrendScalperV22 (2026-09-11) - INSTRUMENTATION ONLY, no logic
// change. Both new structural knobs from V21 (Use5MinBreakoutGate,
// RunnerContractsLong) stay at their no-op defaults here - V21's
// evaluation (5-min gate rejected; RunnerContractsLong=2 kept, RC=5
// declined on win-rate grounds) is unaffected. A 31mo run of V22 MUST
// reproduce V21-at-defaults / V18 to the dollar: net $19,303, PF 1.93,
// maxDD -$915, MTR 203d, Long $6,353/1.85/309, Short $12,950/1.98/452.
//
// BACKGROUND: a review of the trades export found the biggest losses are
// FAST (the 7 losses <=-$350 took 1/1/2/2/5/6/7 bars - avg loss by speed:
// <=4 bars -$201, 5-9 bars -$134, >=10 bars only -$74). So "slow bleed to
// a big loss" essentially doesn't happen here; the real leak is FAST
// losers. Two things added to characterize them, per direction:
//
//   PART A - richer entry-context logging (both LONG and SHORT, mirrors
//   V17's directional feature set: adxSlope, ema5slope, DIspread, ATR,
//   extATR, accel3) PLUS two new fields: volRatio (how far above the
//   volume-filter threshold the entry bar actually cleared - a continuous
//   number, not just pass/fail) and the entry bar's own shape -
//   barRangeATR / barBodyATR (signed toward the trade) - a convicted
//   breakout bar vs a weak one. Joined to outcome, tells us whether fast
//   losers already look different AT ENTRY.
//
//   PART B - post-entry volume tracking on a new, always-added 15-second
//   data series (index computed dynamically - 5 when UseStallFastConfirm
//   is on [default], 4 if it's ever off). For the first ~4 minutes after
//   every entry (16 fast-bars), while nothing has scaled out yet
//   (scaledCount==0 - the exact window fast losers resolve in), logs that
//   bar's 15-sec volume against a ~5-min rolling average. Tests whether
//   volume genuinely dries up before a fast loser turns, or whether
//   that's noise - the risk flagged before building this: a 15-sec window
//   right after entry is a small sample and could easily be false signal.
//
// Both parts are Print-only. No trading decision changed.
// =====================================================================
//
// (V21 header, retained below for continuity)
//
// GoldTrendScalperV21 (2026-09-11) - two NEW structural test knobs, both
// OFF/no-op by default. A 31mo run MUST reproduce V20/V18: net $19,303,
// PF 1.93, maxDD -$915, MTR 203d, Long $6,353 / Short $12,950. Test ONE
// AT A TIME.
//
//   1. Use5MinBreakoutGate / FiveMinBreakoutBars (default off, 6 bars).
//      BOTH directions. Requires the 1-min entry price to also be beyond
//      the last N *completed 5-min bars'* high/low - the higher timeframe
//      must agree a fresh breakout is happening, not just the 1-min chart.
//      Intent: raise entry selectivity so a larger share of entries reach
//      FullTake (51 FullTake cycles already carry ~130% of all net profit
//      - fewer, better entries should grow that share, not shrink trade
//      count for nothing). NOT tested - sweep FiveMinBreakoutBars once on
//      (try 4/6/10), watch trade count, FullTake count, and all-trades PF.
//
//   2. RunnerContractsLong (default 2 = current: 3 rungs peeled, 2 ride).
//      LONG ONLY - shorts stay fixed at the current 3-rung split. The
//      combined (both-sides) version of "hold more contracts to FullTake"
//      was tested in V19 and rejected (RC=4 net -$2,785; RC=5 doubled max
//      drawdown to -$1,685) - but in that same sweep the LONG side ALONE
//      improved at RC=5 (PF 1.85->2.04, net +$1,471) while it was the
//      SHORT book that broke. Isolating it to longs is the untested piece.
//      NOT confirmed - sweep RunnerContractsLong 3/4/5, watch long PF and
//      max drawdown (a bigger runner trades locked partial gains for
//      variance - the same trade-off that didn't pay off combined).
//
// Both are independent parameters - test the 5-min gate sweep fully, then
// RunnerContractsLong fully, then (only if both show promise) the
// combination as its own run.
// =====================================================================
//
// (V20 header, retained below for continuity)
//
// GoldTrendScalperV20 (2026-09-10) - CLEANUP ONLY, no behaviour change.
// Removed the dead ProtectionLadder exit mode (ExitMode selector,
// ArmBuffer, rungUsed[]) and the rejected UseMinAdxSlopeLong filter.
// Kept NoLongEntryHour = 12 (V18 CONFIRMED).
// =====================================================================
//
// (V16 header, retained below for continuity)
//
// GoldTrendScalperV16 (2026-09-09) - two CONFIRMED changes vs V15:
//
//   1. MinAtrShort = 2.0 (was 0/untested in V15). Shorts-only min-ATR
//      floor. 31mo isolated sweep 1.6/1.8/2.0/2.2 -> 2.0 is a clean peak.
//      Effect vs off: all PF 1.27 -> 1.60, net +$6k, max DD -73%,
//      max-time-to-recover 743 -> 146 days, longs bit-identical, whole
//      improvement lands in 2024 (a structural fix, not a curve-fit).
//   2. DailyProfitHalt = 300 (was 420, and 420 had NEVER been swept - it
//      was only spot-checked upward on the YTD window). Full 31mo
//      down-sweep off/420/350/300/250/200/150 -> 300 is a clean peak
//      (net falls on both sides; below 300 max-time-to-recover jumps
//      140 -> 218 days). See the SetDefaults comment for the full table.
//      The halt only fires when FLAT, so it never clips an open runner -
//      it's a "one good setup per day, then stop" filter, and it lands
//      almost entirely on shorts. Vs 420 it removes 25 late-in-day short
//      cycles worth -$1,794, 13 of them the fast-reversal pattern.
//      By year vs 420: 2024 +$706, 2025 +$1,057, 2026 -$368.
//
// COMBINED 31mo (both changes, vs V13): net $10,815 -> $18,203, PF
// 1.27 -> 1.79, max DD -$6,005 -> -$1,096, max-time-to-recover 743 ->
// 140 days. Long side: PF 1.53 throughout, net $5,652 -> $5,253.
//
// Code changes vs V15: (a) MinAtrShort default 0 -> 2.0; (b) the
// DailyProfitHalt check is guarded by `DailyProfitHalt > 0` and its
// property Range widened to (0, 5000) so 0 = fully off is testable
// (the Analyzer used to clamp a typed 0 up to 20). No other logic
// touched. At DailyProfitHalt > 0 and MinAtrShort held, identical to V15.
// =====================================================================
//
// (V15 header, retained below for continuity)
//
// GoldTrendScalperV15 (2026-09-08 - MinAtrShort: minimum-ATR floor for
// SHORT entries only. NOT YET TESTED at any active value.)
//
// New file from V13 (the last confirmed clean baseline - V14 was an
// instrumentation-only build, its Print scaffolding is NOT carried here).
//
// WHY: a 31mo run of V14 logged entry-bar 1-min ATR at every entry, then
// each entry was bucketed by cycle outcome. Result for SHORTS:
//
//   entry ATR band   cycles   net       by year (2024 / 2025 / 2026)
//     0.0 - 1.0         64    -$937
//     1.0 - 1.5         86   -$2,907
//     1.5 - 2.0         55   -$2,084     sub-2.0 total: -$5,706 / -$297 / +$75
//     2.0 - 2.5         36   +$3,001
//     2.5 - 3.5         54     +$314     >=2.0  total: +$1,144 / +$1,690 / +$8,257
//     3.5 +             89   +$7,776
//
// Every sub-2.0 ATR band is net-negative; every >=2.0 band is net-positive;
// and that split holds in ALL THREE YEARS, not just the recent one. The
// sub-2.0 short book lost -$5,928 over 205 cycles (~53% of all short
// entries). Dropping it is a static +$5,928 to short net, at a cost of 6
// FullTake shorts (-$1,967). 2024 - a losing year for the whole system -
// was roughly break-even ex this segment.
//
// LONGS were logged the same way and DO NOT show this: their sub-2.0 ATR
// bands net +$740 over 99 cycles (roughly break-even), and dropping them
// COSTS money. So this floor is deliberately SHORT-ONLY. A shared floor
// would clip neutral long trades for no gain (same reasoning that keeps
// ShortAdxCeiling short-only).
//
// WHAT THIS ADDS: one parameter, MinAtrShort (points). If > 0, a short is
// rejected when atr1[0] < MinAtrShort. Sits alongside MaxBreakoutDistATRShort
// and ShortAdxCeiling in the short-filter block. Longs untouched.
//
// DEFAULT: chosen in SetDefaults (marked block). 0 = OFF and V15 then
// backtests IDENTICALLY to V13/V14. Recommended isolated 31mo sweep:
// 1.6 / 1.8 / 2.0 / 2.2, one value at a time, watching short trade count,
// short gross loss, all-trades PF, and that long PF stays ~1.53.
//
// KNOWN LIMITATION / next step (do NOT fold into this test): MinAtrShort is
// an ABSOLUTE point threshold. Gold's ATR scales with price - in 2024
// (~$2.5k) ATR<2 was normal tape; in 2026 (~$4.2k) it is rare. So a fixed
// value implicitly means "only short when gold is in a higher-volatility
// regime." That is fine here (the short edge only exists in that regime),
// but a future version could make it regime-relative instead:
//   - ATR / Close >= X  (a fraction, stable across price levels), or
//   - two floors selected by the 30-min HTF direction, so an established
//     downtrend and a countertrend pop can carry different ATR minimums.
// Keep that as its own separate file/test after this one is settled.
// =====================================================================
//
// (V13 header, retained below for continuity)
//
// GoldTrendScalperV13 (2026-08-24 - VolatileStopAtrMult split into
// LONG/SHORT versions, NOT YET TESTED)
//
// New file rather than an edit to V12, per usual versioning practice -
// V12.cs stays on disk as the last fully-confirmed comparison point.
//
// PROBLEM this addresses: the single shared VolatileStopAtrMult (see
// the retained V12 header just below) was only a clean design -
// tighten longs, no-op shorts - because it happened to exactly equal
// the base SHORT SlAtrMult (both 1.5). Once V12 confirmed
// SlAtrMult=1.3 for shorts, that equality broke: a volatile bar now
// gives shorts a WIDER stop (1.5x) than a calm bar (1.3x), backwards
// from "tighten when volatile." A single shared value can no longer do
// the right thing for both directions at once, now that their base
// stops differ (1.9 long / 1.3 short) - so it's split here into two
// independent parameters, matching how every other stop setting in
// this file already has a Long-specific twin (SlAtrMult/SlAtrMultLong).
//
// DEFAULTS chosen to be a NEUTRAL starting point, not a guess at the
// answer:
//   VolatileStopAtrMultLong  = 1.5  - unchanged from the old shared
//     value. This is the side it was actually validated for (7.5mo:
//     long PF 1.90->1.99) - carrying it forward unchanged means long
//     behavior is IDENTICAL to V12 until this is deliberately tested
//     at a different value.
//   VolatileStopAtrMultShort = 1.3  - set to exactly equal the new
//     base SlAtrMult, making this a clean no-op for shorts, same as
//     the original 1.5=1.5 design intent, just re-anchored to the new
//     base. Short behavior is also IDENTICAL to V12 with this default.
//
// So as shipped, this file should backtest IDENTICALLY to V12 - the
// split alone changes nothing until one of the two new values is
// actually moved. That's deliberate: it isolates "does splitting the
// parameter change anything" (it shouldn't) from "does tuning shorts'
// volatile-bar stop independently help" (the real open question).
//
// REASONING for what to actually test, worth restating here since it's
// not obvious from the numbers alone:
//   - Lowering VolatileStopAtrMultShort below 1.3 (e.g. 1.0-1.1) stacks
//     TWO tightening effects on the same subset of trades (already-
//     tightened base + a further volatile-bar squeeze). Worth caution:
//     the rejected V9 sizing test found elevated-ATR bars tend to be
//     some of the BEST trending setups, not just riskier ones - if
//     that still holds, tightening further specifically on high-ATR
//     entries risks clipping the best short setups, the same "clips
//     winners" pattern that got SlAtrMult=1.2 rejected as the base.
//   - Raising it (1.7+) gives volatile-bar shorts MORE room than calm
//     ones - plausible if elevated ATR really does mark the better
//     short setups, but currently just a theory, not tested.
//   - Recommended first test: isolated 31mo sweep of
//     VolatileStopAtrMultShort at a few values (e.g. 1.1/1.3/1.5/1.7)
//     with VolatileStopAtrMultLong held at 1.5 throughout, mirroring
//     the same one-variable-at-a-time approach used for every other
//     confirmed change in this project.
// =====================================================================
//
// (2026-08-24 V12 header, retained below for continuity)
//
// GoldTrendScalperV12 (2026-08-24 revision - CONFIRMED: stop=1.3, stall
// exit ON w/ scope=Both, tolerance=15, fast confirm=3s)
//
// Four changes confirmed via 31-month isolation testing this session,
// building on top of the 2026-08-13 V12 base retained further below.
//
//   1. SlAtrMult 1.5 -> 1.3 (CONFIRMED-BEST). Base SHORT stop
//      multiplier (shorts always use this - see slAtrDir logic in
//      OnBarUpdate, never SlAtrMultLong). Isolated a 3-point sweep on
//      31mo (scope=Both, tolerance=15, fast confirm=3s held constant
//      across all three so only the stop value moved):
//        1.2: short PF 1.15, short net $4,333, gross loss -$28,444,
//             avg losing short trade -$97.08
//        1.3: short PF 1.19, short net $5,453, gross loss -$28,698,
//             avg losing short trade -$104.36
//        1.5: short PF 1.17, short net $5,558, gross loss -$31,798,
//             avg losing short trade -$119.54
//      Not linear - 1.3 is a real peak, not an endpoint. It keeps
//      nearly all of 1.2's gross-loss improvement while short PF goes
//      UP instead of down and net stays within ~$100 of the old 1.5
//      baseline. 1.2 is REJECTED - past the point where tightening
//      helps, same "clips winners without saving losers" signature as
//      every other over-tightened stop already rejected in this
//      project. 1.25 and 1.35 not yet tested - true optimum may not
//      sit exactly on 1.3.
//
//   2. UseStallExit false->true, StallExitAppliesTo ShortsOnly->Both
//      (CONFIRMED, net-negative on longs, kept anyway - deliberate
//      tradeoff, not an oversight). An isolated 31mo test of ONLY this
//      change (stop still at 1.5, no fast confirm) showed the short
//      side completely unchanged (it already had stall exit on) but
//      long PF 1.61->1.59, long net $6,754->$6,297 (-$457, and
//      all-trades net moved by that same -$457, confirming this is a
//      clean, isolated long-side cost with nothing else bleeding in).
//      Same "clips winners" signature as every other early-exit
//      mechanism already rejected for longs specifically (RungFloor-
//      Protection, FixedTP1Floor, PartialFloorExit, the old rung-only
//      fast-confirm). Left on for Both anyway per user decision - the
//      short-side case for stall exit is judged worth a small accepted
//      long-side cost.
//
//   3. StallToleranceUSD 25->15 (CONFIRMED as part of the bundle).
//   4. UseStallFastConfirm false->true, StallFastConfirmSeconds 5->3
//      (CONFIRMED as part of the bundle).
//      Neither #3 nor #4 has been isolated separately from each other
//      in testing - both were varied together across every run this
//      session, alongside the stop change in #1. Their combined effect
//      vs. the old ShortsOnly/no-fast-confirm/tolerance=25 baseline is
//      folded into the numbers documented under point 1 above. If a
//      future session wants to know which of tolerance vs. fast-clock-
//      interval is actually doing the work, that still needs its own
//      isolated 31mo pair.
//
// KNOWN OPEN ISSUE, NOT addressed in this revision: VolatileStopAtrMult
// is still 1.5, which was originally set to exactly equal the OLD
// SlAtrMult=1.5 so volatile-bar stop tightening would be a deliberate
// no-op for shorts (see the 2026-08-13 note further below). Now that
// the base SlAtrMult is 1.3, a volatile bar (ATR>=4) gives shorts a
// WIDER stop (1.5x) than a calm bar (1.3x) - backwards from "tighten
// when volatile." This was left AS-IS on purpose: it was present,
// unchanged, in every 31mo run that confirmed the 1.3 value above, so
// "fixing" it now would be an untested change silently riding along on
// an already-confirmed one. Flagged for a dedicated, isolated test next
// session (try VolatileStopAtrMult=1.3 or lower on its own) - do not
// just correct this without testing it in isolation first.
// =====================================================================
//
// (2026-08-13 V12 header, retained below for continuity)
//
// CONFIRMED: volatile-bar stop tightening (UseVolatileStopTighten=true,
// VolatilityATRThreshold=4, VolatileStopAtrMult=1.5). Built from V8's
// clean base - NOT the original rejected V9 draft, which tested
// volatility-ADAPTIVE CONTRACT SIZING (fewer contracts on high-ATR
// bars) and was rejected same day: cutting size clipped what turned
// out to be some of the best trending setups in this data, hurting
// all/long/short PF and net profit at once. That approach is gone
// entirely from this file.
//
// This replacement keeps full position size, and only tightens the
// ATR MULTIPLIER used for the stop when ATR >= threshold. Swept
// threshold (3/4/5) and mult (1.3/1.4/1.5) - confirmed peak at
// threshold=4, mult=1.5: all PF 2.42->2.45, long PF 1.90->1.99, net
// +$109 (7.5mo window). Key finding: mult=1.5 exactly equals the base
// SHORT multiplier (SlAtrMult), making this effectively LONG-ONLY in
// practice - it tightens longs (normal 1.9x) down to 1.5x on volatile
// bars, while being a no-op for shorts (already at 1.5x). Confirmed by
// testing mult=1.4, which started tightening shorts too and cost
// $680 net for a smaller long-side gain. Still only validated on the
// 7.5mo window - needs 31mo confirmation before being treated as final.
//
// Built in response to the 2026-08-13 incident: two shorts, 11 minutes
// apart, both entered late into an already-mature 32-point decline,
// both reversed and stopped out within minutes (combined -$391).
// ADX/ER/DI all correctly read "strong downtrend" at both entries -
// the gates weren't wrong about direction, they just had no way to
// flag an extended/climactic move. Three filters were built and
// isolation-tested on the 31mo window; two are CONFIRMED-BEST and kept,
// one was tested and REMOVED:
//
//   1. MaxBreakoutDistATRShort = 2 (CONFIRMED-BEST). Short-side twin
//      of the existing long-only MaxBreakoutDistATRLong. Rejects a
//      short whose entry price is already more than 2 ATRs below the
//      recent breakout low - blocks late, extended entries into a
//      move that's already run. 31mo: short PF 1.20->1.25 vs baseline,
//      net +~$1,000-1,400, minimal trade-count cost.
//   2. ShortAdxCeiling = 70 (CONFIRMED-BEST). Upper bound on 5-min
//      ADX, SHORTS ONLY. AdxThreshold is a floor only; there was
//      never a ceiling. Loose (70) but surgical - only removed ~9
//      short trades vs filters-off on 31mo while short PF still
//      improved. Ceiling=32 and 50 were both tested and rejected as
//      too tight. NOT applied to longs - AdxThresholdLong already has
//      its own 22 floor; a shared ceiling gutted long PF in testing
//      (1.76->0.72 at 32).
//   3. StopCooldownBars (+ StopCooldownAppliesTo) - REMOVED ENTIRELY,
//      not left as an off toggle. After a STOP-LOSS fill, would have
//      blocked new entries in the same direction for N bars. Tested
//      0/4/5/beyond on 31mo: 0 and 4 were IDENTICAL (redundant with
//      the existing base CooldownBars=3), 5 caught exactly one trade,
//      everything past 5 steadily degraded PF and net profit. The
//      underlying premise ("a stopped-out short predicts another bad
//      short soon after") doesn't hold up in this data.
//
// ALSO: RungScaleLong tightened 1.16 -> 1.12 (confirmed slight long PF
// improvement, 31mo: long PF 1.40, all PF 1.29, net $12,218). Also
// tested this session with NO EFFECT found: BreakoutBarsLong,
// CrossGraceBarsLong, MaxBreakoutDistATRLong. HTFEmaPeriodLong DID
// move results when varied - flagged for a proper isolated sweep in a
// future session, not yet quantified.
// =====================================================================
//
// (V7 header, retained below for continuity)
//
// Two changes from V6:
//
//   1. REMOVED ENTIRELY: the rung-only fast confirm mechanism
//      (UseFiveSecondConfirm / FastConfirmSeconds / FastConfirmBars,
//      CheckRungThresholdsFast(), the 5th data series, and the
//      debounce state). Tested and rejected 2026-08-12 via a sweep of
//      interval (5-60s) x confirm-bars-needed (1-4) in Strategy
//      Analyzer. Finding: no combination ever beat the plain 1-minute
//      bar-close baseline. Any setting requiring LESS than ~60 seconds
//      of total hold time (interval x bars) let through noisier,
//      less-durable rung touches and dragged PF down; any setting
//      requiring ~60 seconds converged to numbers IDENTICAL to the
//      baseline (two different interval/bars combos both landing on
//      net $11,341 / PF 2.12 / 220 trades, matching fast-confirm-off
//      exactly). Conclusion: the 1-minute clock's "must still be above
//      the rung when the bar closes" is already the maximum useful
//      confirmation window - there's no daylight between "too fast,
//      catches noise" and "as slow as 1-minute, adds nothing." Same
//      "clips winners, doesn't help losers" signature as every other
//      early-exit mechanism this project has tested and removed
//      (RungFloorProtection, FixedTP1Floor, PartialFloorExit,
//      MaxHoldMinutesLong, UseRetreatLockLong, UseBreakevenLong,
//      StallExit-for-longs). Removed outright per user request, not
//      left as an off toggle - matches how every other rejected
//      mechanism in this project's history has been handled.
//
//   2. StallBars default changed 3 -> 4. Confirmed better across
//      multiple 2026-08-12 backtest runs alongside the fast-confirm
//      testing above; held up consistently once fast-confirm was
//      taken out of the picture too.
//
// UseTickEvaluation was removed entirely in V12 (unused). See the V12
// header block near the top of this file for the current fast-confirm
// design (now scoped to the stall exit, not FullTake/TP3).
//
// Everything else (rungs, gates, risk rails, session windows, bug
// history) is unchanged from V5/V6 - see those files for the full
// tuning history. GoldTrendScalperV6.cs remains on disk as the
// pre-removal comparison baseline if ever needed again.
// =====================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
	public enum StallExitScopeV40
	{
		ShortsOnly,
		LongsOnly,
		Both
	}

	public class GoldTrendScalperV40 : Strategy
	{
		private EMA emaFast, emaSlow, emaHTF, emaHTFLong, ema5;
		private ADX adx5;
		private DM  dm5;
		private ATR atr1;
		private SMA volAvg;
		private SMA volAvgFast;      // V22: 15-sec series volume average
		private int volSeriesIdx;    // BarsInProgress index of the volume-tracking series
		                             // (5-sec as of V26, was 15-sec in V22-V25)
		private int entryFastBarNum = -1;  // V24: CurrentBars[volSeriesIdx] value stamped at
		                                    // entry; self-arms/re-arms entirely within the
		                                    // volSeriesIdx branch (see V24 fix note there) -
		                                    // replaces V22's fastBarsSinceEntry, which only
		                                    // covered 144/275 entries (see V24 header block).
		private System.IO.StreamWriter debugLogFile;  // V25: direct file log, bypasses the
		                                    // NinjaScript Output window (see V25 header block).

		private double dayStartCum  = 0;
		private bool   haltedForDay = false;
		private DateTime lastRolloverDate = DateTime.MinValue;  // V32: date-based day rollover
		                                                        // (replaces Bars.IsFirstBarOfSession,
		                                                        // which moves with the session template)
		private int    dayCycles    = 0;    // V32: per-day summary instrumentation
		private string dayHaltKind  = "";   // V32: "", "LOSS" or "GOAL"

		// V32: FOMC statement days (14:00 ET). STATIC LIST - verify against the Fed's
		// published calendar and EXTEND before live use past the last date here. Only
		// consulted when UseFomcBlackout is on.
		private static readonly System.Collections.Generic.HashSet<DateTime> fomcDates =
			new System.Collections.Generic.HashSet<DateTime>(new DateTime[] {
				new DateTime(2024,1,31), new DateTime(2024,3,20), new DateTime(2024,5,1),  new DateTime(2024,6,12),
				new DateTime(2024,7,31), new DateTime(2024,9,18), new DateTime(2024,11,7), new DateTime(2024,12,18),
				new DateTime(2025,1,29), new DateTime(2025,3,19), new DateTime(2025,5,7),  new DateTime(2025,6,18),
				new DateTime(2025,7,30), new DateTime(2025,9,17), new DateTime(2025,10,29),new DateTime(2025,12,10),
				new DateTime(2026,1,28), new DateTime(2026,3,18), new DateTime(2026,4,29), new DateTime(2026,6,17),
				new DateTime(2026,7,29), new DateTime(2026,9,16), new DateTime(2026,10,28),new DateTime(2026,12,9),
				// 2027 - VERIFY against federalreserve.gov before live use (estimated from pattern)
				new DateTime(2027,1,27), new DateTime(2027,3,17), new DateTime(2027,5,5),  new DateTime(2027,6,16),
				new DateTime(2027,7,28), new DateTime(2027,9,15), new DateTime(2027,10,27),new DateTime(2027,12,8)
			});

		private double cumAtEntry   = 0;
		private double peakCyclePL  = 0;
		private int    scaledCount  = 0;
		private double entryRiskDist= 0;
		private double stopPrice    = 0;
		private bool   beDone       = false;
		private double lockedFloor  = 0;
		private int    barsSincePeak = 0;
		private int    lastEntryBar = -1000;
		private DateTime cycleEntryTime = DateTime.MinValue;
		private string cycleEntrySig = "";

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description   = "1-min gold scalper - V40 (2026-09-18): FastPeriod 9→5 (EMA(5)/EMA(21) on 1-min). Based on V39. Baseline to beat: $19,418 / PF 1.93 / 756.";
				Name          = "GoldTrendScalperV40";
				Calculate     = Calculate.OnBarClose;
				EntriesPerDirection = 1;
				EntryHandling = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy = true;
				ExitOnSessionCloseSeconds    = 600;
				BarsRequiredToTrade          = 80;
				IsInstantiatedOnEachOptimizationIteration = true;

				Contracts       = 5;
				Rung1           = 87;
				Rung2           = 190;
				Rung3           = 258;
				FullTake        = 312;
				UseStallExit    = true;   // CONFIRMED-BEST (2026-08-24), was off. Isolated
				                          // 31mo test (stop still 1.5, no fast confirm):
				                          // long PF 1.61->1.59, long net -$457, short side
				                          // unaffected (already had this on). Kept on anyway
				                          // per user decision - see 2026-08-24 header block.
				StallExitAppliesTo = StallExitScopeV40.Both;  // CONFIRMED-BEST (2026-08-24),
				                          // was ShortsOnly. See header block above for the
				                          // isolated long-side cost this carries.
				StallBars       = 4;   // was 3 in V5/V6 - confirmed better across multiple 2026-08-12 runs
				StallToleranceUSD = 15;  // CONFIRMED-BEST (2026-08-24), was 25. Tested as
				                          // part of the fast-confirm-on bundle, not isolated
				                          // separately from StallFastConfirmSeconds - see
				                          // header block.

				// === V12 NEW: fast interval for the STALL EXIT's peak-tracking/bar-count, on
				// top of the already-confirmed StallBars mechanism. UseTickEvaluation and the
				// FullTake/TP3-only fast confirm (V11) have both been REMOVED - the former was
				// unused, the latter was untested and superseded by this idea per user request.
				UseStallFastConfirm    = true;   // CONFIRMED-BEST (2026-08-24), was off/untested.
				StallFastConfirmSeconds = 3;     // CONFIRMED-BEST (2026-08-24), was 5. Interval
				                                  // of the fast clock, 1-60s. When on,
				                                  // barsSincePeak counts CONSECUTIVE fast-clock
				                                  // closes instead of 1-minute bars - StallBars
				                                  // keeps its existing meaning/value, just on a
				                                  // faster clock. Peak-tracking runs EXCLUSIVELY
				                                  // on whichever clock is active (fast OR slow,
				                                  // never both) - the old rejected
				                                  // UseFiveSecondConfirm mechanism ran BOTH
				                                  // clocks simultaneously, which is what caused
				                                  // barsSincePeak to fire 3x in under 40 seconds
				                                  // on a live trade back in V5. This design
				                                  // avoids repeating that specific bug.

				// === FOMC blackout (V32). CONFIRMED a backtest NO-OP: 31mo with it ON reproduced
				// the baseline exactly (763 trades, $18,854) - the bot had zero entries at/after
				// 13:30 on any FOMC day in 31 months. Kept, not removed, because what it covers
				// (stop-market slippage into the 14:00 statement) is a LIVE risk the backtest
				// cannot price. Default OFF for backtest comparability; TURN ON FOR LIVE. The
				// fomcDates list must be extended past its last date before then.
				UseFomcBlackout      = false;
				FomcBlackoutFromTime = 1330;

				MinFirstRungRR  = 0.0;
				MaxTradeLossUSD = 400;  // CONFIRMED-BEST (2026-08-13): tightened from 460 after
				                        // the V8 shorts-only filters (extension/ADX ceiling) were
				                        // already in place. Unlike a pre-filter test where
				                        // tightening this cost net profit, with the filters
				                        // active it improved everything at once: largest loss
				                        // $472->$427, gross loss down, all PF 2.34->2.36+, long PF
				                        // 1.79->1.90, net profit up. 430 was also tried and came
				                        // out worse than both 460 and 400 - not a clean gradient,
				                        // treat 400 as the confirmed value, not 430.
				DailyLossHalt   = 400;  // CONFIRMED-BEST: stepped down from 460 in small
				                        // increments (420/410/400) on top of the 400 trade cap
				                        // above - each step improved short PF and all-trades PF
				                        // with long PF unaffected (confirms it's a direction-
				                        // agnostic setting whose impact happens to land on the
				                        // short side more). 400 had the best PF of the three
				                        // tested; 410 had marginally higher net $ but lower PF -
				                        // 400 preferred as the more robust of the two.
				DailyProfitHalt = 300;  // CONFIRMED-BEST (2026-09-09, 31mo). Was 420 (never
				                        // actually swept - only checked upward on the YTD window).
				                        // Full 31mo down-sweep, one value at a time, everything
				                        // else at V16 (MinAtrShort 2.0, DLH 400, trail 2.7):
				                        //   off : net $13,287  PF 1.35  maxDD -$1,949  MTR 146d
				                        //   420 : net $16,808  PF 1.60  maxDD -$1,636  MTR 146d
				                        //   350 : net $17,665  PF 1.70  maxDD -$1,096  MTR 140d
				                        //   300 : net $18,203  PF 1.79  maxDD -$1,096  MTR 140d  <-- peak
				                        //   250 : net $17,759  PF 1.80  maxDD -$1,096  MTR 218d
				                        //   200 : net $14,963  PF 1.70  maxDD -$1,096  MTR 218d
				                        //   150 : net $14,375  PF 1.67  maxDD -$1,352  MTR 218d
				                        // Clean peak at 300: net falls off on both sides, and below
				                        // 300 max-time-to-recover jumps 140 -> 218 days (you halt
				                        // after the day's FIRST trade and can't climb out of the
				                        // 2025 drawdown fast enough). The 200 net cliff is DPH
				                        // starting to skip genuine 2nd-trade FullTakes.
				                        // This is NOT profit protection - the halt only fires when
				                        // FLAT, so the day's open runner still completes; it just
				                        // stops NEW entries. It functions as a "one good setup per
				                        // day, then stop" filter, and it lands almost entirely on
				                        // shorts (longs plateau at <=350). Vs DPH 420, it removes 25
				                        // late-in-day short cycles worth -$1,794, of which 13 are
				                        // the fast-reversal pattern (<=4 bars, stopped for a loss) -
				                        // i.e. it catches ~13 of the ~50 known fast-reversal shorts
				                        // by proxy, because those cluster after a day has already
				                        // trended ~$300. By year vs DPH 420: 2024 +$706, 2025
				                        // +$1,057, 2026 -$368 (mostly one long trade) - lifts the
				                        // weak years, barely dents the strong one. Longs: PF 1.53
				                        // unchanged; net $5,652 -> $5,253 (loses ~18 long-heavy
				                        // profit-cap days, all above 350).
				SlAtrMult       = 1.3;  // CONFIRMED-BEST (2026-08-24), was 1.5. Base SHORT stop
				                        // multiplier (shorts always use this - see slAtrDir
				                        // logic in OnBarUpdate, never SlAtrMultLong). Isolated
				                        // 31mo sweep of 1.2/1.3/1.5 found 1.3 a real peak, not
				                        // an endpoint: short PF 1.17->1.19, net ~flat (-$105),
				                        // gross loss improved ~$3,100, avg losing short trade
				                        // -$119.54->-$104.36. 1.2 tested and REJECTED (short PF
				                        // dropped to 1.15, net -$1,225 vs the 1.5 baseline) -
				                        // past the point where tightening helps, same "clips
				                        // winners" signature as other rejected tightening
				                        // mechanisms. See 2026-08-24 header block for full
				                        // detail. 1.25/1.35 not yet tested.
				RunnerTpAtrMult = 8.0;
				TrailActivationR= 1.0;
				TrailAtrMult    = 2.7;
				BreakEvenR      = 1.0;

				// === V9 CONFIRMED (2026-08-13): tighten (not shrink size) the stop on volatile
				// bars, LONG-SIDE effect specifically ===
				// V9's original approach (fewer contracts when ATR elevated) was tested and
				// REJECTED - elevated-ATR bars turned out to be some of the best trending setups
				// in this data, so cutting size clipped winners uniformly. This replacement idea
				// keeps full size and only tightens the ATR multiplier used for the stop above
				// the volatility threshold.
				//
				// IMPORTANT FINDING from the sweep: since VolatileStopAtrMult=1.5 exactly equals
				// the base SHORT multiplier (SlAtrMult=1.5 AT THE TIME), this override was a
				// NO-OP for shorts - it swapped 1.5 for 1.5. It only had real effect on LONGS,
				// whose normal multiplier (SlAtrMultLong=1.9) is meaningfully above 1.5.
				//
				// *** RESOLVED IN V13 (was a KNOWN OPEN ISSUE in V12): the single shared
				// VolatileStopAtrMult=1.5 stopped being a clean short-side no-op once base
				// SlAtrMult moved to 1.3 in V12 - a volatile bar gave shorts a WIDER stop than a
				// calm bar, backwards from "tighten when volatile." As of this file, the setting
				// is split into VolatileStopAtrMultLong/VolatileStopAtrMultShort below, each
				// independently tunable. Defaults preserve V12's actual behavior (see the V13
				// header block at the top of this file) - NOT YET TESTED at any other value. ***
				UseVolatileStopTighten = true;   // CONFIRMED-BEST (2026-08-13), was off/untested
				VolatilityATRThreshold = 4.0;    // CONFIRMED-BEST. ATR (1-min, 14-period) at or
				                                  // above this = "volatile" -> use
				                                  // VolatileStopAtrMultLong/Short instead of the
				                                  // normal SlAtrMult/SlAtrMultLong. Swept 3/4/5: 4
				                                  // was the clear peak (long PF 1.99 vs 1.67 at 3
				                                  // and 1.91 at 5). Threshold itself applies to
				                                  // both directions - not split, unlike the mult.
				VolatileStopAtrMultLong  = 1.5;  // UNCHANGED from the old shared value - this is
				                                  // the side it was actually validated for
				                                  // (7.5mo: long PF 1.90->1.99). See V13 header
				                                  // block at top of file.
				VolatileStopAtrMultShort = 1.3;  // NEW (2026-08-24, NOT YET TESTED). Defaults to
				                                  // exactly equal base SlAtrMult=1.3, making this
				                                  // a clean no-op for shorts - same design intent
				                                  // as the original shared value, re-anchored to
				                                  // the new base. See V13 header block for what
				                                  // to test and why (raising vs. lowering).

				ERPeriod        = 31;
				ERThreshold     = 0.30;
				UseHTFBias      = true;
				HTFEmaPeriod    = 10;
				Ema5Period      = 21;
				AdxPeriod       = 10;
				AdxThreshold    = 22;
				UseBreakoutTrigger = true;
				BreakoutBars    = 10;

				FastPeriod      = 5;
				SlowPeriod      = 21;
				MinSepATR       = 0.05;
				CrossGraceBars  = 3;
				CooldownBars    = 3;
				UseVolumeFilter = true;
				VolAvgPeriod    = 20;
				VolMult         = 1.2;

				UseLongOverrides = true;
				ERThresholdLong  = 0.47;

				// === V10 CONFIRMED: two-path long entry system ===
				// PATH A = the original full gate stack below (ER/ADX/HTF/5-min quality/1-min
				// alignment/cross-breakout), unchanged. PATH B = a genuinely separate, parallel
				// trigger (DI+/DI- fresh cross + rising ADX + relaxed ER), bypassing HTF bias,
				// 5-min quality, 1-min alignment, and the cross/breakout trigger entirely. A long
				// fires if EITHER path qualifies. Both CONFIRMED-BEST and both on by default as
				// of 2026-08-14 (31mo: long PF 1.45->1.58, long net $4,913->$6,420, all PF
				// 1.28->1.30, net +$1,021, on real added trade volume, not a handful of trades).
				UsePathA = true;   // the original gate stack - can be turned off to run Path B
				                    // in isolation for testing.
				UsePathB = true;   // CONFIRMED-BEST: was off/untested as "UseAdxErOverride" -
				                    // renamed and turned on by default now that it's validated.
				AdxErOverrideThreshold = 22;     // PATH B's ADX gate. CONFIRMED: testing 20 vs 22
				                                  // gave essentially identical results, so this
				                                  // now simply reuses the same 22 the base LONG
				                                  // ADX threshold already uses - no separate
				                                  // "dedicated higher gate" toggle needed anymore
				                                  // (that toggle, UseSecondaryAdxGate, has been
				                                  // removed entirely). Still independently
				                                  // adjustable if a different value is ever
				                                  // worth testing again.
				DiCrossGraceBars = 2;             // CONFIRMED-BEST: tightened from 3 - DI+ must
				                                  // have crossed above DI- within this many
				                                  // 5-min bars, a FRESH directional flip.
				ERThresholdLongOverride = 0.23;  // CONFIRMED-BEST: tightened from 0.28 - the
				                                  // relaxed ER floor Path B uses instead of the
				                                  // normal 0.47. Fully adjustable for testing -
				                                  // anywhere from 0.01 to 0.30.
				AdxRisingLookback = 3;           // ADX must be HIGHER now than it was this many
				                                  // bars ago (adx5[0] > adx5[AdxRisingLookback]),
				                                  // not just sitting above the threshold.
				                                  // Separates "actively building strength" from
				                                  // "already peaked and rolling over" - same
				                                  // static ADX level can mean either.


				AdxThresholdLong = 22;
				MinSepATRLong    = 0.05;
				VolMultLong      = 1.4;
				VolMultPathB     = 1.4;  // NEW: Path B's OWN volume multiplier, independent of
				                          // VolMultLong. Only applies when a long qualifies via
				                          // Path B alone (Path A's bar takes priority if both
				                          // paths qualify on the same bar). Defaults to matching
				                          // VolMultLong so behavior is identical until you
				                          // deliberately test a different value.
				SlAtrMultLong    = 1.9;
				RungScaleLong    = 1.12;  // CONFIRMED-BEST (2026-08-13): tightened from 1.16 - user
				                          // testing found 1.12-1.14 slightly improves long PF
				                          // (31mo run: long PF 1.40, all PF 1.29, net $12,218).
				                          // Also tested this session, NO EFFECT found:
				                          // BreakoutBarsLong, CrossGraceBarsLong,
				                          // MaxBreakoutDistATRLong. HTFEmaPeriodLong DID move
				                          // results when varied - flagged for a proper sweep in
				                          // a future session, not yet isolated/quantified.
				UseBreakoutTriggerLong = true;
				CrossGraceBarsLong = 3;
				HTFEmaPeriodLong = 20;
				MaxBreakoutDistATRLong = 0;
				BreakoutBarsLong = 10;

				// === V18 tested two LONG-only filters. UseMinAdxSlopeLong was REJECTED (31mo:
				// best case +$228 net for +63 DAYS of max-time-to-recover - thinning long
				// entries removes shots needed to climb out of a drawdown) and REMOVED in V20,
				// per this project's practice with rejected mechanisms (cf. StopCooldownBars).
				//
				// NoLongEntryHour = 12 - CONFIRMED-BEST (2026-09-09, 31mo). Blocks LONG entries
				// in the 12:00-12:59 (noon) hour. From the V17 instrumented run: h12 longs were
				// 28 cycles at 36% win / -$1,222 vs 58-66% win every other hour (h10 +$3,180,
				// h11 +$1,042, h13 +$2,253). h12 SHORTS are fine (+$2,489) so this is long-only.
				//   31mo vs V16:  net $18,203 -> $19,303 (+$1,100)   PF 1.79 -> 1.93
				//                 maxDD -$1,096 -> -$995 (better)     MTR 140 -> 203d (worse)
				//   FullTakes: Long 12 / Short 39 - IDENTICAL to V16, zero lost (verified from
				//   the trade export - the 48 removed long h12 legs were all losers/scratches).
				//   By year (all-trades) vs V16: 2024 -$131, 2025 +$283, 2026 +$948 - leans
				//   2026, but the h12-longs-lose pattern is robust (showed up in two independent
				//   analyses) and 2025 also improves. Shorts bit-identical every year.
				//   Kept as the eval config: it improves the trailing-drawdown metric (the hard
				//   APEX fail condition); the longer recovery is a patience cost, not a fail.
				//   Set to 0 to disable (hour 0 is never in the entry window).
				NoLongEntryHour    = 12;

				// CONFIRMED-BEST (2026-09-18, 31mo sweep of 10/9/8/7/6/4/3.5/3/2.5). LONG-ONLY.
				// Max distance to FullTake, in ATRs, a long entry is allowed to have. Blocks
				// longs when ATR is so low that the fixed-dollar FullTake is unreachable.
				// 8 -> ATR floor 0.87. See the V35 header block for the full sweep table, the
				// per-ATR-band decomposition, and an honest read of how much of the +$564 is
				// one trade. HARD FLOOR: never set below 7 - the ATR 1.00-1.17 band is worth
				// +$776 and every threshold <= 6 blocks it. 0 = off (reverts to V33: $18,854).
				MaxFullTakeAtrLong = 8;

				// V36 NEW, UNTESTED: LONG-ONLY directional-conviction gate, 0 = OFF. Minimum
				// DI+ minus DI- (5-min) required for a long entry. See the V36 header block
				// and the gate in OnBarUpdate. At 0 this file MUST reproduce V35:
				// net $19,418, PF 1.93, 756 trades.
				MinDiSpreadLong = 0;

				RunnerContractsLong = 2;   // V21: OFF (= current). Contracts - RunnerContractsLong,
				                           // capped at 3, is how many of the 5 LONG contracts get
				                           // peeled at the rungs; the rest ride to FullTake/trail.
				                           // Shorts unaffected - fixed at the current 3-rung split.
				                           // Not yet tested - sweep 3/4/5 in isolation.

				// === V8 CONFIRMED (2026-08-13): two of the three tested filters kept, locked in ===
				MaxBreakoutDistATRShort = 2;   // CONFIRMED-BEST. Mirror of the existing long-only
				                               // filter, built for shorts. Rejects a short when
				                               // price has already moved more than 2 ATRs below
				                               // the breakout low - filters out shorts that are
				                               // already extended/late into a mature decline.
				                               // 31mo confirmed: short PF 1.20->1.25 vs baseline,
				                               // net +~$1,000-1,400, with minimal trade-count cost.
				ShortAdxCeiling = 70;          // CONFIRMED-BEST. Shorts-only upper bound on 5-min
				                               // ADX. Loose (70) but surgical - only removed ~9
				                               // short trades vs filters-off on 31mo, while short
				                               // PF still improved 1.21->1.25. Ceiling=32 and
				                               // ceiling=50 were both tested and rejected as too
				                               // tight (cut real trades without a matching gain).
				                               // NOT applied to longs - AdxThresholdLong already
				                               // has its own 22 floor; a shared ceiling there
				                               // gutted long PF in testing (1.76->0.72 at 32).

				MinAtrShort = 2.0;  // CONFIRMED-BEST (2026-09-08, 31mo isolated sweep). Reject a
				                    // short when atr1[0] < 2.0 points. Shorts only - longs show no
				                    // ATR-floor pattern and a shared floor COSTS money there.
				                    // 31mo sweep (MinAtrShort = 1.6 / 1.8 / 2.0 / 2.2, one at a
				                    // time, everything else V13):
				                    //   off  : all net $10,815  PF 1.27  maxDD -$6,005  MTR 743d
				                    //   1.8  : all net $16,183  PF 1.55  maxDD -$1,776  MTR 319d
				                    //   2.0  : all net $16,808  PF 1.60  maxDD -$1,636  MTR 146d  <-- peak
				                    //   2.2  : all net $14,375  PF 1.51  maxDD -$1,637  MTR 335d
				                    // 1.6 was also worse than 1.8. 2.2 shows the "clips winners"
				                    // signature: short gross PROFIT dropped ~$2,300 while gross loss
				                    // barely moved - past 2.0 you are cutting the profitable 2.0-2.2
				                    // ATR band, not bad trades. So 2.0 is a real peak, not an
				                    // endpoint. By-year SHORT net vs the off baseline:
				                    //   2024  -$4,562 -> +$1,064   (the whole improvement; 2024 was
				                    //                               a losing YEAR, ~break-even now)
				                    //   2025  +$1,393 -> +$1,835
				                    //   2026  +$8,332 -> +$8,257   (essentially unchanged -> NOT a
				                    //                               curve-fit to the recent regime)
				                    // Longs bit-identical at every threshold ($5,652 / PF 1.53 / 374
				                    // trades) - proof the gate is genuinely shorts-only.
				                    // KNOWN LIMITATION: absolute point threshold. Gold ATR scales
				                    // with price, so 2.0 implicitly means "only short in a higher-vol
				                    // regime." Fine here; a regime-relative form (ATR/Close, or two
				                    // floors gated on the 30-min HTF trend) is a separate future test.

				// StopCooldownBars / StopCooldownAppliesTo REMOVED entirely as of this revision.
				// Tested 0/4/5/and beyond on 31mo: 0 and 4 were IDENTICAL (redundant with the
				// existing base CooldownBars=3), 5 caught exactly one trade, everything past 5
				// steadily degraded PF and net profit. Conclusion: the underlying premise ("a
				// stopped-out short predicts another bad short soon after") doesn't hold up in
				// this data - removed rather than left as an off toggle, per user request and
				// matching how every other rejected mechanism in this project has been handled.

				AllowLongs      = true;
				AllowShorts     = true;

				EntryStartTime  = 1002;
				LastEntryTime   = 1400;
				FlattenTime     = 1500;
			}
			else if (State == State.Configure)
			{
				Calculate = Calculate.OnBarClose; // V12: UseTickEvaluation removed (was unused)

				AddDataSeries(BarsPeriodType.Minute, 30);   // [1] 30-min HTF direction
				AddDataSeries(BarsPeriodType.Minute, 5);    // [2] 5-min quality + ADX
				AddDataSeries(BarsPeriodType.Minute, 1);    // [3] management clock
				if (UseStallFastConfirm)
					AddDataSeries(BarsPeriodType.Second, StallFastConfirmSeconds); // [4] fast
					// clock for the STALL EXIT's peak-tracking/bar-count ONLY. On by default as
					// of 2026-08-24 (StallFastConfirmSeconds=3) - see header block at top of file.

				// V22 INSTRUMENTATION: always-added 15-second series for post-entry volume
				// tracking (thread: "does volume dry up before a fast loser turns?"). Added
				// LAST and its index computed dynamically so it lands correctly whether or not
				// the conditional 3-second series above was added - index 5 when
				// UseStallFastConfirm is on (the default), index 4 if it's ever off.
				volSeriesIdx = UseStallFastConfirm ? 5 : 4;
				AddDataSeries(BarsPeriodType.Second, 5);  // V26: was 15 - coverage experiment,
				                                            // see V26 header block.
			}
			else if (State == State.DataLoaded)
			{
				emaFast = EMA(Closes[0], FastPeriod);
				emaSlow = EMA(Closes[0], SlowPeriod);
				atr1    = ATR(BarsArray[0], 14);
				volAvg  = SMA(Volumes[0], VolAvgPeriod);
				emaHTF  = EMA(Closes[1], HTFEmaPeriod);
				emaHTFLong = EMA(Closes[1], HTFEmaPeriodLong);
				ema5    = EMA(Closes[2], Ema5Period);
				adx5    = ADX(BarsArray[2], AdxPeriod);
				dm5     = DM(BarsArray[2], AdxPeriod);
				volAvgFast = SMA(Volumes[volSeriesIdx], 60);  // V26: was 20 (at 15s bars = 5min).
				                                               // 60 bars at 5s = same 5min window.

				// V25: open a fresh log file for this run, OVERWRITE (not append) so its
				// content can only ever reflect the run that just produced it - see V25
				// header block for why this exists.
				// V33 LIVE-READINESS: in Strategy Analyzer the account is literally named
				// "Backtest" (see any trades export) - keep OVERWRITE there so a log can only
				// ever be the run that produced it (the V25 guarantee). On a real/sim account,
				// APPEND, so a restart mid-session doesn't erase the morning's trades from the
				// log. Every run starts with a RUN START line either way.
				//
				// V37 LIVE-SAFETY FIX - two changes, both mandatory, see V37 header block:
				//   1. SEPARATE FILE PER INSTANCE. A live/sim instance holds its StreamWriter
				//      open for the entire session. On 2026-09-18 V35 was running live on
				//      APEX5995790000003 from 09:38 and every Strategy Analyzer run of V36
				//      that day died in OnStateChange with "The process cannot access the file
				//      ... because it is being used by another process" - 0 trades, no log
				//      line, no obvious cause in the Summary. The failure is SYMMETRIC and the
				//      dangerous direction is the other one: a backtest holding the file when
				//      the live strategy is enabled would stop the LIVE BOT FROM STARTING.
				//      Backtests keep GTS_debug_log.txt (all existing analysis tooling reads
				//      that path); live/sim writes GTS_debug_log_<account>.txt.
				//   2. NEVER LET LOGGING KILL THE STRATEGY. The StreamWriter is now wrapped in
				//      try/catch - any IO failure (file locked, OneDrive sync on this synced
				//      folder, permissions, disk full) disables FILE logging only. Print()
				//      still works, LogLine already null-guards debugLogFile, and the strategy
				//      trades normally. A logging convenience must never be able to take the
				//      bot offline.
				bool isBacktest = Account == null || Account.Name == "Backtest";
				string acctName = Account != null ? Account.Name : "unknown";
				foreach (char badCh in System.IO.Path.GetInvalidFileNameChars())
					acctName = acctName.Replace(badCh, '_');
				string logPath = @"C:\Users\kunna\OneDrive\Documents\"
					+ (isBacktest ? "GTS_debug_log.txt" : "GTS_debug_log_" + acctName + ".txt");
				try
				{
					debugLogFile = new System.IO.StreamWriter(logPath, !isBacktest);
					debugLogFile.AutoFlush = true;
				}
				catch (Exception logEx)
				{
					debugLogFile = null;   // Print-only from here; LogLine null-guards this
					Print("GTS V37: file logging DISABLED - could not open " + logPath
						+ " (" + logEx.Message + "). The strategy continues trading normally.");
				}
				LogLine("===== RUN START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
					+ " | account=" + (Account != null ? Account.Name : "(null)")
					+ " | " + (isBacktest ? "backtest: log overwritten" : "LIVE/SIM: log appended")
					+ " | file=" + logPath + " =====");

				// V32 HARDENING: make the session template visible in every log. A silent
				// switch from "CME US Index Futures RTH" to "Custom" once changed which bars
				// were in scope for the whole 31mo backtest and collapsed the profit factor
				// before anyone noticed - the log now says which template produced it.
				string thName = Bars.TradingHours != null ? Bars.TradingHours.Name : "(null)";
				LogLine("SESSION TEMPLATE: " + thName + "  | primary=" + BarsPeriod.ToString()
					+ " | entry window " + EntryStartTime + "-" + LastEntryTime + " flatten " + FlattenTime);
				if (thName.IndexOf("RTH", StringComparison.OrdinalIgnoreCase) < 0)
					LogLine("*** WARNING: trading-hours template does not look like an RTH template - "
						+ "results will NOT be comparable to the confirmed baseline. ***");
			}
			else if (State == State.Terminated)
			{
				// V37: defensive here too - a throw during shutdown must not surface as a
				// strategy error either.
				if (debugLogFile != null)
				{
					try { debugLogFile.Close(); } catch (Exception) { }
					debugLogFile = null;
				}
			}
		}

		private void LogLine(string s)
		{
			Print(s);
			if (debugLogFile != null) debugLogFile.WriteLine(s);
		}

		protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
		{
			if (marketPosition == MarketPosition.Flat)
			{
				peakCyclePL = 0;
				scaledCount = 0;
				beDone      = false;
				lockedFloor = 0;
				barsSincePeak = 0;
			}
			else if (quantity == Contracts)
			{
				cumAtEntry  = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
				peakCyclePL = 0;
				lockedFloor = 0;
				barsSincePeak = 0;
				cycleEntryTime = Times[3] != null && Times[3].Count > 0 ? Times[3][0] : DateTime.MinValue;
			}
			else if (quantity > 0 && quantity < Contracts)
			{
				SetStopLoss(CalculationMode.Price, stopPrice);
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBars[0] < BarsRequiredToTrade ||
				CurrentBars[1] < Math.Max(HTFEmaPeriod, HTFEmaPeriodLong) + 2 ||
				CurrentBars[2] < Math.Max(Ema5Period, AdxPeriod) + 3)
				return;

			// ---------------- management clock (1-minute, the only exit clock in V7) ----------------
			if (BarsInProgress == 3)
			{
				int tNow = ToTime(Times[3][0]) / 100;
				if (IsFlattenDue(tNow) && Position.MarketPosition != MarketPosition.Flat)
				{
					ExitLong();
					ExitShort();
					LogLine(Times[3][0] + "  FLATTEN - session stop.");
					return;
				}
				if (Position.MarketPosition != MarketPosition.Flat)
					ManageCycle(Closes[3][0]);
				return;
			}

			// ---------------- V12 NEW: fast clock, SCOPED ONLY to the STALL EXIT ----------------
			// Deliberately does NOT touch rung checks (TP1/TP2/TP3/FullTake) or the trail - those
			// remain exclusively on the 1-minute clock above. Peak-tracking and barsSincePeak run
			// EXCLUSIVELY here (never also on the 1-minute clock) when this toggle is on - see
			// UpdatePeakAndCheckStall() and the ManageCycle() branch that skips its own
			// peak-tracking call when UseStallFastConfirm is active. Running BOTH clocks at once
			// was the specific bug that made the old rejected fast-confirm mechanism fire the
			// stall exit 3x in under 40 seconds on a live trade - this design structurally
			// prevents that by only ever having one clock own the peak/counter state at a time.
			if (UseStallFastConfirm && BarsInProgress == 4)
			{
				if (CurrentBars[4] < 2) return;
				if (Position.MarketPosition != MarketPosition.Flat)
					UpdatePeakAndCheckStall(Closes[4][0]);
				return;
			}

			// V22 INSTRUMENTATION (no logic change): post-entry volume tracking, PRE-TP1
			// window only (scaledCount==0), capped at 48 fast-bars (~4 min at 5s) - the
			// window the fast losers found in the trades export resolve within. Tests
			// whether volume genuinely dries up before a fast loser turns, or whether
			// that's noise. Print only - no branch, no return that affects a trade.
			//
			// V24 REWRITE (behaviorally identical to V22, confirmed via V25's byte-
			// identical output on this branch - kept for the cleaner, self-contained
			// design, not because it changed anything): entryFastBarNum stamps
			// CurrentBars[volSeriesIdx] the first bar it sees a non-flat position, and
			// re-arms to -1 the moment Position.MarketPosition goes Flat.
			//
			// V22/V24/V25's shared coverage ceiling (144/275, 52%) is STRUCTURAL, not a
			// bug: a cycle that goes flat (stopped out) or scales its first rung before
			// this series' own FIRST tick since entry can never be observed here -
			// entryFastBarNum never gets stamped, nothing prints. At 15-second bars
			// anything resolving in <~15s was invisible by construction. V26 tightens
			// the series to 5 seconds (see V26 header block) specifically to test
			// whether that recovers coverage.
			if (BarsInProgress == volSeriesIdx)
			{
				if (CurrentBars[volSeriesIdx] < 61) return;
				if (Position.MarketPosition == MarketPosition.Flat)
				{
					entryFastBarNum = -1;
					return;
				}
				if (entryFastBarNum < 0) entryFastBarNum = CurrentBars[volSeriesIdx];
				int fastBar = CurrentBars[volSeriesIdx] - entryFastBarNum;
				if (scaledCount == 0 && fastBar >= 0 && fastBar < 48)
				{
					bool isLongPos = Position.MarketPosition == MarketPosition.Long;
					double cycleRealized = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - cumAtEntry;
					double cyclePLv = cycleRealized + Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Closes[volSeriesIdx][0]);
					double vol15 = Volumes[volSeriesIdx][0];
					double volAvgF = volAvgFast[0];
					double volRatio15 = volAvgF > 0 ? vol15 / volAvgF : 0;

					LogLine(Times[volSeriesIdx][0].ToString("yyyy-MM-dd HH:mm:ss") + "  VOL15 " + (isLongPos ? "L" : "S")
						+ " fastBar=" + fastBar
						+ " cyclePL=" + cyclePLv.ToString("F0")
						+ " vol15=" + vol15.ToString("F0")
						+ " volAvg15=" + volAvgF.ToString("F0")
						+ " volRatio=" + volRatio15.ToString("F2"));
				}
				return;
			}

			if (BarsInProgress != 0) return;

			// ---------------- day rollover ----------------
			// V32 HARDENING: calendar-date based, not Bars.IsFirstBarOfSession. The old check
			// fires wherever the session TEMPLATE says a session starts - under a template that
			// includes overnight bars that's the Globex open, and dayStartCum/haltedForDay reset
			// at the wrong moment. This strategy is always flat outside 10:02-15:00, so "first
			// bar of a new calendar date" is the correct reset point under ANY template. Under
			// the RTH template the two are the same bar - this MUST reproduce V31 exactly.
			if (Times[0][0].Date != lastRolloverDate)
			{
				double cumNow = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
				if (lastRolloverDate != DateTime.MinValue)
					LogLine("DAY " + lastRolloverDate.ToString("yyyy-MM-dd") + " pl=" + (cumNow - dayStartCum).ToString("F0")
						+ " halt=" + (dayHaltKind == "" ? "none" : dayHaltKind) + " cycles=" + dayCycles);
				lastRolloverDate = Times[0][0].Date;
				dayStartCum  = cumNow;
				haltedForDay = false;
				dayCycles    = 0;
				dayHaltKind  = "";
			}

			double cum   = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
			double today = cum - dayStartCum
				+ (Position.MarketPosition != MarketPosition.Flat
					? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]) : 0);

			if (!haltedForDay && today <= -DailyLossHalt)
			{
				haltedForDay = true;
				dayHaltKind  = "LOSS";
				ExitLong(); ExitShort();
				LogLine("DAILY LOSS HALT (" + today.ToString("F0") + ").");
				return;
			}
			if (DailyProfitHalt > 0 && !haltedForDay && today >= DailyProfitHalt && Position.MarketPosition == MarketPosition.Flat)
			{
				haltedForDay = true;
				dayHaltKind  = "GOAL";
				LogLine("DAILY GOAL reached (+" + today.ToString("F0") + ").");
				return;
			}

			// V32 NEW, UNTESTED, default OFF: FOMC blackout. CPI/NFP land at 08:30 ET, before
			// the 10:02 entry window - no exposure. FOMC statements land at 14:00 ET: entries
			// stop at 14:00 but a position opened 13:xx is held THROUGH the statement until the
			// 15:00 flatten, with a 5-contract stop-market order under it. The backtest already
			// shows tick-through past the $400 cap (largest loss -$437); live FOMC slippage is
			// worse. From FomcBlackoutFromTime on an FOMC day: flatten anything open, take no
			// new entries. Static date list - see fomcDates.
			if (UseFomcBlackout && fomcDates.Contains(Times[0][0].Date)
				&& (ToTime(Times[0][0]) / 100) >= FomcBlackoutFromTime)
			{
				if (Position.MarketPosition != MarketPosition.Flat)
				{
					ExitLong(); ExitShort();
					LogLine(Times[0][0].ToString("yyyy-MM-dd HH:mm") + "  FOMC BLACKOUT - flatten ahead of 14:00 statement.");
				}
				return;
			}

			if (Position.MarketPosition != MarketPosition.Flat)
			{
				ManageCycle(Close[0]);
				return;
			}
			if (haltedForDay) return;

			int tBar = ToTime(Times[0][0]) / 100;
			if (!IsInEntryWindow(tBar)) return;
			if (CurrentBar - lastEntryBar < CooldownBars) return;

			double a = atr1[0];
			if (a <= 0) return;

			double erThrL   = UseLongOverrides ? ERThresholdLong  : ERThreshold;
			double adxThrL  = UseLongOverrides ? AdxThresholdLong : AdxThreshold;
			double sepL     = UseLongOverrides ? MinSepATRLong    : MinSepATR;
			int    brkBarsL = UseLongOverrides ? BreakoutBarsLong : BreakoutBars;

			// V10 CONFIRMED: PATH B is a genuinely separate, parallel long entry trigger - it does
			// NOT touch or loosen PATH A (the normal gate stack: ER/ADX/HTF/5-min quality/1-min
			// alignment/cross-breakout, all unchanged below, gated by UsePathA). A long fires if
			// EITHER path qualifies (both on by default). Path B bypasses HTF bias, 5-min quality
			// direction, 1-min EMA alignment, and the cross-or-breakout trigger entirely - it
			// only requires:
			//   1. DI+ has JUST crossed above DI- (within DiCrossGraceBars 5-min bars)
			//   2. ADX is actively RISING (adx5[0] > adx5[N bars ago]) - this also excludes a
			//      declining ADX automatically, since it can never be greater than its own
			//      earlier value.
			//   3. ADX has cleared AdxErOverrideThreshold (confirmed same result at 20 vs 22, so
			//      this now just reuses 22 - no separate "dedicated higher gate" toggle anymore).
			//   4. ER (the SAME efficiency ratio calc, just a lower bar) >= ERThresholdLongOverride.
			// Volume filter still applies to BOTH paths further below (not bypassed) - Path B is
			// independent of the trend/timing gates, not the volume-quality check.
			bool diCrossedUp = CrossAbove(dm5.DiPlus, dm5.DiMinus, DiCrossGraceBars);
			bool adxRising   = CurrentBars[2] > AdxRisingLookback && adx5[0] > adx5[AdxRisingLookback];
			double adxGateLevel = AdxErOverrideThreshold;
			bool adxGateOK   = adx5[0] >= adxGateLevel;

			double er = EfficiencyRatio(ERPeriod);
			bool erOKLong  = er >= erThrL;
			bool erOKShort = er >= ERThreshold;

			bool wantLongPathB = AllowLongs && UsePathB && diCrossedUp && adxRising
								 && adxGateOK && (er >= ERThresholdLongOverride);


			bool htfUp = true, htfDown = true;
			if (UseHTFBias)
			{
				if (UseLongOverrides)
					htfUp = emaHTFLong[0] > emaHTFLong[1] && Closes[1][0] > emaHTFLong[0];
				else
					htfUp = emaHTF[0] > emaHTF[1] && Closes[1][0] > emaHTF[0];
				htfDown = emaHTF[0] < emaHTF[1] && Closes[1][0] < emaHTF[0];
			}

			bool q5Up   = ema5[0] > ema5[2] && Closes[2][0] > ema5[0];
			bool q5Down = ema5[0] < ema5[2] && Closes[2][0] < ema5[0];
			bool adxOKLong  = adx5[0] >= adxThrL;
			bool adxOKShort = adx5[0] >= AdxThreshold;
			bool diBull = dm5.DiPlus[0] > dm5.DiMinus[0];

			bool alignUp   = emaFast[0] > emaSlow[0] && emaFast[0] > emaFast[2]
							 && (emaFast[0] - emaSlow[0]) >= sepL * a;
			bool alignDown = emaFast[0] < emaSlow[0] && emaFast[0] < emaFast[2]
							 && (emaSlow[0] - emaFast[0]) >= MinSepATR * a;

			int crossGraceL = UseLongOverrides ? CrossGraceBarsLong : CrossGraceBars;
			bool crossUp   = CrossAbove(emaFast, emaSlow, crossGraceL);
			bool crossDown = CrossBelow(emaFast, emaSlow, CrossGraceBars);

			bool useBrkLong = UseLongOverrides ? UseBreakoutTriggerLong : UseBreakoutTrigger;

			bool breakUp = false, breakDown = false;
			double hh = double.MinValue, ll = double.MaxValue;
			if (UseBreakoutTrigger || useBrkLong)
			{
				for (int i = 1; i <= Math.Max(brkBarsL, BreakoutBars); i++)
				{
					if (i <= brkBarsL)        hh = Math.Max(hh, High[i]);
					if (i <= BreakoutBars)    ll = Math.Min(ll, Low[i]);
				}
				breakUp   = useBrkLong && Close[0] > hh;
				breakDown = UseBreakoutTrigger && Close[0] < ll;
			}

			bool wantLongPathA = AllowLongs && UsePathA && erOKLong  && adxOKLong  && alignUp
							 && (crossUp   || breakUp)   && htfUp   && q5Up   && diBull;
			bool wantShort = AllowShorts && erOKShort && adxOKShort && alignDown
							 && (crossDown || breakDown) && htfDown && q5Down && !diBull;

			bool wantLong = wantLongPathA || wantLongPathB;

			if (wantLong && UseLongOverrides && MaxBreakoutDistATRLong > 0)
			{
				double overExtension = Close[0] - hh;
				if (overExtension > MaxBreakoutDistATRLong * a)
					wantLong = false;
			}

			// V8 NEW: mirror of the above for shorts - reject if price has already fallen more
			// than MaxBreakoutDistATRShort ATRs below the breakout low (ll).
			if (wantShort && MaxBreakoutDistATRShort > 0)
			{
				double overExtensionShort = ll - Close[0];
				if (overExtensionShort > MaxBreakoutDistATRShort * a)
					wantShort = false;
			}

			// V8 NEW: ADX ceiling, SHORTS ONLY. A very high, still-rising 5-min ADX can mean a
			// climactic/exhausting move rather than a fresh, sustainable trend. Deliberately NOT
			// applied to longs - AdxThresholdLong already sets its own floor (22), so a shared
			// ceiling leaves too narrow a window and was confirmed to gut long PF in testing
			// (2026-08-13: ceiling=32 dropped long PF from 1.76 to 0.72). Shorts use the base
			// AdxThreshold floor (also 22) with no override, so this ceiling only needs to coexist
			// with that single floor, not a separate long-specific one.
			if (ShortAdxCeiling > 0 && adx5[0] > ShortAdxCeiling)
			{
				wantShort = false;
			}

			// V15 NEW: minimum-ATR floor, SHORTS ONLY. 31mo analysis (see V15 header block)
			// found every entry-bar ATR band below ~2.0 points was net-negative for shorts in
			// all three years, while every band >= 2.0 was net-positive. Longs show no such
			// pattern (their low-ATR bands are ~break-even), so this is NOT applied to longs -
			// a shared floor would clip neutral long trades. 0 = off. 'a' here is atr1[0]
			// (1-min, 14-period), the same ATR the stop and the volatile-bar branch already use.
			if (MinAtrShort > 0 && wantShort && a < MinAtrShort)
			{
				wantShort = false;
			}

			// V18 CONFIRMED: block LONG entries in the noon hour. h12 longs were 28 cycles at
			// 36% win / -$1,222 vs 58-66% win every other hour (V17 instrumented run). h12
			// SHORTS are fine, so this is long-only. 0 = off (hour 0 is never in the window).
			// (V18's other long filter, UseMinAdxSlopeLong, was tested and REMOVED in V20.)
			// CONFIRMED-BEST at 8 (V35; swept in V34). LONG-ONLY regime gate. Block a long when
			// FullTake sits too far away MEASURED IN ATRs. The rungs are FIXED DOLLARS
			// (Rung1/2/3/FullTake), so what they cost in market movement depends entirely on
			// volatility: FullTake for a long is 312 * RungScaleLong = $349 = 6.99 points on 5
			// contracts, which is 5.9 ATRs away at the 2024 median long-entry ATR (1.18) but
			// only 1.8 ATRs at the 2026 median (3.88). Low ATR does not make longs LOSE - it
			// makes their targets unreachable, and they bleed out through stalls and stops
			// instead. Measured over the 31mo baseline log, long FullTake rate tracks this
			// exactly: 2024 6.1% / 2025 10.9% / 2026 19.0%, and median entry ATR separates
			// FullTake longs from the rest better than any other logged feature (3.09 vs 1.62;
			// ADX does NOT separate at all - 38.3 vs 39.5).
			//
			// Expressed as a distance-in-ATRs rather than a raw ATR floor on purpose: it stays
			// correct if Contracts, FullTake or RungScaleLong ever change, and it is the
			// regime-relative formulation the V15 header flagged as the better next step.
			// RELATED PRIOR: a raw sub-2.0 ATR floor for LONGS was tested around V15 and
			// rejected (those cycles netted about +$740 - break-even, and dropping them cost
			// that). Expect the same shape here: the blocked cycles are mostly break-even, so
			// net may fall slightly while PF and exposure improve. Judge it on PF, max drawdown
			// and trade count TOGETHER, not net alone - the APEX trailing drawdown is the hard
			// fail condition, and at threshold 5.0 the 40 blocked longs produced ZERO FullTakes
			// in 31 months.
			// SWEPT 31mo at 10/9/8/7/6/4/3.5/3/2.5 - see the V35 header block. Only 7-10
			// beat the gate-off baseline; 6 and below are NET-NEGATIVE because they start
			// eating the profitable ATR 1.00-1.17 band. Confirmed value 8 (ATR floor 0.87).
			if (MaxFullTakeAtrLong > 0 && wantLong)
			{
				double pvL     = Instrument.MasterInstrument.PointValue;
				double ftUsdL  = FullTake * (UseLongOverrides ? RungScaleLong : 1.0);
				double ftDistAtr = (Contracts * pvL > 0) ? (ftUsdL / (Contracts * pvL)) / a : 0;
				if (ftDistAtr > MaxFullTakeAtrLong)
				{
					LogLine(Times[0][0].ToString("yyyy-MM-dd HH:mm") + "  Blocked: LONG regime - FullTake "
						+ ftDistAtr.ToString("F2") + " ATRs away (max " + MaxFullTakeAtrLong.ToString("F2")
						+ ", ATR=" + a.ToString("F2") + ")");
					wantLong = false;
				}
			}

			// V36 NEW, UNTESTED, default OFF (0): LONG-ONLY directional-conviction gate. Block
			// a long when DI+ is not far enough above DI- on the 5-min series. Sits alongside
			// MaxFullTakeAtrLong above but measures a DIFFERENT dimension: that gate asks "is
			// the target reachable given volatility", this one asks "is the move actually
			// directional". DIspread was the runner-up separator in the same V33-era analysis
			// that produced the ATR gate - FullTake longs median 33.4 vs 25.9 for the rest -
			// and was left untested at the time.
			//
			// NOTE ON SIZING: those 33.4/25.9 medians come from the PRE-ATR-gate population
			// (116 longs, V33). V35's gate has since removed the lowest-ATR longs, so the
			// remaining separation may be weaker - and if DIspread correlates with ATR the two
			// gates overlap and the combined effect will be less than additive. Re-size from a
			// fresh full-window log before trusting the sweep range below.
			//
			// The existing entry logic already requires diBull (DI+ > DI-, i.e. spread > 0) for
			// Path A longs, so this is a magnitude floor on a condition already directionally
			// true - NOT a new direction requirement. Path B longs qualify via a DI CROSS and
			// may legitimately show a small spread right at the cross; watch whether this gate
			// disproportionately kills GL_PB entries, which are a deliberately separate,
			// confirmed-profitable entry path (V10).
			//
			// PROVISIONAL SWEEP (re-size first): 15 / 20 / 25 / 30. Baseline to beat is V35's
			// $19,418 / PF 1.93 / 756 trades, NOT the older $18,854. FALSIFIER: net falls with
			// no PF gain and no drawdown improvement, same standard as every other gate here.
			if (MinDiSpreadLong > 0 && wantLong)
			{
				double diSpreadL = dm5.DiPlus[0] - dm5.DiMinus[0];
				if (diSpreadL < MinDiSpreadLong)
				{
					LogLine(Times[0][0].ToString("yyyy-MM-dd HH:mm") + "  Blocked: LONG DIspread "
						+ diSpreadL.ToString("F1") + " < " + MinDiSpreadLong.ToString("F1")
						+ " [" + (wantLongPathA ? "PathA" : "PathB") + "]");
					wantLong = false;
				}
			}

			if (wantLong && NoLongEntryHour > 0 && (tBar / 100) == NoLongEntryHour)
				wantLong = false;

			if (!wantLong && !wantShort) return;

			if (UseVolumeFilter)
			{
				double vAvg = volAvg[0];
				// V10 NEW: Path B and Path C each get their OWN volume multiplier, separate from
				// Path A/short. Priority when a long qualifies via multiple paths: Path A's
				// volume bar applies first (most complete, validated setup), then Path B, then
				// Path C only if it's the SOLE reason the long qualified. Path C already checked
				double vMultDir;
				if (wantLong && wantLongPathA)      vMultDir = UseLongOverrides ? VolMultLong : VolMult;
				else if (wantLong && wantLongPathB) vMultDir = VolMultPathB;
				else                                 vMultDir = VolMult; // shorts

				if (vAvg > 0 && Volumes[0][0] < vMultDir * vAvg)
				{
					LogLine("Blocked: volume " + Volumes[0][0].ToString("F0") + " < " + (vMultDir * vAvg).ToString("F0"));
					return;
				}
			}

			double pv      = Instrument.MasterInstrument.PointValue;
			double capDist = MaxTradeLossUSD / (Contracts * pv);
			double slAtrDir = (wantLong && UseLongOverrides) ? SlAtrMultLong : SlAtrMult;

			// V10 NEW: on a volatile bar, use the tighter override multiplier instead of the
			// normal direction-specific one. This ONLY affects the ATR-based stop calculation -
			// position size (Contracts) is untouched, unlike V9's rejected approach.
			bool isVolatileEntry = UseVolatileStopTighten && a >= VolatilityATRThreshold;
			if (isVolatileEntry) slAtrDir = wantLong ? VolatileStopAtrMultLong : VolatileStopAtrMultShort;

			double slDist  = Math.Min(slAtrDir * a, capDist);
			bool   capBound = slAtrDir * a > capDist;  // V32 instrumentation: did the $ cap, not
			                                            // the ATR multiple, set this stop? When it
			                                            // does, 1R = capDist and BreakEvenR/Trail
			                                            // may be unreachable before FullTake.
			double tick    = Instrument.MasterInstrument.TickSize;
			if (slDist < 4 * tick) { LogLine("Skipped: stop too tight (" + slDist.ToString("F2") + ")."); return; }

			if (MinFirstRungRR > 0)
			{
				double dollarRisk = slDist * pv * Contracts;
				double rung1Dir = (wantLong && UseLongOverrides) ? Rung1 * RungScaleLong : Rung1;
				if (rung1Dir < MinFirstRungRR * dollarRisk)
				{
					LogLine("Skipped: Rung1 $" + rung1Dir.ToString("F0") + " < " + MinFirstRungRR.ToString("F2")
						+ "x risk $" + dollarRisk.ToString("F0") + " (reward:risk too low).");
					return;
				}
			}

			entryRiskDist = slDist;
			beDone        = false;
			lastEntryBar  = CurrentBar;

			// ===== V22 INSTRUMENTATION (no logic change) - shared entry context, both sides =====
			// For bucketing fast losers (<=4 bar, stopped for a loss) against everything else.
			// adxSlope/ema5slope/DIspread/extATR/accel3 are the same directional features V17
			// used for the long/short fast-reversal work. NEW here: volRatio (how far above the
			// volume-filter threshold the entry bar actually cleared, not just pass/fail) and
			// the entry bar's own shape - barRangeATR/barBodyATR (signed toward the trade) -
			// was this a convicted breakout bar or a weak one.
			double vAdxSlope  = CurrentBars[2] > 3 ? adx5[0] - adx5[3] : 0;
			double vEma5Slope = ema5[0] - ema5[2];
			double vSwHi = double.MinValue, vSwLo = double.MaxValue;
			for (int i = 1; i <= 30 && i <= CurrentBar; i++) { if (High[i] > vSwHi) vSwHi = High[i]; if (Low[i] < vSwLo) vSwLo = Low[i]; }
			double vExtAtr   = a > 0 ? (wantLong ? (Close[0] - vSwLo) : (vSwHi - Close[0])) / a : 0;
			double vAccel3   = (a > 0 && CurrentBar >= 3) ? (wantLong ? (Close[0] - Close[3]) : (Close[3] - Close[0])) / a : 0;
			double vDiSpread = wantLong ? (dm5.DiPlus[0] - dm5.DiMinus[0]) : (dm5.DiMinus[0] - dm5.DiPlus[0]);
			double vVolRatio = volAvg[0] > 0 ? Volumes[0][0] / volAvg[0] : 0;
			double vBarRange = a > 0 ? (High[0] - Low[0]) / a : 0;
			double vBarBody  = a > 0 ? (wantLong ? (Close[0] - Open[0]) : (Open[0] - Close[0])) / a : 0;
			string vCtx = " ADX=" + adx5[0].ToString("F1")
				+ " adxSlope=" + vAdxSlope.ToString("F1")
				+ " ema5slope=" + vEma5Slope.ToString("F2")
				+ " DIspread=" + vDiSpread.ToString("F1")
				+ " ATR=" + a.ToString("F2")
				+ " extATR=" + vExtAtr.ToString("F2")
				+ " accel3=" + vAccel3.ToString("F2")
				+ " volRatio=" + vVolRatio.ToString("F2")
				+ " barRangeATR=" + vBarRange.ToString("F2")
				+ " barBodyATR=" + vBarBody.ToString("F2")
				+ " capBound=" + (capBound ? "1" : "0")
				+ " riskPts=" + slDist.ToString("F2");

			if (wantLong)
			{
				// GL_C/GL_B = Path A cross/breakout. GL_PB = Path B (DI cross + rising ADX +
				// relaxed ER). GL_PC = Path C (DI+/ADX cross-or-rising-together + volume>1.6x).
				// Priority when multiple paths qualify on the same bar: A, then B, then C - just
				// for tagging clarity, all three still independently fire the entry.
				cycleEntrySig = wantLongPathA ? (crossUp ? "GL_C" : "GL_B") : (wantLongPathB ? "GL_PB" : "GL_PC");
				stopPrice = Close[0] - slDist;
				SetStopLoss(CalculationMode.Price, stopPrice);
				SetProfitTarget(CalculationMode.Price, Close[0] + RunnerTpAtrMult * a);
				EnterLong(Contracts, cycleEntrySig);
				dayCycles++;
				LogLine(Times[0][0].ToString("yyyy-MM-dd HH:mm") + "  LONG " + Contracts + " @" + Close[0].ToString("F1")
					+ " [" + cycleEntrySig + "] ER=" + er.ToString("F2") + vCtx
					+ (isVolatileEntry ? " VOL" : "")
					+ " SL=" + stopPrice.ToString("F1"));
			}
			else
			{
				cycleEntrySig = "GS";
				stopPrice = Close[0] + slDist;
				SetStopLoss(CalculationMode.Price, stopPrice);
				SetProfitTarget(CalculationMode.Price, Close[0] - RunnerTpAtrMult * a);
				EnterShort(Contracts, cycleEntrySig);
				dayCycles++;
				LogLine(Times[0][0].ToString("yyyy-MM-dd HH:mm") + "  SHORT " + Contracts + " @" + Close[0].ToString("F1")
					+ " [" + cycleEntrySig + "] ER=" + er.ToString("F2") + vCtx
					+ (isVolatileEntry ? " VOL" : "")
					+ " SL=" + stopPrice.ToString("F1"));
			}
		}

		private bool IsInEntryWindow(int t)
		{
			bool wraps = LastEntryTime <= EntryStartTime;
			return wraps
				? (t >= EntryStartTime || t < LastEntryTime)
				: (t >= EntryStartTime && t < LastEntryTime);
		}

		private bool IsFlattenDue(int t)
		{
			bool wraps = FlattenTime <= EntryStartTime;
			return wraps
				? (t >= FlattenTime && t < EntryStartTime)
				: (t >= FlattenTime);
		}

		private double EfficiencyRatio(int period)
		{
			if (CurrentBar < period + 1) return 0;
			double net = Math.Abs(Close[0] - Close[period]);
			double sum = 0;
			for (int i = 0; i < period; i++)
				sum += Math.Abs(Close[i] - Close[i + 1]);
			return sum > 0 ? net / sum : 0;
		}

		// ================= trade-cycle management (1-minute clock by default) =================
		// V12 NEW: peak-tracking + stall-exit check, factored out so it can run on EITHER the
		// 1-minute clock (normal) or the fast clock (when UseStallFastConfirm is on) - but never
		// both at once for the same cycle. This is what avoids the old bug where running peak-
		// tracking on both clocks simultaneously caused barsSincePeak to be driven almost
		// entirely by the faster clock, firing the stall exit far more often than intended.
		private void UpdatePeakAndCheckStall(double px)
		{
			bool   isLong = Position.MarketPosition == MarketPosition.Long;
			string entSig = !string.IsNullOrEmpty(cycleEntrySig) ? cycleEntrySig : (isLong ? "GL_C" : "GS");
			double cycleRealized = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - cumAtEntry;
			double cyclePL = cycleRealized + Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, px);

			// V33: the V32 "FullTake on this 3-second clock" test knob was REMOVED here after a
			// clean reject - see V33 header. FullTake stays on the 1-minute clock in ManageCycle.
			double priorPeak = peakCyclePL;
			if (cyclePL > peakCyclePL) peakCyclePL = cyclePL;
			if (cyclePL > priorPeak + StallToleranceUSD) barsSincePeak = 0;
			else barsSincePeak++;

			bool stallScopeOK = StallExitAppliesTo == StallExitScopeV40.Both
				|| (isLong  && StallExitAppliesTo == StallExitScopeV40.LongsOnly)
				|| (!isLong && StallExitAppliesTo == StallExitScopeV40.ShortsOnly);

			if (UseStallExit && stallScopeOK && scaledCount >= 1 && scaledCount < 3
				&& Position.Quantity > 1
				&& lockedFloor > 0 && cyclePL > lockedFloor && barsSincePeak >= StallBars)
			{
				StallScaleOut(isLong, entSig, 1, "StallExit");
				barsSincePeak = 0;
				LogLine("STALL EXIT (" + (isLong ? "long" : "short") + ", "
					+ (UseStallFastConfirm ? StallFastConfirmSeconds + "s fast clock" : "1-min clock") + "): "
					+ StallBars + " bars without a new peak (cyclePL +" + cyclePL.ToString("F0")
					+ ", peak +" + peakCyclePL.ToString("F0") + ")");
			}
		}

		private void ManageCycle(double px)
		{
			bool   isLong  = Position.MarketPosition == MarketPosition.Long;
			string entSig  = !string.IsNullOrEmpty(cycleEntrySig) ? cycleEntrySig : (isLong ? "GL_C" : "GS");
			double cycleRealized = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - cumAtEntry;
			double cyclePL = cycleRealized + Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, px);

			// V12: when the fast clock owns peak-tracking for the stall exit
			// (UseStallFastConfirm on), the 1-minute clock does NOT also update peakCyclePL/
			// barsSincePeak - only one clock ever owns that state at a time.
			bool fastClockOwnsStall = UseStallFastConfirm;
			if (!fastClockOwnsStall)
			{
				double priorPeak = peakCyclePL;
				if (cyclePL > peakCyclePL) peakCyclePL = cyclePL;
				if (cyclePL > priorPeak + StallToleranceUSD) barsSincePeak = 0;
				else barsSincePeak++;
			}

			double rs = (isLong && UseLongOverrides) ? RungScaleLong : 1.0;
			double r1 = Rung1 * rs, r2 = Rung2 * rs, r3 = Rung3 * rs, ft = FullTake * rs;

			if (cyclePL >= ft)
			{
				if (isLong) ExitLong("FullTake", entSig); else ExitShort("FullTake", entSig);
				LogLine("FULL TAKE at +" + cyclePL.ToString("F0"));
				return;
			}

			// V20: ProgressiveScale is now the ONLY exit engine - the ProtectionLadder branch
			// and the ExitMode selector were removed (never used since V5; only ever caused a
			// wasted backtest when selected by accident). Rungs -> stall exit -> runner trail.
			//
			// V21 NEW: RunnerContractsLong (default 2 = current behaviour, 3 rungs peeled, 2
			// ride) caps how many of the 5 LONG contracts are held as the runner vs peeled at
			// the rungs - SHORTS unchanged at the fixed 3-rung / 2-runner split. The combined
			// (both-sides) version of this was tested in V19 and rejected (RC=4 net -$2,785,
			// RC=5 doubled max drawdown) - but in that same sweep the LONG side alone improved
			// at RC=5 (PF 1.85->2.04, net +$1,471) while it was the SHORT book that broke.
			// Isolating it to longs only is the untested piece. NOT a swept-confirmed value -
			// sweep RunnerContractsLong 3/4/5 in isolation, watch long PF and max drawdown.
			int runnerContracts = (isLong && UseLongOverrides) ? RunnerContractsLong : 2;
			int ladderSteps = Math.Max(0, Math.Min(3, Contracts - runnerContracts));
			if (ladderSteps >= 1 && scaledCount < 1 && cyclePL >= r1) { lockedFloor = r1; ScaleOut(isLong, entSig, 1, "TP1"); return; }
			if (ladderSteps >= 2 && scaledCount < 2 && cyclePL >= r2) { lockedFloor = r2; ScaleOut(isLong, entSig, 1, "TP2"); return; }
			if (ladderSteps >= 3 && scaledCount < 3 && cyclePL >= r3) { lockedFloor = r3; ScaleOut(isLong, entSig, 1, "TP3"); return; }

			// V12: stall exit firing also moves entirely to the fast clock when
			// UseStallFastConfirm is on - see UpdatePeakAndCheckStall(), called from
			// OnBarUpdate's BarsInProgress==4 branch.
			if (!fastClockOwnsStall)
			{
				bool stallScopeOK = StallExitAppliesTo == StallExitScopeV40.Both
					|| (isLong  && StallExitAppliesTo == StallExitScopeV40.LongsOnly)
					|| (!isLong && StallExitAppliesTo == StallExitScopeV40.ShortsOnly);

				if (UseStallExit && stallScopeOK && scaledCount >= 1 && scaledCount < 3
					&& Position.Quantity > 1
					&& lockedFloor > 0 && cyclePL > lockedFloor && barsSincePeak >= StallBars)
				{
					StallScaleOut(isLong, entSig, 1, "StallExit");
					barsSincePeak = 0;
					LogLine("STALL EXIT (" + (isLong ? "long" : "short") + ", 1-min clock): " + StallBars
						+ " bars without a new peak (cyclePL +" + cyclePL.ToString("F0")
						+ ", peak +" + peakCyclePL.ToString("F0") + ")");
					return;
				}
			}
			RunnerTrail(px, isLong);
		}

		private void ScaleOut(bool isLong, string entSig, int qty, string name)
		{
			qty = Math.Min(qty, Position.Quantity);
			if (qty <= 0) return;
			if (isLong) ExitLong(qty, name, entSig);
			else        ExitShort(qty, name, entSig);
			scaledCount++;
		}

		private void StallScaleOut(bool isLong, string entSig, int qty, string name)
		{
			qty = Math.Min(qty, Position.Quantity);
			if (qty <= 0) return;
			if (isLong) ExitLong(qty, name, entSig);
			else        ExitShort(qty, name, entSig);
		}

		private void RunnerTrail(double px, bool isLong)
		{
			double a = atr1[0];
			if (a <= 0) return;
			double entry = Position.AveragePrice;
			double risk  = entryRiskDist > 0 ? entryRiskDist : SlAtrMult * a;
			double gain  = isLong ? px - entry : entry - px;

			// V32 INSTRUMENTATION: log every time this layer actually moves the stop. Hypothesis
			// (see V32 header): when the $400 cap sets the stop (capBound=1 at entry), 1R =
			// capDist = 8 pts = ~$400 on 5 contracts, which is PAST FullTake ($312) - so
			// neither branch below can fire before the cycle is already closed, and
			// BreakEvenR / TrailActivationR / TrailAtrMult are inert for those entries. The
			// native 8xATR runner target fired 3 times in 791 trades. Count these lines by
			// year and by capBound before deciding whether the runner layer needs a redesign.
			if (!beDone && gain >= BreakEvenR * risk)
			{
				double be = entry;
				if ((isLong && be > stopPrice) || (!isLong && be < stopPrice))
				{
					stopPrice = be; SetStopLoss(CalculationMode.Price, stopPrice);
					LogLine(Time[0].ToString("yyyy-MM-dd HH:mm") + "  BE SET (" + (isLong ? "long" : "short")
						+ ") gain=" + gain.ToString("F2") + "pts risk=" + risk.ToString("F2") + "pts ATR=" + a.ToString("F2")
						+ " scaled=" + scaledCount + " stop->" + stopPrice.ToString("F1"));
				}
				beDone = true;
			}
			if (gain >= TrailActivationR * risk)
			{
				double trail = isLong ? px - TrailAtrMult * a : px + TrailAtrMult * a;
				if ((isLong && trail > stopPrice) || (!isLong && trail < stopPrice))
				{
					stopPrice = trail; SetStopLoss(CalculationMode.Price, stopPrice);
					LogLine(Time[0].ToString("yyyy-MM-dd HH:mm") + "  TRAIL MOVE (" + (isLong ? "long" : "short")
						+ ") gain=" + gain.ToString("F2") + "pts risk=" + risk.ToString("F2") + "pts ATR=" + a.ToString("F2")
						+ " scaled=" + scaledCount + " stop->" + stopPrice.ToString("F1"));
				}
			}
		}

		#region Properties
		[NinjaScriptProperty, Range(1, 20), Display(Name="Contracts", GroupName="1. Mode & Rungs", Order=2)]
		public int Contracts { get; set; }
		[NinjaScriptProperty, Range(5, 5000), Display(Name="Rung 1 ($)", GroupName="1. Mode & Rungs", Order=3)]
		public double Rung1 { get; set; }
		[NinjaScriptProperty, Range(5, 5000), Display(Name="Rung 2 ($)", GroupName="1. Mode & Rungs", Order=4)]
		public double Rung2 { get; set; }
		[NinjaScriptProperty, Range(5, 5000), Display(Name="Rung 3 ($)", GroupName="1. Mode & Rungs", Order=5)]
		public double Rung3 { get; set; }
		[NinjaScriptProperty, Range(5, 10000), Display(Name="Full take ($)", GroupName="1. Mode & Rungs", Order=6)]
		public double FullTake { get; set; }
		[NinjaScriptProperty, Display(Name="USE stall exit", GroupName="1. Mode & Rungs", Order=11)]
		public bool UseStallExit { get; set; }
		[NinjaScriptProperty, Display(Name="Stall exit applies to", GroupName="1. Mode & Rungs", Order=12)]
		public StallExitScopeV40 StallExitAppliesTo { get; set; }
		[NinjaScriptProperty, Display(Name="USE stall exit fast confirm", GroupName="1. Mode & Rungs", Order=15)]
		public bool UseStallFastConfirm { get; set; }
		[NinjaScriptProperty, Range(1, 60), Display(Name="Stall fast confirm interval (sec)", GroupName="1. Mode & Rungs", Order=16)]
		public int StallFastConfirmSeconds { get; set; }
		[NinjaScriptProperty, Range(1, 20), Display(Name="Stall bars (no new peak)", GroupName="1. Mode & Rungs", Order=12)]
		public int StallBars { get; set; }
		[NinjaScriptProperty, Range(0, 200), Display(Name="Stall tolerance ($ above prior peak)", GroupName="1. Mode & Rungs", Order=13)]
		public double StallToleranceUSD { get; set; }

		[NinjaScriptProperty, Range(20, 5000), Display(Name="Max single-trade loss ($)", GroupName="2. Risk Rails", Order=1)]
		public double MaxTradeLossUSD { get; set; }
		[NinjaScriptProperty, Range(0, 5), Display(Name="Min first-rung reward:risk (0=off)", GroupName="2. Risk Rails", Order=9)]
		public double MinFirstRungRR { get; set; }
		[NinjaScriptProperty, Display(Name="USE FOMC blackout (V32, static date list)", GroupName="2. Risk Rails", Order=21)]
		public bool UseFomcBlackout { get; set; }
		[NinjaScriptProperty, Range(0, 2359), Display(Name="FOMC blackout from (HHmm)", GroupName="2. Risk Rails", Order=22)]
		public int FomcBlackoutFromTime { get; set; }
		[NinjaScriptProperty, Range(20, 5000), Display(Name="Daily loss halt ($)", GroupName="2. Risk Rails", Order=2)]
		public double DailyLossHalt { get; set; }
		[NinjaScriptProperty, Range(0, 5000), Display(Name="Daily profit halt ($, 0=off)", GroupName="2. Risk Rails", Order=3)]
		public double DailyProfitHalt { get; set; }
		[NinjaScriptProperty, Range(0.5, 5), Display(Name="Stop (x ATR, capped by $)", GroupName="2. Risk Rails", Order=4)]
		public double SlAtrMult { get; set; }
		[NinjaScriptProperty, Range(1, 20), Display(Name="Runner far target (x ATR)", GroupName="2. Risk Rails", Order=5)]
		public double RunnerTpAtrMult { get; set; }
		[NinjaScriptProperty, Range(0.1, 5), Display(Name="Trail activates at (R)", GroupName="2. Risk Rails", Order=6)]
		public double TrailActivationR { get; set; }
		[NinjaScriptProperty, Range(1, 10), Display(Name="Trail distance (x ATR)", GroupName="2. Risk Rails", Order=7)]
		public double TrailAtrMult { get; set; }
		[NinjaScriptProperty, Range(0.1, 5), Display(Name="Runner BE at (R)", GroupName="2. Risk Rails", Order=8)]
		public double BreakEvenR { get; set; }
		[NinjaScriptProperty, Display(Name="USE volatile-bar stop tighten", GroupName="2. Risk Rails", Order=9)]
		public bool UseVolatileStopTighten { get; set; }
		[NinjaScriptProperty, Range(0.5, 20), Display(Name="Volatility ATR threshold (points)", GroupName="2. Risk Rails", Order=10)]
		public double VolatilityATRThreshold { get; set; }
		[NinjaScriptProperty, Range(0.5, 3), Display(Name="Volatile-bar stop mult LONG (x ATR)", GroupName="2. Risk Rails", Order=11)]
		public double VolatileStopAtrMultLong { get; set; }
		[NinjaScriptProperty, Range(0.5, 3), Display(Name="Volatile-bar stop mult SHORT (x ATR)", GroupName="2. Risk Rails", Order=12)]
		public double VolatileStopAtrMultShort { get; set; }

		[NinjaScriptProperty, Range(10, 200), Display(Name="Efficiency ratio period", GroupName="3. Trend Gates", Order=1)]
		public int ERPeriod { get; set; }
		[NinjaScriptProperty, Range(0.05, 0.9), Display(Name="Efficiency ratio threshold", GroupName="3. Trend Gates", Order=2)]
		public double ERThreshold { get; set; }
		[NinjaScriptProperty, Display(Name="Use 60-min HTF bias", GroupName="3. Trend Gates", Order=3)]
		public bool UseHTFBias { get; set; }
		[NinjaScriptProperty, Range(5, 200), Display(Name="HTF EMA period (30m)", GroupName="3. Trend Gates", Order=4)]
		public int HTFEmaPeriod { get; set; }
		[NinjaScriptProperty, Range(5, 200), Display(Name="5-min quality EMA period", GroupName="3. Trend Gates", Order=5)]
		public int Ema5Period { get; set; }
		[NinjaScriptProperty, Range(5, 50), Display(Name="ADX period (5m)", GroupName="3. Trend Gates", Order=6)]
		public int AdxPeriod { get; set; }
		[NinjaScriptProperty, Range(5, 50), Display(Name="ADX threshold", GroupName="3. Trend Gates", Order=7)]
		public double AdxThreshold { get; set; }
		[NinjaScriptProperty, Display(Name="Use breakout trigger", GroupName="3. Trend Gates", Order=8)]
		public bool UseBreakoutTrigger { get; set; }
		[NinjaScriptProperty, Range(3, 60), Display(Name="Breakout lookback bars", GroupName="3. Trend Gates", Order=9)]
		public int BreakoutBars { get; set; }

		[NinjaScriptProperty, Range(3, 100), Display(Name="Fast EMA (1m)", GroupName="4. Signal", Order=1)]
		public int FastPeriod { get; set; }
		[NinjaScriptProperty, Range(5, 300), Display(Name="Slow EMA (1m)", GroupName="4. Signal", Order=2)]
		public int SlowPeriod { get; set; }
		[NinjaScriptProperty, Range(0, 1), Display(Name="Min EMA separation (x ATR)", GroupName="4. Signal", Order=3)]
		public double MinSepATR { get; set; }
		[NinjaScriptProperty, Range(1, 10), Display(Name="Cross grace bars", GroupName="4. Signal", Order=4)]
		public int CrossGraceBars { get; set; }
		[NinjaScriptProperty, Range(0, 30), Display(Name="Cooldown bars", GroupName="4. Signal", Order=5)]
		public int CooldownBars { get; set; }
		[NinjaScriptProperty, Display(Name="Use volume filter", GroupName="4. Signal", Order=6)]
		public bool UseVolumeFilter { get; set; }
		[NinjaScriptProperty, Range(3, 200), Display(Name="Volume avg period", GroupName="4. Signal", Order=7)]
		public int VolAvgPeriod { get; set; }
		[NinjaScriptProperty, Range(1.0, 5.0), Display(Name="Volume multiplier", GroupName="4. Signal", Order=8)]
		public double VolMult { get; set; }

		[NinjaScriptProperty, Display(Name="USE long-only overrides", GroupName="6. Long Filters", Order=1)]
		public bool UseLongOverrides { get; set; }
		[NinjaScriptProperty, Display(Name="USE Path A (original gate stack)", GroupName="6. Long Filters", Order=21)]
		public bool UsePathA { get; set; }
		[NinjaScriptProperty, Range(0.05, 0.9), Display(Name="LONG efficiency ratio threshold", GroupName="6. Long Filters", Order=2)]
		public double ERThresholdLong { get; set; }
		[NinjaScriptProperty, Display(Name="USE Path B (DI cross + rising ADX + relaxed ER)", GroupName="6. Long Filters", Order=22)]
		public bool UsePathB { get; set; }
		[NinjaScriptProperty, Range(0, 60), Display(Name="Path B ADX gate", GroupName="6. Long Filters", Order=23)]
		public double AdxErOverrideThreshold { get; set; }
		[NinjaScriptProperty, Range(0.01, 0.30), Display(Name="Secondary ER threshold (long)", GroupName="6. Long Filters", Order=24)]
		public double ERThresholdLongOverride { get; set; }
		[NinjaScriptProperty, Range(2, 5), Display(Name="ADX rising lookback bars (secondary ER)", GroupName="6. Long Filters", Order=25)]
		public int AdxRisingLookback { get; set; }
		[NinjaScriptProperty, Range(1, 10), Display(Name="DI+/DI- fresh-cross lookback bars", GroupName="6. Long Filters", Order=26)]
		public int DiCrossGraceBars { get; set; }
		[NinjaScriptProperty, Range(5, 50), Display(Name="LONG ADX threshold", GroupName="6. Long Filters", Order=3)]
		public double AdxThresholdLong { get; set; }
		[NinjaScriptProperty, Range(0, 1), Display(Name="LONG min EMA separation (x ATR)", GroupName="6. Long Filters", Order=4)]
		public double MinSepATRLong { get; set; }
		[NinjaScriptProperty, Range(1.0, 5.0), Display(Name="LONG volume multiplier", GroupName="6. Long Filters", Order=5)]
		public double VolMultLong { get; set; }
		[NinjaScriptProperty, Range(1.0, 5.0), Display(Name="Path B volume multiplier", GroupName="6. Long Filters", Order=28)]
		public double VolMultPathB { get; set; }
		[NinjaScriptProperty, Range(0.5, 5), Display(Name="LONG stop (x ATR)", GroupName="6. Long Filters", Order=6)]
		public double SlAtrMultLong { get; set; }
		[NinjaScriptProperty, Range(0.2, 2.0), Display(Name="LONG rung scale (x all targets)", GroupName="6. Long Filters", Order=7)]
		public double RungScaleLong { get; set; }
		[NinjaScriptProperty, Display(Name="USE breakout trigger (long)", GroupName="6. Long Filters", Order=9)]
		public bool UseBreakoutTriggerLong { get; set; }
		[NinjaScriptProperty, Range(1, 15), Display(Name="LONG cross grace bars", GroupName="6. Long Filters", Order=11)]
		public int CrossGraceBarsLong { get; set; }
		[NinjaScriptProperty, Range(5, 200), Display(Name="LONG HTF EMA period (30m)", GroupName="6. Long Filters", Order=12)]
		public int HTFEmaPeriodLong { get; set; }
		[NinjaScriptProperty, Range(0, 5), Display(Name="LONG max breakout distance (x ATR, 0=off)", GroupName="6. Long Filters", Order=13)]
		public double MaxBreakoutDistATRLong { get; set; }
		[NinjaScriptProperty, Range(3, 60), Display(Name="LONG breakout lookback bars", GroupName="6. Long Filters", Order=10)]
		public int BreakoutBarsLong { get; set; }
		[NinjaScriptProperty, Range(0, 23), Display(Name="LONG no-entry hour (0=off, e.g. 12)", GroupName="6. Long Filters", Order=32)]
		public int NoLongEntryHour { get; set; }
		[NinjaScriptProperty, Range(0, 20), Display(Name="LONG max FullTake distance (x ATR, 0=off, V34)", GroupName="6. Long Filters", Order=34)]
		public double MaxFullTakeAtrLong { get; set; }
		[NinjaScriptProperty, Range(0, 100), Display(Name="LONG min DI spread (0=off, V36)", GroupName="6. Long Filters", Order=35)]
		public double MinDiSpreadLong { get; set; }
		[NinjaScriptProperty, Range(1, 5), Display(Name="LONG runner contracts (ride to FullTake, V21)", GroupName="6. Long Filters", Order=33)]
		public int RunnerContractsLong { get; set; }

		[NinjaScriptProperty, Range(0, 5), Display(Name="SHORT max breakout distance (x ATR, 0=off)", GroupName="7. Short Filters", Order=1)]
		public double MaxBreakoutDistATRShort { get; set; }
		[NinjaScriptProperty, Range(0, 100), Display(Name="SHORT ADX ceiling (0=off)", GroupName="7. Short Filters", Order=2)]
		public double ShortAdxCeiling { get; set; }
		[NinjaScriptProperty, Range(0, 20), Display(Name="SHORT min ATR (points, 0=off)", GroupName="7. Short Filters", Order=3)]
		public double MinAtrShort { get; set; }

		[NinjaScriptProperty, Display(Name="Allow long trades", GroupName="4. Signal", Order=9)]
		public bool AllowLongs { get; set; }
		[NinjaScriptProperty, Display(Name="Allow short trades", GroupName="4. Signal", Order=10)]
		public bool AllowShorts { get; set; }

		[NinjaScriptProperty, Range(0, 2359), Display(Name="Entry start (HHmm)", GroupName="5. Session", Order=1)]
		public int EntryStartTime { get; set; }
		[NinjaScriptProperty, Range(0, 2359), Display(Name="Last entry (HHmm)", GroupName="5. Session", Order=2)]
		public int LastEntryTime { get; set; }
		[NinjaScriptProperty, Range(0, 2359), Display(Name="Flatten (HHmm)", GroupName="5. Session", Order=3)]
		public int FlattenTime { get; set; }
		#endregion
	}
}
