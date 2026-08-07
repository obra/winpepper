# Injection Delivery Integrity — v3.1 (pinned strategy ladder)

**Status:** Proposed v3.1 (design only; per-rung probe gates in §3 must pass before a rung
enters the default ladder; no implementation yet)
**Supersedes:** v2 of this document (deleted). v2's causal model — a phantom Ctrl-class
modifier in the target's translation context as THE production root cause — was
**falsified for the Notepad incident class by E6** (2026-08-05/06): corruption reproduces
at ~100% with no modifier involvement of any kind. v2's pre-registered mode-selection
rule ("take SendMessageTimeout only if it passes where PostMessage fails") survived and
was exercised: that is exactly what happened.
**Evidence base:** E1/E3/E4a probes (2026-08-03, salvaged below), E6 fresh-tab matrix +
E7 SMTO runs (2026-08-05/06, `.trycycle-lifecycle-probe/freshprobe.ps1`), E8 prior-art
research (2026-08-06), production incidents 943055c8 (terminal) and the Notepad
`Even     we worked` class, owner usage report (incidents occur in freshly opened
Notepad sessions, voice-only workflow).

---

## 0. Evidence record

### 0.1 E6 — fresh-tab Notepad matrix (2026-08-05/06, production host)

Per run: a NEW probe-owned tab in Win11 Notepad, ZERO prior key events into it, inject
the 134-unit production string in winpepper's exact send shape (8-unit chunks, 14 ms),
read back via WM_GETTEXT, then read the target thread's believed keystate
(AttachThreadInput+GetKeyState, post-measurement). Physical keyboard silent throughout
(async Ctrl clean at every chunk boundary); target believed-keystate clean (all-up).

| Delivery | Result | Reading |
|---|---|---|
| VK_PACKET @ 14 ms (production) | **0/16 intact** | broken at ~100% on cold tabs |
| VK_PACKET @ 100 ms | 0/4 | not a rate problem |
| VK_PACKET + closed-loop (poll WM_GETTEXTLENGTH per chunk, 600 ms cap) | 0/4; one run stalled 15/17 chunks and still corrupted | backpressure cannot reach the input queue |
| VK_PACKET after a real arrow-key tap | 0/4 | a healing keystroke does NOT fix it |
| VK_PACKET after Ctrl+A/Delete (old probe's prep) | 0/4 | ditto |
| VK_PACKET after 3 s settle | 0/4 | time does not fix it |
| Posted WM_CHAR | 2/3 intact; 1 chunk-transposition (`it's ouple obeen a cf`) | NOT immune on cold tabs — qualifies v2 row 5 |
| **SendMessageTimeout WM_CHAR** (SMTO_ABORTIFHUNG, 150 ms/unit) | see E7 | survives |
| **EM_REPLACESEL per chunk** | **3/3 intact** | survives; fastest (one message per chunk) |

Corruption shape: dropped spans + autorepeat-style duplication (`sssssssssss`,
`ooooooof`), spaces and the stream head surviving — the production
`Even     we worked` signature is a mild instance.

**Critical historical correction:** the E-series never ran a no-attack control
(v2's E5a(i) was planned, never executed). E1/E3 proved injected Ctrl is *sufficient*
to corrupt; E6 proves it is *not necessary* for the Notepad class.

### 0.2 E7 — SMTO WM_CHAR characterization (2026-08-06)

| Cell | Result |
|---|---|
| Soak, fresh tabs, clean | **10/12 intact**; 2 no-delivery anomalies (below) |
| E3 posted-Ctrl attack bracketing chunks 2–6 | **2/2 delivered runs intact** (+1 no-delivery anomaly) — phantom-Ctrl immunity carries |
| Surrogate pairs + combining mark + tab | 2/2 intact |
| Timing | 630–1170 ms per 134 units ≈ 4.7–8.7 ms/unit (vs ~250 ms VK_PACKET; bounded) |

**No-delivery anomaly (open, E7c):** 3/17 SMTO runs read back an EMPTY document with
anomalously fast injection (330–410 ms vs ~800–900 normal) and ZERO SMTO
failures/timeouts. Signature is consistent with the harness capturing a stale/wrong
focused-child hwnd (a live non-editor control accepting WM_CHAR trivially), not with
character corruption. Winpepper's production flow re-resolves foreground per injection
and guards per chunk, but this class motivates the capture-hardening requirement in §2.

### 0.3 E8 — prior-art research (2026-08-06, two web-research passes; sources cited in
session record)

1. **No supported cross-process "insert text" API exists on Windows.** TSF insertion is
   in-process only (requires shipping a registered IME/text service); UIA TextPattern is
   read-only by design; `InputInjector` is SendInput with packaging friction. Microsoft
   documents SendInput into arbitrary apps as best-effort ("not guaranteed to work").
2. **Industry pattern** (Dragon, PhraseExpress, espanso, Handy): key simulation +
   clipboard-paste + per-app override table + an own-window fallback. Nobody uses
   synchronous per-char messaging as primary. AutoHotkey and pywinauto both name
   **EM_REPLACESEL** as the reliable+fast path for edit controls.
3. **espanso's formal verdict on our exact Notepad bug** (issue #1481): closed with no
   code fix; per-app route to the clipboard backend. Root-cause corroboration: SO
   76177119's accepted answer pins Notepad's interruption on its autocorrect/spellcheck
   pipeline; a mouse-move unsticks a stalled stream.
4. **No reusable open-source per-app compatibility list exists.** The complete union
   across espanso (3 Windows patches), Beeftext (5 entries, dormant), Talon community
   (2 usable) is ~10 rules; they barely overlap, and where they do (mintty) they
   contradict each other. Win11 Notepad appears on none of them. There is no upstream
   to adopt or track — confirming the no-curated-list architecture (§1).
5. **Best-in-class clipboard transaction design** (Handy): delayed-render +
   WM_RENDERFORMAT read-receipt + GetClipboardSequenceNumber guard + Microsoft
   ignore-formats — and it STILL has open clipboard-clobber/race bugs. Recorded here
   because it informed the decision to exclude clipboard from automatic delivery (§1).

### 0.4 Salvaged from v1/v2 (still valid, still constraining)

1. **Injected Ctrl is sufficient to corrupt** (E1 real pulse, E3 posted/queue-only) —
   and the E3 class is invisible to GetAsyncKeyState sampling; no send-time sampling
   bounds the blast radius (E1 smear).
2. **Synthetic modifier preludes make corruption worse** (E4a rows 2–4: KEYUP prelude
   persisted poison; DOWN+UP pair prelude amplified to repeated-char storms). Any
   prelude-based fix remains falsified.
3. Posted WM_CHAR was intact under an active E3 attack on a **warm** tab (E4a row 5) —
   now qualified by E6: on cold tabs it can transpose without any attack.
4. The transcript is always archived correctly; corruption is delivery-time only.
5. Elevation/UIPI: posted/sent messages fail loudly where SendInput lies silently.

### 0.5 Root cause (evidence-graded)

**Demonstrated:** Windows 11 Notepad mishandles sustained synthetic text input into a
cold (never-typed-in) tab, independent of modifier state and delivery rate —
asynchronous channels (SendInput VK_PACKET, posted WM_CHAR) are dropped/duplicated/
reordered; synchronous delivery (SendMessageTimeout WM_CHAR, EM_REPLACESEL) survives.
Warmed tabs mostly do not exhibit this. The mechanism is target-side: a standalone
PowerShell probe with no winpepper code reproduces it at ~100%.

**Attributed (informed, consistent with AutoHotkey t=116789, StackOverflow 76177119,
espanso #1481):** deferred, wake-driven drain plus re-entrant first-text
initialization (RichEditD2D/TSF/spellcheck-autocorrect) in Notepad's island/InputHost
pipeline. The attribution is not load-bearing: the fix depends only on the
demonstrated sync-vs-async split.

**Open — why incidence rose recently (owner, 2026-08-06):** the owner attests Notepad
did not change when incidents began; a winpepper-side change is suspected to have
altered *exposure* (not the mechanism — the standalone probe rules that out). Candidate
suspects to investigate: streaming-ASR latency reduction (injection now starts sooner
after a fresh tab gains focus), chunking/pacing changes, prewarm changes. Investigation
item: correlate incident onset dates with winpepper's injection-path and latency
commits. Until resolved, treat "what we changed" as unknown, not as refuted.

**Demoted, not retired:** the phantom-Ctrl class (E1/E3) remains a real, sufficient
corruption mechanism (best available explanation for the Terminal incident) and every
enabled rung must stay immune to it (E7 attack cell: SMTO WM_CHAR is).

---

## 1. Decisions record

- **D0 (owner, v2):** no always-on readback/delivery verification. Stands.
- **Owner (2026-08-06):** fix must be code-side — no user-facing workarounds.
- **Owner (2026-08-06):** no curated per-app target lists (reaffirmed after E8 §0.3.4
  showed none exist to adopt anyway). Routing is by capability probing only.
- **Owner (2026-08-06):** **no automatic clipboard use, ever.** The clipboard is shared
  user state; even best-in-class transactions (E8 §0.3.5) retain clobber/race/history
  residuals. The clipboard appears in exactly one place: the existing failure flow,
  where injection stops and the status pill shows the transcript with **click-to-copy**
  — the user explicitly chooses to spend their clipboard.
- **Owner (2026-08-06):** **pin ALL validated delivery channels as swappable
  strategies** behind one contract, routed by a simple ordered ladder whose order is
  settings-configurable. Rationale: two falsified causal models in one week and a
  channel believed immune that wasn't — regressions must be recoverable by
  reordering/disabling a rung, not by ripping out code.
- **Owner (2026-08-06):** mid-run delivery failures stop the run and show the
  click-to-copy pill. **Never re-route to another rung mid-text** (risk: duplicated
  partial text).
- Synthetic-modifier preludes: falsified (E4a), rejected permanently.

---

## 2. Architecture: a pinned ladder of delivery strategies

### 2.1 The strategy contract

One small interface per channel; everything else (chunking, guarded run loop, async
physical-modifier/mouse halt, per-chunk foreground halt, pacing, elevation handling,
the failure pill, `InjectionText.ForPaste`) is shared and unchanged:

```csharp
internal interface IDeliveryStrategy
{
    DeliveryChannel Channel { get; }
    /// Capability gate: can this strategy deliver to this target? Runs ONCE at
    /// send start, before any text is sent. Must be side-effect-free on the target
    /// document (probing messages only).
    bool CanDeliver(long foregroundHwnd, long focusedChildHwnd);
    /// Deliver one chunk. False = delivery refused/failed -> the run STOPS
    /// (SendFailed -> pill with click-to-copy). Never throws for target-side failure.
    bool TrySendChunk(long targetHwnd, string chunk);
}
```

### 2.2 The rungs

| Rung | Channel | Gate | Send | Status |
|---|---|---|---|---|
| 1 | `EmReplaceSel` | focused child answers `EM_GETSEL` sanely via SMTO (capability probe — E9a pins the exact predicate) | one `EM_REPLACESEL` per chunk (SMTO, 150 ms) | E6: 3/3 intact; **gate probe pending (E9a)** |
| 2 | `WmCharFenced` | focused child observable + stable (double sample) | post chunk's units as WM_CHAR, then one synchronous `WM_NULL` fence per chunk (SMTO, 150 ms) | **probe pending (E9b)** — expected ~8× fewer round-trips than rung 3 |
| 3 | `WmCharSmto` | focused child observable + stable | `SendMessageTimeout` WM_CHAR per unit (SMTO_ABORTIFHUNG, 150 ms) | **validated (E7)**: 10/12 + attack-immune + surrogates |
| 4 | `VkPacket` | always | today's SendInput path, unchanged | status quo floor |

Not a rung, by owner decision: clipboard (see §1). Not a rung: posted WM_CHAR without
a fence (E6: transposition).

**Focused-child capture hardening (all rungs above 4):** at send start, sample
`GetGUIThreadInfo.hwndFocus` twice, ≥30 ms apart, after foreground stabilization; if
the samples disagree or either is 0, rungs 1–3 gate out and rung 4 runs. Motivated by
the E7c no-delivery anomalies: a wrong-target send must degrade to status-quo
delivery, not silently type into the wrong control.

### 2.3 Routing

- Walk the ladder in order; the first rung whose gate passes delivers the whole run.
  Gates run once, before any text is sent. No scoring, no heuristics, no app lists.
- **Ladder order lives in settings** (e.g. `"injectionChannels":
  ["emReplaceSel","wmCharFenced","wmCharSmto","vkPacket"]`), hardcoded default = the
  enabled-rung order above. A field regression is fixed by reordering or removing a
  rung in settings — no release. Unknown names are logged and skipped; an empty or
  invalid list falls back to the hardcoded default.
- **Pinned ≠ enabled:** every rung ships as code with tests, but a rung enters the
  DEFAULT ladder only after its §3 probe gate passes. Until then it is present but
  not in the default order (available for explicit opt-in via settings).
- **Mid-run failure:** first refused/timed-out chunk → stop the run → the existing
  failure flow: the status pill shows the transcript with click-to-copy. No mid-run
  re-routing. (The transcript is also always in history — nothing is ever lost.)

### 2.4 Telemetry (channel provenance, not verification — D0 stands)

- `inject_via=<channel>` in the dictation timing line and `InjectionRunReport`.
- `inject_gates=<comma-list>` of rungs that gated out and why (e.g.
  `emReplaceSel:no-em,wmCharFenced:focus-unstable`) so a field regression's routing is
  diagnosable from the log alone.
- No readback, no verdicts.

### 2.5 Failure-mode table (deltas from status quo)

| Scenario | Status quo (VK_PACKET) | This design |
|---|---|---|
| Fresh Notepad tab, voice-only (THE production class) | ~100% corruption (E6) | intact via rung 1 or 3 (E6/E7); rung 1 also ~2× faster than status quo |
| Phantom Ctrl (E3 class, guard-invisible) | silent corruption | rungs 1–3 immune (no translation step); rung 3 verified under attack |
| Real modifier held / boundary-spanning | guard halts; pill | unchanged |
| Hung target | text lands whenever | first 150 ms timeout → stop → click-to-copy pill; never blocks unbounded |
| Focused child unobservable/unstable | n/a | rung 4 (status quo), stamped in telemetry |
| Target ignores WM_CHAR / EM_ messages | n/a | gates route past it; residual "accepts but displays nothing" class bounded by §3 compat gates |
| A rung regresses in the field | n/a | settings reorder/removal; telemetry shows which rung delivered |
| User types during injection | interleaves mid-text | injected text contiguous; user chars after (E9d characterizes; owner sign-off) |

---

## 3. Pre-implementation gates (per rung)

- **E9a — EM_REPLACESEL gate probe:** pin the exact capability predicate (candidate:
  SMTO `EM_GETSEL` returns TRUE with a sane selection range AND target class/behavior
  checks) and verify: fresh Notepad tab (expect pass+intact), Windows Terminal /
  Chromium / non-edit controls (expect gate-out, no side effects), classic EDIT
  (expect pass+intact). Undo/caret behavior recorded.
- **E9b — fenced WM_CHAR probe:** posted chunk + WM_NULL fence on fresh Notepad tabs
  (expect: no transposition, intact ≥ SMTO's rate) + timing (expect ≥4× faster than
  per-unit SMTO).
- **E9c — no-delivery anomaly (was E7c):** reproduce the empty-readback class with
  focus-capture logging; confirm the double-sample rule converts it to a rung-4 route.
- **E9d — typing-interleave semantics (was E7d/E5f):** characterize; owner signs off.
- **E9e — compatibility matrix (was E7b):** for every rung in the default ladder:
  Windows Terminal, Chromium (omnibox + textarea), Word (or record not-installed),
  classic EDIT, warm Notepad regression. Criterion per target: intact and identical
  to the VK_PACKET warm baseline, or a clean gate-out to a lower rung. **NO-GO for a
  rung** on any silent-wrong-delivery; the rung then ships default-disabled.
- Probe harnesses exist: `.trycycle-lifecycle-probe/freshprobe.ps1` (rungs 2–4 cells),
  `compat.ps1` + `edithost.ps1` (E9e targets).

---

## 4. Test plan (contingent on §3)

- **Linux seam tests:** per-strategy contract tests; ladder selection (first passing
  gate wins; gates called once; order honored from settings; unknown/empty settings
  fall back to default); mid-run failure stops without re-route and maps to the
  existing SendFailed → pill flow; `inject_via`/`inject_gates` rendering.
- **Windows gate (in-proc):** per-strategy delivery tests against a hosted EDIT child
  (verbatim order, surrogate pairs, non-pumping window → false within ≤ 2× timeout);
  focused-child double-sample rule; destroyed-hwnd → loud false.
- **Post-implementation:** one live E6-shape confirmation run per default-ladder rung
  (fresh Notepad tab, real binary), then first-week `inject_via`/`inject_gates` field
  review.

---

## 5. Open decisions for the owner

1. Default ladder order once E9a/E9b land: is `emReplaceSel` first (fastest, most
   invasive of control state) or `wmCharFenced` first (more universal, slightly
   slower)?
2. Does `wmCharSmto` remain in the default ladder once `wmCharFenced` is validated,
   or stay pinned-but-default-disabled as the spare?
3. E9d interleave semantics sign-off.
4. Word in E9e: required, or not-applicable on this host?
