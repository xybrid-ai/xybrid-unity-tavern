# Table reactions — decision-model experiment (milestone 1)

When the player talks to one member of Master Wu's table, the other members who can hear it may react
nonverbally (a brief look, a nod or shake, a face expression, or nothing) to what the line *means*: who it
concerns and whether it praises, criticises or worries about them. Scope: Wu / Shara / Varien, player
utterances only, no speaking turns, memory, relationships or movement.

Status: working, toggleable prototype. Results and recommendation at the end.

## Try it

1. Start the decision model (dev-only sidecar, see [Model path](#model-path)):
   `tools/d1-sidecar/start.sh` (port 8187; reuses space-jarv's pinned llama.cpp + d1 GGUF).
2. Play `demo_xybrid`, walk to the corner table and talk to Master Wu (E).
3. **F3** cycles the mode: `Off` → `Rules` → `D1Sidecar` → `GemmaBaseline`. **F4** toggles the overlay.
4. Overlay (top-right) shows per line: session/turn, who could hear it, judge + model, status, queue/inference
   time, what each listener's interpretation was, what the policy asked for, and what the animation systems
   accepted. Same data in the Console (`[TableReactions]`) and `Logs/table-reactions.jsonl` (also `reply`
   records with time-to-first-token per mode).

`TableReactionDirector` sits on `Revamp_Life/Table_Chatter_WuSharaVarien` with `Mode = Off` (scene default).

Dev tooling:
- `./scripts/unity.sh method Tavern.Social.Editor.TableReactionEval.Run` — offline comparison of all judges on
  the fixed scenarios (Editor closed). Options: `-tableEvalJudges rules,d1,gemma -tableEvalSplits test,dev
  -tableEvalRepeats 3`. Writes `Logs/table-reactions-eval/eval-<utc>.{md,json}`.
- `tools/table-reactions-eval/`: dotnet harness over the same sources (`dotnet run -- --judge d1 --split dev`,
  `cd Tests && dotnet test`) for a fast loop without Unity.
- Play mode menu `Tavern > Table Reactions > Run Live Check` (≈4 min, every mode + stale checks) or
  `… Frames Only` (≈1.5 min, Off + D1 with frame captures). Output in `Logs/table-reactions-live/`.

## Model path

| Judge | What runs | Notes |
| --- | --- | --- |
| **d1** (dedicated decision model) | `LiquidAI/d1-2B-GGUF` Q8_0 (rev `e968f657`, sha256 `073313d9…6738c`) on upstream llama.cpp **b11960** macOS arm64 (Metal), `POST /v1/systemone` | **Outside Unity, not Xybrid.** Answers read off logits over closed options; nothing is decoded. The GGUF backend scores each named question in its own pass. |
| **Gemma** (structured-judgment baseline) | `gemma4npc-e2b-q4` through Xybrid 0.16.0, greedy, ≤80 tokens, GBNF from `XybridClient.JsonSchemaToGbnf` | The dialogue model asked to fill a JSON form. Generative, no confidence. **Not a decision model.** |
| **Rules** | aliases + praise/criticism/concern word lists | No pronouns, no negation, no context. |
| **Off** | existing `TableConversation.WatchDialogue` | Content-blind: listeners watch the addressee, a random one glances at the player every 3–7 s. |

**Update 2026-11-16:** Xybrid v0.18.0 (2026-11-12) added d1 inference. See [Round 3](#round-3-2026-11-16-d1-through-xybrid-v0190):
it gives the same labels but takes ~8 s per line on CPU. The paragraph below describes the 0.16.0 state the
experiment ran on.

**Why d1 couldn't run through Xybrid 0.16.0.** Xybrid 0.16.0's C# binding only exposes text generation
(`InferenceResult` = text, tool calls, metrics; no logits or choice scores). The Rust core has a
`ChoiceScorer`, but the FFI refuses such models:
`crates/xybrid-ffi-facade/src/lib.rs:586-597` `ensure_ffi_supported` → "choice scoring is not available through
the FFI bindings yet". The d1 `GgufDecision` / `lfm3-d1` protocol and a C# decision binding exist only on
unmerged branches (`origin/feat/decision-*`, `origin/chore/decision-bindings-spike`). The Tavern also runs a
local Phonon (`tts_zzz`) native build, so adopting those branches needs a new native with both features.

## Pipeline

```
DialogueManagerV2 ── PlayerUtteranceSubmitted(session, turn, npc, text) ──┐
                  └─ NpcLineCompleted(session, turn, npc, text, ttft)  ───┤
TableReactionDirector                                                     ▼
  hearing (Unity only): table member ∧ head within 3.5 m of the player ∧ not the addressee ∧ not speaking
  → UtteranceEvent (immutable) → ReactionGate (one live ticket; supersede / close / expire)
  → IListenerJudge (Rules | d1 | Gemma) → JudgmentValidator
  → ReactionPolicy (Resources/TableReactions/wu_table.json) → ReactionPlan per listener
  → ListenerReactionPerformer → NPCLookAt / NPCGestures / NPCFace → ReactionTrace
```

- **Event** (`Assets/Scripts/Social/UtteranceEvent.cs`): session + turn ids, speaker kind (Player now; Npc
  reserved), speaker/addressee ids, text, ≤4 recent lines, timestamp, eligible listener ids. Collections are
  copied; invalid events are rejected.
- **Contract** (`ListenerJudgment.cs`): per listener `referent` ∈ member ids ∪ {none, unclear},
  `meaning` ∈ {praise, criticism, concern, neutral, unclear}, `attention` yes/no. d1 probabilities are kept
  for the trace only; the 0.5 attention gate is an uncalibrated operating point. Unknown ids, bad labels,
  malformed output, missing or extra listeners and non-finite numbers are rejected (no reaction).
- **Judges** see only the table profile (facts condensed from `Resources/NPCPrompts/*.md`), the line, ≤4
  recent lines, speaker, addressee and listeners — never an NPC's dialogue history. The utterance is a data
  field marked untrusted. d1: one HTTP request per event (referent, meaning, one yes/no per listener).
- **Policy** (`ReactionPolicy.cs` + JSON): (listener, relation self/peer/addressee/none, meaning) →
  weighted options, including authored "nothing". Attention "no", neutral/unclear-about-others and missing
  rules mean no reaction. 20% skip chance, 8 s cooldown per listener, ≤1 sequence per listener per event,
  staggered starts (≥0.3 s apart), no back-to-back repeat of an option.
- **Execution**: gaze uses `NPCLookAt.LookAtFor(…, LookPriority.Awareness)`, above the table's Social watch
  look and below Dialogue; it expires and the watch look resumes. Gestures go through `NPCGestures.Play`;
  refusals are logged ("declined"), not retried. Faces go through `NPCFace.ShowEmotion` (transient slot).
  Never the addressee, never a speaking NPC, no movement. The watcher's random player glance is suppressed
  for that member while a reaction plays.
- **Timeliness**: judgments for a closed session, a superseded turn, a gone listener, or arriving later than
  8 s are discarded; each delayed step re-checks the gate. d1 runs in parallel with the reply (separate
  process). Gemma starts only after this turn's reply is final, via
  `XybridModelService.RunBackgroundAsync`, which never starts while a player-facing request waits.

Dialogue fixes made for this experiment (both confirmed in code before the change):
- **Greeting overlap**: input stays locked until the greeting is final, so a reply can't race the greeting.
- **Context clearing**: `XybridDialogueProvider` bumps a per-NPC epoch on `ClearNPCContext`; a reply that
  finishes after its dialogue closed no longer lands in the cleared history.

## Evaluation

Fixtures: `Assets/Scripts/Social/Tests/Fixtures/table-reaction-scenarios.json`, 22 test + 8 dev cases,
sha256 `be01b09f…df885`, written and hashed (2026-11-04 00:25 UTC) before any model saw them. Each case lists
every acceptable referent/meaning and per-listener react yes/no/any. Wording was iterated on the **dev split
only**: d1 variants B–F → E; Gemma prompt v1 → v2 plus a field-order fix (below), and one rejected
option-order variant.

Test split, final run `Logs/table-reactions-eval/eval-20261104-021319.md` (M5 Max, 64 GB; d1 sidecar idle;
Gemma loaded in the batch Editor; 3 repeats, labels identical across repeats for every judge):

| judge | referent | meaning | both | attention agrees | false reactions (should not react) | missed reactions (should react) | latency p50 / p95 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| rules | 64% | 77% | 50% | 75% | 2/12 | 3/8 | <1 ms |
| **d1** | **82%** | **82%** | **64%** | **90%** | **0/12** | 2/8 | **232 / 265 ms** |
| Gemma | 18% | 64% | 14% | 90% | 0/12 | 2/8 | 767 / 826 ms |

"False/missed reactions" use the policy's deterministic *could react* (attention + rule + valid target),
ignoring randomness.

- **d1 right**: explicit names, the indirect "hides behind technique" → Varien, pronouns with context
  ("He moves like water" → Varien, praise), "your other student" → Varien, concern, every unrelated line
  (no attention, no reaction), both injection lines (never praise; no reaction).
- **d1 wrong**: "Shara told me she's already surpassed you" → praise (expected criticism/neutral),
  "She never stops talking, does she?" → praise, "I'm not saying Varien is a coward" → unclear (no
  reaction), "hides behind technique" → neutral, "One of your students seems troubled" → Varien (expected
  unclear), "Shara says your technique is the best" (to Varien) → unclear.
- **Gemma**: names the addressee as referent for almost every line, even after the order fix, so the policy
  aims reactions at the wrong person (e.g. Varien nods at Wu when *Varien* is praised). Its attention flags
  are good, which is why that column looks fine. Not fixed by option order (dev check).
- **Rules**: "What's good to drink" → praise, "Mind if I sit with you" → praise of Wu (2 false reactions);
  no pronouns, so the pronoun cases and "she never stops talking" get nothing.

Found while wiring Gemma: **Xybrid 0.16's JSON-schema → GBNF emits properties in alphabetical order**, not
schema order (its docs say "map order"). The model then had to answer `attention_*` before `referent`. Field
names carry numeric prefixes (`1_referent`, `2_meaning`, `3_attention_*`) to fix the generation order.
Worth an SDK issue.

## Live check (Play mode, 2026-11-04 02:28 UTC)

Same 5 lines to Wu in each mode, listeners Shara (0.7 m) and Varien (1.1 m) in earshot.

| line | Off | Rules | d1 | Gemma |
| --- | --- | --- | --- | --- |
| Varien seems like your most disciplined student. | — | Shara looks at Varien, nods, Happy; Varien glances away, Worried | Varien looks up, Worried | Varien: authored nothing (ref = Wu) |
| What's good to drink here? | — | none | none | **Varien nods at Wu** (ref = Wu, "praise") |
| Shara told me she's already surpassed you. | — | none (neutral) | interpreted praise; both skipped by the 20% skip chance | Shara: skip chance |
| I'm not saying Varien is a coward. | — | Shara shakes head, Suspicious; Varien skipped | Varien attends, meaning unclear → no rule → none | **Shara nods, Happy** |
| Looks like rain is coming over the hills. | — | none | none | Shara: cooldown |

Every requested look, face and gesture was accepted by `NPCLookAt` / `NPCFace` / `NPCGestures` (no declines).
After the run a `*/self/unclear` rule (50% brief look at the player) was added, so a remark about a listener
with an unclear tone is sometimes noticed. Not reflected in the numbers above.

Stale results (all discarded as designed):
- d1, dialogue closed one frame after the line → `discarded: dialogue closed (cancelled)`.
- Gemma, closed while waiting for the reply → `discarded: dialogue closed`.
- Gemma, second line as soon as input unlocked → turn 1's judgment arrived 1.45 s later →
  `discarded: superseded by T2`.

Performance (Editor Play mode, 5 turns per mode, one run; orc-demo and space-jarv Editors also open, the
latter in Play mode):

| | Off | Rules | d1 | Gemma |
| --- | --- | --- | --- | --- |
| reply first token, median (range) | 46 ms (41–51) | 50 (47–60) | 91 (53–95) | 127 (50–183) |
| reply total, median | 147 ms | 161 | 330 | 250 |
| judge time | — | <2 ms | 299–313 ms warm, 1488 ms first call | 1.30–1.78 s after the reply |
| frame time p50 / p95 / p99 | 5.6 / 6.2 / 9.7 ms | 5.4 / 6.2 / 8.6 | 4.9 / 5.6 / 14.4 | 5.3 / 6.4 / 10.1 |
| frames > 33 ms (≈37 s window) | 34 | 8 | 19 | 19 |

- d1 shares the GPU with the reply: first token +~45 ms, reply total ~2× (+~180 ms) while both run. Small in
  absolute terms; replies here are short.
- Gemma doesn't overlap the reply but evicts the NPC's cached prompt, so the *next* reply's first token is
  +~80 ms. A player who answers immediately waits behind a running judge: **first token 1395 ms** in the
  rapid-turn check. Batch runs can't be interrupted once started.
- No frame-time effect stands out from the noise (spikes up to 230–530 ms occur in every mode, Off included).
- Memory: the sidecar maps the 1.85 GB GGUF and has a 133 MB footprint (275 MB peak); Gemma adds no model
  memory (shared). Managed allocation per event is small (GC gen-0 counts 5–15 per 37 s window across all modes).

Limits: one live run, Editor not a build, other Editors competing for the GPU, 22 test cases authored by
the same person who wrote the policy, frame captures from that run unusable (camera inside scenery; fixed
afterwards), and **no human appropriateness rating yet**.

## Round 2 (same day): tone reading and policy

Short summary for the team: [decision-model-vs-gemma.md](decision-model-vs-gemma.md).

- Added 6 dev cases (reported claims, negation, teasing, praise guards) and 7 holdout cases before running
  anything on them (fixture sha256 `df2a33c3…`; test split unchanged).
- d1 tone attempts on dev (14 cases, both labels right): frozen wording 10/14; + yes/no "is it a dig?" 10/14
  (its probabilities sat near 0.5); + "does it speak well of them?" 7/14; "does it make them look bad?"
  10/14; second pass asking the tone toward the named referent 10/14 (+54 ms); same with a broader
  criticism description 9/14. None kept: the frozen wording stays.
- Holdout, scored once (`Logs/table-reactions-eval/holdout-d1-rules.json`): d1 referent 86%, meaning 57%,
  attention 100%, 0/2 false and 0/6 missed reactions, ~205 ms; rules 57% / 71%, 4/6 missed. Gemma not run
  on the holdout (needs the Editor closed for the batch run).
- Policy: `selfSkipChance` 0.05 (was the global 0.2) and a `*/self/unclear` rule (50% glance at the player).

## Round 3 (2026-11-16): d1 through Xybrid v0.19.0

Xybrid v0.18.0 added d1 inference (`lfm3-d1/text/v1`, PR #748); v0.19.0 is current. Its profile is fixed:
CPU only, 4 threads, d1-2B **Q4_K_M** (not our Q8_0), one whole-prompt pass per question. It takes the same
`{state, questions}` JSON our judge already builds. `XybridDecisionJudge` sends exactly the sidecar request and
runs Xybrid's answers through the same validation.

Measured with the released macOS arm64 CLI (`xybrid-v0.19.0-macos-arm64`, `xybrid run --decision-request`),
weights from HF `LiquidAI/d1-2B-GGUF` at `e968f657` (sha256 `adca5789…`, matches the SDK pin), M5 Max, frozen
wording, each set scored once (`Logs/table-reactions-eval/xybrid-cli-{dev,test,holdout}.json`):

| set | judge | referent | meaning | both | attention agrees |
|---|---|---|---|---|---|
| test (22) | d1 Q4 via Xybrid, CPU | 82% | **86%** | **68%** | **100%** |
| test (22) | d1 Q8 via llama.cpp, Metal (2026-11-04) | 82% | 82% | 64% | 90% |
| holdout (7) | d1 Q4 via Xybrid, CPU | 86% | **71%** | 57% | 88% |
| holdout (7) | d1 Q8 via llama.cpp, Metal (2026-11-03) | 86% | 57% | 57% | 100% |

No invalid answers, no reaction to any line that should get none (0/12 test, 0/2 holdout).

Speed is the problem. One CLI call per line took 8.4–9.7 s (p50 8.8 s) for 2,118–2,334 input tokens. Timing a
1-question request (539 tokens) against a 4-question one (2,065 tokens): **about 3.5 ms per prompt token** plus
about 0.76 s of process start and model load. In the game, with the model loaded once, our 4-question request
would take **about 8 s per line**, against 0.23–0.31 s on the Metal sidecar. That's at the 8 s expiry, so
reactions would come far too late. Road Decider's xybrid figure (325 ms for ~92 tokens, M4 Max) fits the same
3.5 ms/token rate. The cost is the prompt length times the question count, because nothing is shared between
the per-question passes.

So: Xybrid's d1 is good enough on labels, but too slow for this feature until its profile can use the GPU
(Metal) or reuse the shared prompt across questions. Shorter state (no persona facts) and fewer questions
would cut it to 1–2 s but were not tested for accuracy. The Tavern stays on the 0.16.0 checkout; nothing
was integrated in Unity.

## Human check (2026-11-16)

First informal rating, after a frames-only run (Off pass, then d1 pass; not blind). The right characters
reacted and the listeners were visible, but "they don't have expressions, they just look at me", and the d1
pass felt the "same" as the Off pass. At the authored intensities (faces 0.3–0.5, looks of 1.2–2 s) a d1
reaction is hard to tell apart from the watcher's random glances. Interpretation is no longer the
bottleneck; how visible the reaction is, is. Next: a blind test of whether a player can tell which line a
character reacted to.

## Recommendation: revise (keep the d1 path, drop the Gemma baseline)

- d1 interprets clearly better than rules and the dialogue model: referent and meaning at 82%, no reaction
  to any unrelated or injection line, deterministic, about a quarter of a second, and no contention with the
  dialogue model's lock.
- It is not good enough to feel alive yet: in the live run only one of five lines produced a visible d1
  reaction. Reported speech and negation ("surpassed you", "not a coward", "never stops talking") are its
  weak spots, and those are exactly the provocative lines this feature is for.
- Gemma-as-judge should not be used for this: wrong referent, ~1.5 s in the scene, and it delays the next reply.
- Production needs d1 in-process. Xybrid can run it since v0.18.0, but only on CPU, at about 8 s per line
  for this request (Round 3). The sidecar stays dev-only.

Next steps: (1) a short human rating pass with the frames-only run; (2) measure d1 through the Xybrid
decision API once it lands; (3) better tone reading needs a bigger decision model or game-specific
examples, since question rewording didn't help (round 2).
