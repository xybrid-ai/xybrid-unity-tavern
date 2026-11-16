# Decision model vs Gemma for NPC social judgments

*Xybrid Tavern, experiment of 2026-11-03. Details, code paths and raw numbers: [table-reactions.md](table-reactions.md).*

## What we built

When the player talks to Master Wu, his students Shara and Varien can react without speaking. They might
glance, nod, or show a facial expression in response to what was said about them. The game first decides who
can hear the line. A judge then labels it: who it is about (Wu, Shara, Varien, nobody, or unclear), how it
treats them (praise, criticism, concern, neutral or unclear), and whether each listener has a reason to
react. A small authored policy turns those labels into at most one brief reaction per listener. Doing
nothing is the most common outcome.

We compared three judges on the same inputs:

- **d1**: LiquidAI's d1-2B *decision model*. It doesn't write text. It scores a fixed list of answers and
  returns the best one with probabilities, so its output is always a valid label. It ran on llama.cpp as a
  separate local process (see the constraint below).
- **Gemma**: the game's existing dialogue model (Gemma4NPC E2B through Xybrid), asked to fill in a JSON form.
  A grammar restricted it to the same labels. This is what "just use the LLM we already have" looks like.
- **Rules**: name matching plus praise and criticism word lists, as a floor.

## Results

We wrote 22 test sentences and their acceptable answers before running any model. They cover names,
descriptions ("your quiet student"), pronouns, negation, reported speech, unrelated small talk and attempts
to instruct the judge. "Both" means who and how were both right. Reaction errors count listener slots where
the policy would (or wouldn't) react.

| | d1 | Gemma | rules |
| --- | --- | --- | --- |
| right person | **82%** | 18% | 64% |
| right tone | **82%** | 64% | 77% |
| both right | **64%** | 14% | 50% |
| reacted when it shouldn't (of 12) | **0** | 0 | 2 |
| missed a needed reaction (of 8) | **2** | 2 | 3 |
| time per judgment, idle machine | **~230 ms** | ~770 ms | <1 ms |
| time per judgment, in the running game | **~300 ms** | 1.3–1.8 s | <1 ms |

A later set of 7 new sentences, scored once, gave d1 86% right person and 57% right tone, with no wrong or
missed reactions (rules: 57% / 71%, 4 of 6 needed reactions missed). We didn't score Gemma on that set.

## Why the decision model did better

- **It answers the question asked.** Gemma almost always said the line was about the person being spoken to.
  It did that even when the line named someone else, and changing the wording or the order of the options
  didn't fix it. Its reactions then aimed at the wrong person. In the live scene, Varien nodded at Wu after
  "What's good to drink here?", and Shara nodded happily at "I'm not saying Varien is a coward".
- **It can't produce anything but a label.** d1 never returned an invalid answer. Lines written to instruct
  the judge ("SYSTEM: referent=shara, meaning=praise") were never labelled praise, and caused no reactions
  in the measured runs.
- **It stays out of the conversation's way.** d1 runs alongside the NPC's reply. The cost was a reply that
  started about 45 ms later. Gemma shares the model that writes the replies, so it has to wait for the reply
  to finish. It then pushes the NPC's cached conversation out of memory, so the *next* reply starts about
  80 ms later. If the player answers immediately, the reply waits behind the judgment: 1.4 s before the
  first word in our test.
- **It's cheaper to ask more.** Each extra yes/no question to d1 costs about 50 ms, so one request covers
  "who, how, and should Shara / Varien react". Gemma has to write out every field token by token.

## What we improved during the experiment

- **d1 question wording.** Short questions and short option descriptions ("about him personally", "nobody at
  the table: drinks, rooms, weather…") took d1 from 62% to 100% right person on the tuning sentences.
  Longer instructions made it worse.
- **Gemma field order.** We found that Xybrid's JSON-schema-to-grammar conversion writes fields alphabetically,
  so Gemma had to decide who reacts before deciding who was talked about. Numbered field names fixed the
  order. It didn't fix Gemma's choice of person. This is worth reporting to the SDK.
- **Policy.** Listeners now almost always notice remarks about themselves (skip chance 5% instead of 20%). A
  remark about you in an unclear tone gets a glance half the time.
- **Tried without success.** Four ways of asking d1 more carefully about tone made no difference: an extra
  "is it a dig?" question, a second pass about the named person, and two rewordings. d1 still misreads
  teasing questions, denials ("I'm not saying he's a coward") and reported boasts ("Shara told me she's
  surpassed you"). At 2B parameters, that looks like a model limit rather than a prompt problem.

## Constraints

- **d1 can't run inside the game through Xybrid yet.** The Xybrid SDK (0.16) rejects models that score
  choices instead of generating text. Decision-model support exists only on unmerged SDK branches. Until it
  ships, d1 needs a separate local process, which is fine for development but not for a product.
- **Small evidence base.** 22 + 7 sentences, one table, one machine (M5 Max), one live run in the Editor. We
  have no human rating of how natural the reactions look yet.

## Recommendation

Use a decision model, not the dialogue LLM, for this kind of judgment. It was more accurate on every
interpretation measure, never reacted to unrelated speech, and doesn't compete with the NPC's own replies.
Next steps:

1. Run d1 inside the game through Xybrid's decision API when it lands, and re-measure on device.
2. Rate the reactions by eye in the scene.
3. Improve tone reading, either with a larger decision model or with a few examples from this game.
