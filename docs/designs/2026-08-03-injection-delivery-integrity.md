# Injection Delivery Integrity — REVISED after E4a falsification

**Status:** Proposed v2 (design only; E5 probe series must run before any C#)
**Supersedes:** v1 of this document (Element A in-band Ctrl-KEYUP prelude — **falsified by
E4a**; Element B always-on delivery verification — **deleted by owner decision**).
**Evidence base:** E1/E3 probes (2026-08-03), E4a falsification runs (2026-08-03, extended
`probe.ps1` with `-Prelude/-PreludeVks/-PreludePair/-SendMode`), production incidents
943055c8 (terminal) and the Notepad `Even     we worked` case.

---

## 0. Evidence record

### 0.1 Original proven facts (unchanged)

1. **Mechanism (E1/E3):** a Ctrl-class modifier in the TARGET's message-translation context
   transforms injected `KEYEVENTF_UNICODE` (VK_PACKET) letters into C0 control codes at the
   target's retrieval time. Letters vanish or execute editor commands; spaces survive.
2. **The async guard is structurally blind (E3):** queue-only (PostMessage) Ctrl produced full
   corruption with all 17 `GetAsyncKeyState` samples clean and `SendInput` accepting everything
   — the exact production signature (`outcome=completed inject_chunks=17/17`).
3. **No send-time sampling bounds the blast radius (E1 smear):** corruption extends beyond the
   modifier's queue-order window. "Better sampling" is ruled out by evidence.
4. **The seed is unknown and must be treated as unfindable** (pedal exonerated by HID capture).
5. Frequency ~2/2000; transcript always archived correctly; the corruption is silent.

### 0.2 E4a falsification (2026-08-03, production host, Win11 Notepad, probe-owned tab)

The v1 fix — Ctrl-class KEYUPs prepended inside every chunk's `SendInput` batch — was built on
the model *"translation context = target thread keystate, updated in retrieval order by key
transitions in the stream."* **E4a falsified both the fix and the model:**

| # | Delivery | Attack | Result |
|---|---|---|---|
| 1 | VK_PACKET, no prelude (control) | E3 posted-Ctrl bracketing chunks 2–6 | **CORRUPTED** (`"Even ssss     eeeeeeee...d"`) — attack reproduces |
| 2 | VK_PACKET + KEYUP prelude (0x11,0xA2,0xA3 per batch) | E3 | **CORRUPTED** (`"Even ttt's"` + rest dead). In-band Ctrl-ups did NOT clear posted-seed poison; corruption persisted past the attack's own posted Ctrl-UP |
| 3 | VK_PACKET + KEYUP prelude | E1 real Ctrl pulse | **CORRUPTED, worse than E1 alone** (`"Even ooough"` + rest dead). The prelude *did* clear the async table (guard read Ctrl-down at chunk 2 only) yet corruption ran to end of stream |
| 4 | VK_PACKET + full Ctrl DOWN+UP pair prelude (V1) | E3 | **CORRUPTED, much worse** — repeated-char storms (`"cccccccccccccouple hhhhh"`). Transient synthetic Ctrl-downs amplify damage |
| 5 | **WM_CHAR posted** to focused child (`PostMessage(hwnd, WM_CHAR, unit, 1)` per unit, same chunking/pacing, no SendInput) (V2) | E3 | **INTACT — perfect text.** WM_CHAR bypasses VK_PACKET translation entirely; transformation-immune to the phantom class **by construction** |
| 6 | WM_CHAR posted (V2b) | E1 real Ctrl pulse (90 ms, spans 5 guard boundaries) | **CORRUPTED — new mode:** character scrambling/transposition, 3/3 runs, different each time (`"I ou learned from yall"`). Letters survive; order scrambles around real-input interleave. Mechanism unclear |

**Empirical matrix (delivery × attack class):**

| Delivery | E3 class (phantom/queue — guard-INVISIBLE) | E1 class (real/async — guard-VISIBLE when boundary-spanning) |
|---|---|---|
| SendInput VK_PACKET (status quo) | dies (silent) | dies (smear) — but production guard halts+parks when the pulse spans a ≥1 boundary sample |
| + KEYUP prelude / + pair prelude | dies, sometimes worse — **FALSIFIED** | dies, worse |
| WM_CHAR posted | **immune by construction** | scrambles (V2b) — but this class is async-visible and production already halts on it; residual = sub-period pulses only |

**Model lesson:** the tidy retrieval-order keystate model was wrong or incomplete; extra
synthetic Ctrl traffic aggravates rather than clears. The revision below therefore refuses to
depend on ANY model of the poison state: it selects the delivery channel whose immunity is
**structural** (no VK→char translation step executes at all) and **demonstrated** (row 5), and
validates every remaining unknown by probe before implementation.

### 0.3 Production-relevance calibration of V2b

The V2b pulse (90 ms real Ctrl) spans 5–6 inter-chunk guard samples; production's existing
async guard (`GuardedInjectionRun.cs:49-50`) halts and parks on exactly that class. The probe
has no halt logic. **The true residual for the real-input class, under either delivery channel,
is a sub-period pulse: a real Ctrl transition fully inside one ~14 ms inter-sample window.**
Today that residual corrupts VK_PACKET too (C0 transformation, silent). Whether it scrambles
WM_CHAR is an open empirical question → E5a(iv).

---

## 1. Decisions record

- **D0 (owner, this revision):** Element B (always-on post-injection delivery verification) is
  **deleted from v1** — owner chose the council minority endpoint over always-on readback.
  Nothing in this design performs target readback.
- **D1, D2, D4, D5 (council, as amended):** stand as amended by the council record. This doc is
  the engineering reflection of those rulings; the authoritative wording lives in the council
  record. (Owner: please confirm the D-number mapping against §7 item 6.)
- **Council-adopted items surviving the redesign:**
  - *Atomicity/ordering pin concept:* retained in spirit. The v1 pin was "prelude+text atomic in
    one SendInput batch"; the v2 equivalent is the **fixed-target, in-order, stop-on-first-failure
    pin** on the WM_CHAR sender (§4, tests §6.1-2/3) — every unit of a run goes to the SAME hwnd
    captured at send start, in code-unit order, halting the run on the first refused post.
  - *Commit probe + docs:* the extended `probe.ps1` (with `-Prelude/-PreludeVks/-PreludePair/
    -SendMode` and the E5 additions below) and this document are to be committed as the evidence
    trail. Ensure the committed probe copy is the EXTENDED one from the E4a runs.
  - *Doc rewording:* applied — v1's model-confident language ("the exact state domain and
    ordering domain where the corruption occurs") is retired; claims below are labeled
    *structural*, *demonstrated*, or *open (probe)*.

---

## 2. Revised architecture

### 2.1 The three candidate endpoints, argued from the matrix

**(a) Hybrid — SendInput VK_PACKET primary, WM_CHAR only when 〈trigger〉.** Rejected: **no
principled trigger exists.** The E3 class is invisible to every sampling domain (proven fact
0.1-2/3), so a "switch to WM_CHAR now" trigger would require either detection (impossible
without readback — deleted) or target classification (curated lists — owner-rejected as primary
architecture). Any hybrid of this polarity is unreachable-by-construction or
blocklist-in-disguise.

**(b) WM_CHAR primary, VK_PACKET fallback (observability-gated).** The only delivery in the
matrix that survives the invisible class — and it survives **by construction**, not by model:
posted `WM_CHAR` *is* the post-translation product; there is no VK_PACKET→WM_CHAR translation
step for phantom keystate to poison. Row 5 demonstrates it against the live E3 attack on the
production incident-2 target class (RichEdit). Its weaknesses are all **enumerable and
probeable**: V2b interleave scrambling (residual class only — §0.3), per-target compatibility
(some window procs only handle WM_KEYDOWN and never see a posted WM_CHAR), focus/ordering
semantics shifts. The fallback is **mechanism-availability, not classification**: WM_CHAR
requires a target hwnd; when the focused child is unobservable
(`GetGUIThreadInfo.hwndFocus == 0`), fall back to today's VK_PACKET path — never worse than
status quo, no curated list, and stamped honestly in telemetry (`inject_via=`).

**(c) No delivery change; accept and document.** Honest, minimum-change — but it walks away
from a demonstrated immune channel while the residual is silent, trust-destroying corruption.
Rejected as the *starting* endpoint; **retained as the pre-registered NO-GO endpoint**: if the
E5 compatibility matrix (§3) fails on a major target class, (b) collapses into either a silent
no-op on those targets (worse than rare corruption) or a fallback list (rejected), and (c)
becomes the correct, evidence-honest outcome.

### 2.2 Chosen: (b), contingent on E5 — "message-delivered text"

Everything around delivery is **unchanged** (minimum viable change): the guarded run loop,
async physical-modifier/mouse halt, foreground halt, chunking, pacing, elevation park,
park-on-0, pending-paste/pill flows, `InjectionText.ForPaste`. Only the **inside of the
`sendChunk` seam** changes, plus a one-time focused-child capture at send start:

1. **At send start** (after the elevation check, before the preludes): capture the focused
   child of the foreground window's GUI thread (`GetWindowThreadProcessId` →
   `GetGUIThreadInfo.hwndFocus`) — validated working on Win11 Notepad by the E3/V2 probes.
2. **Per chunk:** deliver each UTF-16 code unit as `WM_CHAR` to that **fixed** hwnd (mode —
   `PostMessage` vs `SendMessageTimeout` — decided by E5a, not by argument). Same 8-unit
   chunks, same 14/16 ms pacing (its rationale shifts from bleed-ceiling to guard-cadence —
   the guard still needs a bounded sampling period to observe halt gestures).
3. **Fallback:** focused child unobservable → today's VK_PACKET send, unchanged.
4. **First refused delivery** (`PostMessage`=FALSE / `SendMessageTimeout` timeout or failure)
   → `SendFailed` → existing park+pill flow. Note this is an honesty *upgrade* over SendInput:
   UIPI blocks posted messages to higher-IL windows **loudly** (FALSE/access-denied), where
   SendInput reports success while delivering nothing.
5. **Telemetry (not Element B):** stamp which channel delivered — `inject_via=wmchar|vkpacket`
   in the timing line and `InjectionRunReport`. No readback, no verification, no verdict — pure
   channel provenance so future incidents are interpretable. (Owner confirmation §7 item 4.)

**What this buys, per attack class:**

- **E3/phantom class (both production incidents' best explanation):** immune by construction;
  demonstrated (row 5). The class no guard can ever see stops mattering.
- **E1/real class, boundary-spanning:** unchanged — the existing async guard halts and parks
  (it samples *physical* state, which this class by definition occupies).
- **E1/real class, sub-period residual:** today = silent C0 transformation. Under WM_CHAR:
  possibly intact, possibly V2b-style scrambling — **E5a(iv) decides**; even the bad outcome
  replaces one corruption mode with another of comparable rarity, and cannot C0-execute editor
  commands (letters survive in every V2b run).
- **Mid-stream focus change:** structural improvement to the AD-1 bleed story — posted messages
  go to the captured hwnd, so **zero** characters can bleed into the newly focused window
  (today's bound: ≤ 1 in-flight chunk). The per-chunk foreground guard still halts and parks,
  so at most ~1 chunk lands in the *old* window — where the user had been typing anyway.
- **New failure mode accepted:** a target whose window proc ignores posted WM_CHAR types
  *nothing*, silently (Completed reported, field empty). This is the compatibility bet E5b
  exists to size, and the NO-GO trigger if a major class fails.

### 2.3 Explicitly rejected in this revision

| Option | Why |
|---|---|
| Any synthetic-modifier prelude (KEYUP-only or pair), any composition | **Empirically falsified** (E4a rows 2–4); pair variant amplifies damage |
| Better/thread-scoped sampling | Unchanged from v1: ruled out by E1 smear (fact 0.1-3) |
| Detection-triggered or class-triggered hybrid (a) | No principled trigger exists (§2.1a) |
| Always-on readback verification | Deleted by owner (D0) |
| Control-class-specific delivery (`EM_REPLACESEL`, `WM_SETTEXT`, `WM_PASTE`) | Per-control-class API selection is blocklist-shaped; WM_CHAR is the one universal "here is a typed character" message every `TranslateMessage` consumer already handles; clipboard paste additionally asserts the proven poison class (Ctrl+V) and clobbers |
| Curated target lists as primary architecture | Owner-rejected (unchanged) |

---

## 3. E5 probe series — ordered, with go/no-go criteria (BEFORE any C#)

All runs on the production host via the existing `.trycycle-lifecycle-probe/probe.ps1` harness
(extend with `-Experiment E5x`); verbatim output appended to this doc's evidence section.
Probe hygiene: probe-owned temp-file tabs / scratch windows only (E3's unsaved-tab casualty
must not recur).

### E5a — Delivery-mode × attack matrix on RichEdit (Notepad). **DECIDES THE MODE.**

For each mode `M ∈ {PostMessage WM_CHAR, SendMessageTimeout WM_CHAR (SMTO_ABORTIFHUNG,
150 ms/unit)}`, run the 134-unit production string, 8-unit chunks, 14 ms period:

| Cell | Attack | Go criterion |
|---|---|---|
| (i) | none (clean) | INTACT, byte-identical, ×3 runs |
| (ii) | E3 posted-Ctrl bracketing chunks 2–6 | INTACT ×3 (re-confirm row-5 immunity under M) |
| (iii) | E1 real 90 ms Ctrl pulse (V2b repro) | informational — production halts this class; record whether SendMessage serialization eliminates the scramble |
| (iv) | **real Ctrl tap fully inside one inter-chunk window** (< 14 ms, guard-invisible — THE residual class) | INTACT required for a clean GO; scramble ⇒ record as documented residual (still ≥ status quo, which C0-transforms under the same tap) and weigh in §7 item 2 |

**Mode selection rule (pre-registered):** prefer `PostMessage` if it passes (i),(ii),(iv) —
it can never block the pipeline. Take `SendMessageTimeout` only if it passes where PostMessage
fails, and then cap exposure: first timeout ⇒ abort ⇒ `SendFailed` park (worst-case added wall
time = one 150 ms timeout, never per-unit accumulation).

### E5b — Compatibility matrix (the GO/NO-GO for the whole architecture)

Winner mode from E5a, clean run + E3-attack run, into each target; criterion per target:
INTACT and identical to a VK_PACKET clean baseline.

Targets (major classes; production incident classes marked):
1. **Windows Terminal** (production incident 1 class) — also record conhost
2. **Chromium** — omnibox AND a textarea (covers Electron/VS Code by engine)
3. **Word** (TSF-heavy path)
4. **Win32 classic `EDIT`** (e.g. a Run dialog / property sheet)
5. `EDIT` with `ES_PASSWORD`
6. Win11 Notepad RichEdit (done — row 5; re-run under winner mode)

**GO:** all of 1–4 intact. **NO-GO:** any of 1–4 ignores or mangles WM_CHAR ⇒ fall to
pre-registered endpoint (c): no delivery change; document the matrix and the residual; close.
(Target 5 informational: password fields that ignore posted WM_CHAR are recorded as a known
limitation, not a NO-GO — dictating into password fields is marginal.)

### E5c — Unicode integrity under WM_CHAR

Surrogate pairs (emoji), combining marks, CRLF/`\t`, posted as consecutive per-unit WM_CHARs:
compose correctly on targets 1, 2, 6? Criterion: identical rendering to VK_PACKET baseline.
Failure on surrogates ⇒ chunker's straddle logic still applies; failure beyond that ⇒ weigh as
partial NO-GO for affected content class.

### E5d — Focused-child observability rate + top-level fallback behavior

For each E5b target: does `GetGUIThreadInfo` yield a nonzero `hwndFocus`? (Sizes how often the
VK_PACKET fallback path fires.) Also: post WM_CHAR to the TOP-LEVEL hwnd where `hwndFocus`=0 —
does anything sane happen? (Informs whether the fallback should try top-level first; default
design says no — straight to VK_PACKET.)

### E5e — Loud-failure confirmation on elevated target

`PostMessage`/`SendMessageTimeout` WM_CHAR to an elevated window ⇒ confirm FALSE/error
(documents the belt-and-suspenders behind the existing elevation park).

### E5f — Ordering-with-real-input characterization

Type plain letters physically (no modifiers — the guard does not halt on letter keys) during a
WM_CHAR injection: confirm the injected text lands contiguously and user characters land after
(posted-queue priority), on targets 1, 2, 6. Feeds §7 item 3 (accept the semantics change?).

**Sequencing:** E5a → E5b → (E5c ∥ E5d ∥ E5e ∥ E5f). Implementation begins only after E5a GO +
E5b GO, with the mode fixed by E5a's rule.

---

## 4. Component-level specification (contingent on E5 GO)

### 4.1 New files

**`src/Winpepper.Platform/Injection/WmCharSender.cs`** — Windows plumbing, thin.

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>
/// Message-delivered text: posts each UTF-16 code unit as WM_CHAR to a FIXED
/// target hwnd captured at send start. Chosen over SendInput VK_PACKET because
/// posted WM_CHAR is the post-translation product — there is no VK->char
/// translation step for phantom (queue-domain) modifier state to poison
/// (E4a row 5, INTACT under the live E3 attack; every SendInput variant died,
/// E4a rows 1-4). Mode (PostMessage vs SendMessageTimeout) pinned by probe E5a.
/// Stops at the FIRST refused unit and reports false so the guarded run maps it
/// to SendFailed -> park (UIPI and dead windows fail LOUDLY here, unlike
/// SendInput's silent success). Fixed-target pin: never re-resolves the hwnd
/// mid-run — this is what makes mid-stream focus-change bleed into a newly
/// focused window structurally zero (supersedes the AD-1 <=1-chunk bound for
/// the wmchar path).
/// </summary>
internal static class WmCharSender
{
    // Mode constants pinned by E5a; TimeoutMs applies only in SendMessageTimeout mode.
    internal const int TimeoutMs = 150;

    /// <summary>Deliver one chunk's code units, in order, to hwnd. False on first failure.</summary>
    public static bool TrySendChunk(long hwnd, string chunk);
}
```

**`src/Winpepper.Platform/Injection/FocusedChildProbe.cs`** — Windows plumbing.

```csharp
/// <summary>Focused child of the foreground window's GUI thread, captured ONCE at
/// send start (GetWindowThreadProcessId -> GetGUIThreadInfo.hwndFocus). 0 = unknown
/// (off-Windows, call failure, no focused child) => caller falls back to the
/// VK_PACKET send path (mechanism-availability fallback, never a target list).</summary>
internal static class FocusedChildProbe
{
    public static long Capture(long foregroundHwnd);
}
```

**`src/Winpepper.Platform/Injection/DeliveryChannel.cs`**

```csharp
/// <summary>Which channel actually delivered a run. Telemetry/provenance only
/// (inject_via=); NOT delivery verification (Element B deleted, owner D0).</summary>
public enum DeliveryChannel { VkPacket, WmChar }
```

### 4.2 Modified files

**`src/Winpepper.Platform/Injection/SendInputNative.cs`** — add `GetWindowThreadProcessId`,
`GetGUIThreadInfo` (+struct), `PostMessageW` and/or `SendMessageTimeoutW` per E5a.

**`src/Winpepper.Platform/Injection/TextInjector.cs`**

- Seam changes (preserving the ctor `Func`-seam style, `TextInjector.cs:111-127`):

```csharp
Func<long, long>?        focusedChildProbe = null,  // default: FocusedChildProbe.Capture
Func<long, string, bool>? sendChunkTo      = null,  // default: WmCharSender.TrySendChunk
Func<string, bool>?      sendChunk         = null   // EXISTING VK_PACKET seam, now the fallback
```

- `TryInjectGuardedDetailed` flow: after the elevation check, `targetChild =
  _focusedChildProbe(hwndAtSendStart)`; choose the per-chunk sender:
  `targetChild != 0 ? c => _sendChunkTo(targetChild, c) : _sendChunk` (channel recorded).
  Everything else — preludes, `GuardedInjectionRun.Execute`, pacing, halt mapping — unchanged.
- `NeutralizeHeldModifiers` (`TextInjector.cs:285-307`) **stays for both channels**: it guards
  the *physical*-hold case (async-visible, its original job) which E4a did not falsify; the
  wmchar path keeps it because a physically held Ctrl still poisons the VK_PACKET *fallback*
  path and any target that checks `GetKeyState` while handling WM_CHAR (open — E5a(iv)).
- Doc corrections: `ChunkCodeUnits` comment gains the wmchar zero-bleed note;
  `InterChunkPauseMs` rationale re-anchored on guard cadence (bleed-ceiling rationale retained
  for the VK_PACKET fallback path only).

**`src/Winpepper.Platform/Injection/InjectionRunReport.cs`**

```csharp
public readonly record struct InjectionRunReport(
    InjectionRunOutcome Outcome, int ChunksTotal, int ChunksSent, int PacingWaitMs,
    DeliveryChannel Via = DeliveryChannel.VkPacket);
```

**`src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs`** — `public string? InjectVia
{ get; set; }`, rendered `inject_via=` via `AppendOptStr` after `inject_chunks`
(`DictationTimingSummary.cs:133-135`). No new budget.

**`src/Winpepper.App/Hosting/PipelineHost.cs`** — both call sites (`:958-965`, `:1544-1549`)
and `TryPastePending` (`:452-515`) stamp `timing.InjectVia`/log it. **No new outcome handling**:
WM_CHAR failure surfaces as the existing `SendFailed` → park + pill + ErrorBus
(`PipelineHost.cs:1008-1022`) — honest failure through existing flows, per constraint.

**Deleted from v1 spec (never implemented):** `TranslationContextClear`, `DeliveryVerdict`,
`DeliveryVerdictDecider`, `FocusedTextReader.TryReadText`, `BuildChunkBatchInputs`,
`HistoryEntry.DeliveryStatus`, `inject_delivery=`/`inject_verify=` fields.

### 4.3 Data flow

```
TryInjectGuardedDetailed(text)
  ├─ foreground hwnd (park-on-0)                    [existing]
  ├─ elevation check (park)                         [existing]
  ├─ targetChild = focusedChildProbe(hwnd)          [NEW, once]
  │     ├─ != 0 → sender = WM_CHAR to targetChild   [Via=WmChar]
  │     └─ == 0 → sender = SendInput VK_PACKET      [Via=VkPacket, status quo]
  ├─ NeutralizeHeldModifiers (physical, conditional) [existing, unchanged]
  ├─ mouse-release prelude                          [existing]
  ├─ GuardedInjectionRun per chunk                  [existing loop, unchanged]
  │    guard sample → foreground check → sender(chunk)
  │    first refused unit → SendFailed → park+pill  [existing flow]
  └─ InjectionRunReport { …, Via } → timing inject_via= → history via timing (unchanged schema)
```

---

## 5. Failure-mode table (revised)

| Scenario | Status quo (VK_PACKET) | This design (WM_CHAR primary) |
|---|---|---|
| **Phantom Ctrl, pre-stream seed** (both production incidents; E3) | silent corruption, `Completed N/N` | **immune by construction** (E4a row 5); no translation step executes |
| **Phantom Ctrl asserted mid-stream** (posted) | corruption + unbounded smear (E1-smear analogue) | **immune** — row 5's attack brackets chunks 2–6, i.e. covers mid-stream assertion |
| **Real modifier held / boundary-spanning pulse** | guard halts, parks full text | unchanged (guard samples physical state; this class occupies it) |
| **Real sub-period Ctrl tap** (guard-invisible residual) | silent C0 transformation | **open — E5a(iv)**: intact (clean GO) or scramble (documented residual; no worse than today, and cannot C0-execute) |
| **Mid-stream focus change** | halt + park; ≤ 1 chunk bleeds into NEW window (AD-1) | halt + park; **zero bleed into new window** (fixed-target pin); ≤ 1 chunk lands in the OLD window |
| **Target ignores posted WM_CHAR** | n/a | **new mode: silent nothing typed.** Bounded by E5b GO/NO-GO on major classes; residual documented for exotic targets; transcript always in history |
| **Focused child unobservable** | n/a | VK_PACKET fallback — exactly status-quo risk, stamped `inject_via=vkpacket` |
| **Hung target** | SendInput queues; text lands whenever | PostMessage mode: posts succeed, processed on unhang. SMTO mode: first 150 ms timeout → `SendFailed` → park+pill. Pipeline never blocks unbounded |
| **Elevated target** | pre-check parks; SendInput would lie (silent success) | pre-check parks; posted messages additionally **fail loudly** (E5e) — defense in depth |
| **User types letters during injection** | interleaves unpredictably mid-text | injected text lands contiguously; user chars land after (posted-queue priority) — characterized by E5f, owner-accepted per §7 item 3 |
| **UIPI/dead window mid-stream** | silent success | first FALSE → `SendFailed` → park+pill (honesty upgrade) |
| Off-Windows | parks (`NoForeground`) | unchanged; probe default 0 → fallback seam, tests seam everything |

---

## 6. Test plan

### 6.1 Linux units (green before every commit; xUnit v3 in-process runner, no `dotnet test`)

1. **`TextInjectorGuardedTests` additions/updates** (existing seam style preserved):
   - channel selection: `focusedChildProbe` → nonzero ⇒ every chunk delivered through
     `sendChunkTo` with the **same** hwnd (fixed-target pin — the zero-bleed property);
     probe → 0 ⇒ every chunk through the legacy `sendChunk` seam; `Via` propagates.
   - `focusedChildProbe` called exactly once, before the first chunk, and **not at all** on
     park paths that precede it (NoForeground/BlockedElevated).
   - first `sendChunkTo` false ⇒ `SendFailed`, remaining chunks unsent (maps through the
     existing `GuardedInjectionRun` contract — its tests unchanged).
   - existing tests updated mechanically where they seam `sendChunk` (unchanged semantics);
     halt/pacing/prelude pins untouched.
2. **`GuardedInjectionRun`**: no changes — pure driver untouched (pin: file diff empty).
3. `DictationTimingSummaryTests`: `inject_via` rendered when set, omitted when null, position
   after `inject_chunks`.

### 6.2 Windows gate additions (`./scripts/windows-gate.sh`; in-proc windows only)

4. **`WmCharSenderWindowsTests`** — in-proc STA thread hosting an `EDIT` child:
   `TrySendChunk` lands text verbatim, in order; unit order pin with a multi-chunk run;
   surrogate-pair chunk lands correctly (per E5c result). SMTO mode only: non-pumping window ⇒
   false within ≤ 2× `TimeoutMs` wall-clock (pipeline-never-hangs pin).
5. **`FocusedChildProbeWindowsTests`** — resolves the in-proc focused child; returns 0 for a
   destroyed hwnd.
6. Loud-failure pin: post to a destroyed hwnd ⇒ `TrySendChunk` false (maps to park).

### 6.3 Live probes

E5a–E5f (§3) are **pre-implementation gates** with pre-registered criteria — the same
evidentiary standard that caught E4a. Post-implementation: one confirmation run of the E3
attack against the real winpepper binary on the host (the production-shaped end-to-end proof),
plus first-week field check that `inject_via=wmchar` dominates and no delivery regressions
surface on daily targets.

---

## 7. Open decisions for the owner

1. **Fallback polarity when the focused child is unobservable:** VK_PACKET fallback
   (recommended — never worse than status quo, no list) vs park (fail-safe but regresses
   targets where `GetGUIThreadInfo` fails yet typing works today). E5d sizes how often this
   fires.
2. **Residual acceptance if E5a(iv) scrambles:** sub-period real-Ctrl taps are guard-invisible
   under any design; today they C0-transform silently. If WM_CHAR scrambles under them instead,
   accept as documented residual (recommended) or treat as NO-GO?
3. **Ordering semantics:** user keystrokes during an injection land AFTER the injected text
   (posted-queue priority) instead of interleaving. Accept? (Recommended: yes — contiguous
   injected text is more predictable than today's interleave; E5f characterizes it.)
4. **`inject_via` telemetry field:** confirm this channel-provenance stamp is acceptable under
   the Element-B deletion (it involves no readback or verification — it records which path
   winpepper itself took).
5. **NO-GO endpoint confirmation:** if E5b fails on any of targets 1–4, the pre-registered
   outcome is architecture (c) — no delivery change, matrix + residual documented, close.
   Confirm there is no appetite for a target-list-gated partial rollout (assumed no, per your
   blocklist ruling).
6. **D-numbering:** confirm §1's mapping of D1/D2/D4/D5 against the council record wording.
