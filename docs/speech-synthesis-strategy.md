# Speech Synthesis Strategy – LLM → TTS (Azure-first, vendor-agnostic)

This document defines the **system-wide strategy** for turning LLM output into spoken audio without pronunciation artifacts (e.g., `fineeee` being read as “fine eeee”) and without relying on the LLM to produce production-safe SSML.

It is written to be used as a **global guideline** across multiple prompts/agents and multiple code paths (time-reporting assistant and future voice features).

---

## 1. Problem statement

### 1.1 Expressive orthography breaks TTS
LLMs often produce “expressive spelling” to convey emotion:

- `fineeee`, `nooooo`, `soooo`, `pleaaase`
- `!!!`, `???`, `......`

Many TTS engines – including Azure Speech – may pronounce these **literally** instead of stretching the vowel naturally (e.g., “fine eeee”).

This can happen even when SSML is used, because SSML does not interpret repeated letters as prosody.

### 1.2 LLM-generated SSML is untrusted input
Even if the LLM is instructed to output SSML, it can still occasionally output:

- malformed XML (unescaped `&`, `<`, `>`, quotes)
- unsupported tags/attributes for the specific engine/voice
- incorrect namespaces
- overly creative markup

These cause intermittent failures or weird speech.

---

## 2. Architectural principle

**Separate speech semantics from speech rendering.**

- **LLM** produces *what to say* plus optional *speech intent* (emphasis, rate, pauses, style).
- **Code** produces vendor-specific SSML deterministically.
- **TTS engine** only receives validated, well-formed input.

Treat LLM output as **untrusted input** across the entire system.

---

## 3. Required pipeline (applies everywhere)

All TTS flows must follow this pipeline:

1) **Generate content** (LLM)  
2) **Normalize** the text (deterministic)  
3) **Render** vendor-specific SSML (deterministic)  
4) **Call TTS**  
5) **Fallback** if SSML fails: call TTS with normalized plain text

### 3.1 Non-negotiable rule
**Normalization MUST run before any TTS call** – including the raw-text fallback.

---

## 4. SpeechIntent (vendor-neutral contract)

Prefer a vendor-neutral contract for speech semantics. Keep it intentionally small.

Example:

```json
{
  "text": "I'm fine.",
  "style": "playful",
  "rate": "slower",
  "pitch": "slightly_higher",
  "emphasis": ["fine"],
  "pauses_ms": [120]
}
```

### Notes
- `text` is always plain text.
- `style`, `rate`, `pitch`, `emphasis`, `pauses_ms` are optional.
- You can extend later (e.g., `spelling`, `sayAs`, `phonemes`) without changing the architecture.

---

## 5. Normalization rules (English)

Implement a deterministic function:

- `NormalizeForTts(string text) -> string`

### 5.1 Collapse elongated letters
Replace 3+ repeated letters with a single letter:

- `fineeee` → `fine`
- `nooooo` → `no`

Regex (conceptual):

- `([A-Za-z])\1{2,}` → `$1`

### 5.2 Collapse repeated punctuation
Recommended canonicalization:

- `!{2,}` → `!`
- `\?{2,}` → `?`
- `\.{4,}` → `...` (keep three dots as a natural pause)
- `,{2,}` → `,`

### 5.3 Normalize whitespace
- Convert multiple spaces/tabs/newlines to single spaces.
- Trim leading/trailing whitespace.

### 5.4 Optional hardening
- Remove control characters.
- Keep ASCII + common punctuation. (No emojis if your output is voice-only.)

---

## 6. Elongation-to-intent mapping (optional but recommended)

Instead of losing expressiveness, detect elongated spelling and convert it into intent.

Example:
- Input: `fineeee`
- Output:
  - `text = "fine"`
  - `emphasis = ["fine"]`
  - `rate = "slower"` (or apply a local prosody change around the word)

This preserves the “drawn out” effect without breaking pronunciation.

---

## 7. SSML rendering rules (Azure Speech)

### 7.1 Deterministic renderer
Implement:

- `RenderAzureSsml(SpeechIntent intent, VoiceConfig cfg) -> string`

Renderer requirements:
- Exactly **one** `<speak>` root
- Correct namespaces
- Always wrap content in `<voice name="...">`
- Only use tags known to be supported in your target configuration:
  - `speak`, `voice`, `p`, `s`, `break`, `prosody`, `emphasis`, `say-as`, `phoneme`
  - Azure-specific (optional): `mstts:express-as`, `mstts:silence`

### 7.2 Escape XML characters
All text nodes MUST escape:
- `&` → `&amp;`
- `<` → `&lt;`
- `>` → `&gt;`
- `"` → `&quot;`
- `'` → `&apos;`

### 7.3 Expressiveness must be structural
Do **not** use repeated letters for emotion. Use:
- `<emphasis>`
- `<prosody>`
- `<break>`
- (optional) `mstts:express-as`

---

## 8. Validation and fallback behavior

### 8.1 Validate SSML before calling Azure
- Parse as XML (fast check)
- Optionally enforce an allowlist of tags/attrs
- If invalid → fallback path

### 8.2 Fallback
If SSML rendering or Azure call fails:
- speak **normalized plain text**
- do not send unnormalized raw text to TTS

---

## 9. How this changes prompts and agents

### 9.1 Prompt rules (system-wide)
Across all prompts that can feed TTS:

- Never use elongated spelling (no `fineeee`, `noooo`, `soooo`)
- Never use emojis or special Unicode characters (voice-only)
- Avoid markdown (no bullet points, no formatting tokens)

### 9.2 Strong recommendation: stop asking the LLM to produce final SSML
Instead, ask it to produce **SpeechIntent** (JSON) and let the renderer produce SSML.

If you must keep the current `{ text, ssml }` format temporarily:
- Treat `ssml` as “best-effort” and still run:
  - XML validation
  - normalization over `text`
  - deterministic rendering if `ssml` fails

---

## 10. Specific notes about your current system prompt (example)

You currently ask the model to return:

```json
{ "text": "...", "ssml": "<speak>...</speak>" }
```

And provide an Azure SSML shell with `mstts:express-as`, `mstts:silence`, and `<prosody>`.

### What to keep
- The outer Azure template is useful and consistent.

### What to change (recommended)
1) Add a strict rule:
   - **Never** use repeated letters for emphasis (fineeee/noooo). Use SSML intent instead.
2) Still **normalize** `text` and use it for fallback.
3) Consider switching `ssml` → `intent` (vendor-neutral), e.g.:

```json
{
  "text": "I'm fine.",
  "intent": { "emphasis": ["fine"], "rate": "slower", "style": "playful" }
}
```

Then render SSML in code using your template.

---

## 11. Enforcement checklist for code reviews and Claude Code

Whenever adding/modifying anything TTS-related:

- [ ] LLM output is never sent directly to TTS
- [ ] NormalizeForTts is applied on every TTS path (SSML and fallback)
- [ ] SSML is rendered deterministically (renderer function)
- [ ] SSML is validated as XML before calling Azure
- [ ] Unit tests exist for normalization and rendering
- [ ] Prompt rules forbid elongated spelling and emojis

---

## 12. Test cases (minimum)

Normalization:
- `fineeee` → `fine`
- `nooooo!!!` → `no!`
- `Wait...... what???` → `Wait... what?`
- `  hello   world  ` → `hello world`

Rendering:
- Output is well-formed XML
- Contains one `<speak>` root and one `<voice>` wrapper
- Escapes `&` correctly in text

---

## 13. Alternative: LLM-native TTS engines

### 13.1 The fundamental problem with traditional TTS

Traditional TTS engines (Azure Speech, Google Cloud TTS, Amazon Polly) are **text readers** — they convert characters to phonemes deterministically. They don't understand that `Ugh` is a vocal interjection, `fineeee` is emphasis, or `*giggles*` is a stage direction. This means:

- Interjections that are natural in character speech (`Ugh`, `Hmm`, `Pfft`) are pronounced literally and sound robotic
- Expressive spelling is read letter-by-letter
- The normalization pipeline (sections 5-6) exists to work around this limitation

LLM-native TTS engines solve this at the source — they understand language intent, not just text.

### 13.2 gpt-4o-mini-tts (recommended evaluation)

OpenAI's `gpt-4o-mini-tts` is an LLM that generates speech directly. Instead of SSML, you control voice style via a **system prompt** (e.g., "speak with a snarky, reluctant tone, sigh before answering").

**Key advantages for this project:**
- Natively understands `Ugh` as a sigh, `fineeee` as drawn-out emphasis, `*giggles*` as laughter
- No SSML at all — eliminates the entire XML generation/validation/fallback pipeline
- Style is controlled via natural language prompt, which maps directly to character personalities
- Streaming supported — same chunked playback pattern as Azure
- Latest model: `gpt-4o-mini-tts-2025-12-15` with ~35% lower word error rate

**Pricing:** $0.60/1M input tokens + $12/1M audio output tokens (~$0.015/min). Comparable to Azure Neural.

**Tradeoff:** No fine-grained SSML control (prosody rate, pitch, pauses). Style is prompt-driven, not structural. Latency may be slightly higher than dedicated TTS engines.

**If adopted, this eliminates the need for:** sections 4-8 of this document (SpeechIntent, normalization, SSML rendering, validation/fallback). The pipeline simplifies to: LLM → plain text → gpt-4o-mini-tts with character style prompt.

### 13.3 Other notable alternatives

OpenAI also offers a **Realtime API** (`gpt-realtime-mini`) that does speech-to-speech over WebSocket/WebRTC — could replace the entire STT → LLM → TTS pipeline with a single connection. Much larger architectural change.

### 13.4 TTS provider cost comparison (as of February 2026)

| Provider | Tier / Model | Per 1K chars | Notes |
|---|---|---|---|
| **Google Cloud** | Standard | **$0.004** | 4M chars/mo free. Basic quality. |
| **Amazon Polly** | Standard | **$0.005** | 5M chars/mo free. Basic quality. |
| **Cartesia** | Sonic 3 | **$0.011** | Low latency. Expressive — supports AI laughter/emotion natively. |
| **OpenAI** | tts-1 | **$0.015** | 6 preset voices. No free tier. |
| **Azure Speech** | Neural | **$0.016** | 5M chars/mo free. Current engine. |
| **Google Cloud** | WaveNet/Neural | **$0.016** | 1M chars/mo free. |
| **Amazon Polly** | Neural | **$0.019** | 1M chars/mo free (12 months). |
| **OpenAI** | gpt-4o-mini-tts | **~$0.015/min** | LLM-native. Prompt-steerable style. Streaming. |
| **Azure Speech** | Neural HD V2 | **$0.030** | Premium voices. |
| **OpenAI** | tts-1-hd | **$0.030** | Higher quality audio. |
| **Amazon Polly** | Generative | **$0.030** | 100K chars/mo free (12 months). |
| **LMNT** | Standard | **$0.030** | Voice cloning from 5s sample. |
| **Play.ht** | Premium | **$99/mo flat** | Unlimited generation. May not have API. |
| **ElevenLabs** | Creator (in plan) | **$0.220** | 100K chars for $22/mo. Most expressive traditional TTS. |
| **ElevenLabs** | Pro (overage) | **$0.240** | Best voice quality, highest cost. |

### 13.5 Recommendation

For a voice assistant with strong character personalities (interjections, expressive speech, emotional tone):

1. **Best fit:** `gpt-4o-mini-tts` — comparable cost to Azure, eliminates SSML complexity, natively handles expressive text. Evaluate first.
2. **Budget alternative:** Cartesia Sonic 3 — cheaper than Azure, purpose-built for voice agents, handles emotion/laughter.
3. **If staying on Azure:** Implement `NormalizeForTts` (section 5) and add prompt rules forbidding problematic interjections. Workable but fights the engine's limitations.

---

## 14. Outcome

With this strategy:
- Speech output becomes reliable and predictable
- You prevent "fine eeee" style artifacts
- You decouple LLM prompting from vendor SSML quirks
- You can swap Azure Speech later with minimal changes (renderer swap only)
- Or: adopt an LLM-native TTS engine and eliminate the SSML pipeline entirely
