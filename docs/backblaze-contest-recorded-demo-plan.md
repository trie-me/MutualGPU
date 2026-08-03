# Backblaze contest recorded-demo plan

Status: draft for iteration; planning only

Companion script: `docs/backblaze-contest-recorded-demo-script.md`

## Demo objective

Tell a two-minute human story:

1. MutualGPU turns spare graphics power into shared compute.
2. That shared capacity can widen access to expensive GPU workloads.
3. A requestor submits a job and a participating provider completes it.
4. Backblaze B2 makes storing and delivering the files economical.

The viewer should understand the product before hearing about its storage.

## MutualGPU and its social impact

MutualGPU is a distributed WebGPU work exchange. It connects people who need
demanding creative or AI work with people willing to contribute spare graphics
power from their own computers.

Its goal is to turn hardware that might sit idle into useful community
capacity. That can make powerful computing more accessible to students,
independent creators, researchers, and small teams that cannot justify buying a
high-end GPU of their own.

Keep this grounded as a goal and product direction. Do not promise universal
availability, guaranteed performance, or income for participants.

## Why Backblaze fits

MutualGPU stores each file once, but people and providers may download it many
times. At MutualGPU's projected scale, Backblaze's egress model takes that
download cost off the bill. B2 also speaks the S3-compatible language, so the
project can keep the storage work already built instead of starting over.

Plain-language version:

> We store each file once, but it may be downloaded again and again. At our
> projected scale, Backblaze takes that download cost off the bill. And because
> B2 works like S3, we can keep the storage plumbing we already built.

## Final-video boundary

- Hard maximum: `2:00`, including titles, transitions, end frames, and silence.
- Target duration: `1:56`.
- Format: 16:9, 1080p.
- Keep a small `NON-PRODUCTION DEMO` label visible throughout.
- Use synthetic, non-sensitive input and result media.
- Record only after the B2 demo path and its end-to-end tests are complete.
- Show the application and friendly status cards, not developer or
  administrator tooling.
- Never show credentials, tokens, handles, account identifiers, bucket names,
  object keys, raw logs, or signed links.

## Demo journey

- A requestor chooses an available capability, adds a small image and prompt,
  and submits the job.
- MutualGPU checks the input and stores it in Backblaze B2.
- A participating provider accepts the task and runs it on their GPU.
- The completed result returns through MutualGPU, is checked, and is stored in
  B2.
- The requestor opens the result, which Backblaze delivers directly after
  MutualGPU approves access.

This is the only product journey shown. Everything else belongs in technical
evidence, not the contest video.

## Required demo material

1. One small, visually recognizable input image.
2. One short prompt that produces a clear, deterministic-looking result.
3. One ready participating provider or a clearly labeled verified capture of
   the same flow.
4. Simple illustrations for the requestor, provider, shared GPU capacity, and
   “write once, read many times” concept.
5. Friendly product statuses:

```text
Upload checked
Saved to Backblaze B2
Provider connected
Work in progress
Result checked and ready
Direct delivery from Backblaze B2
```

## Storyboard

| Time | Picture | Story |
| --- | --- | --- |
| 0:00–0:14 | MutualGPU title; requestor and provider illustrations | Explain that MutualGPU connects people who need GPU work with people willing to share spare graphics power. |
| 0:14–0:30 | Ordinary computers form a shared pool; student, creator, researcher, and small-team illustrations | Explain the goal: turn idle hardware into community capacity and make powerful compute accessible to more people. |
| 0:30–0:48 | One stored file branches into many downloads; Backblaze B2 and S3-compatible labels appear | Explain the read-heavy workload, the projected egress-cost benefit, and reuse of existing storage work. |
| 0:48–1:08 | Live requestor submission with friendly status cards | Choose a capability, add the synthetic input and prompt, submit, and show the input safely stored in B2. |
| 1:08–1:28 | Provider accepts; progress advances; result becomes ready | Time-compress the workload, show the participating GPU do the work, and show the checked result stored in B2. |
| 1:28–1:44 | Requestor opens the completed result | Show MutualGPU approve access and Backblaze deliver the finished file directly. |
| 1:44–1:56 | Finished result beside MutualGPU and Backblaze names | Close on shared opportunity, lower cost, and room for more people to participate. |

## Clipchamp workflow

1. Record the seven scenes separately in a clean Chrome or Edge profile.
2. Put the application captures on the main video track.
3. Add social-impact illustrations, titles, and status cards above them.
4. Record narration separately and drag each section to its scripted timestamp.
5. Split or trim narration and video scene by scene.
6. Use hard cuts or very short dissolves; label time-compressed processing.
7. Add captions and optional quiet, properly licensed music.
8. Fade every audio track before `1:56` and export at 1080p.

Suggested track layout:

```text
V3  NON-PRODUCTION DEMO label and friendly status cards
V2  titles, illustrations, product arrows, and captions
V1  application capture and synthetic input/result media
A2  optional low-volume licensed music
A1  narration aligned to the timestamped script
```

## Claim-to-evidence checklist

| Claim | What the viewer sees |
| --- | --- |
| MutualGPU shares GPU capacity | A requestor submits work and a separate provider completes it. |
| The product has a social purpose | Idle computers become a shared pool serving students, creators, researchers, and small teams. |
| Backblaze fits the economics | A “write once, read many times” visual accompanies the projected egress-cost explanation. |
| Existing storage work is preserved | A simple S3-compatible label appears without implementation detail. |
| MutualGPU protects the exchange | Input and result statuses show that MutualGPU checks files before storing them. |
| Backblaze delivers the result | The final file moves directly from B2 to the requestor after access approval. |

## Review checklist

- The first 30 seconds explain MutualGPU and its intended social impact.
- A non-technical viewer can follow requestor → provider → result.
- Backblaze is presented as an enabler of the mission, not as the product itself.
- The cost explanation uses “write once, read many times” and projected scale.
- No unnecessary system terminology appears in narration, captions, or visuals.
- The video stays entirely on the current B2 product journey.
- No secret or sensitive identifier appears in raw footage, audio, captions,
  export metadata, or final media.
- The complete exported file is no longer than `2:00`; target `1:56`.
- One technical reviewer and one non-technical viewer approve the final cut.

## Deliverables

- editable Clipchamp project;
- final 1080p video no longer than two minutes;
- caption file and plain-text transcript;
- social-impact and read-heavy-workload illustrations;
- thumbnail or end-card artwork if the contest requires it; and
- private review record with the demo revision, fixture provenance, reviewers,
  and approval date.

## Unresolved decisions for iteration

1. Which synthetic image, prompt, and result tell the clearest story?
2. Should the opening illustrations use people, devices, or a simple animated
   network?
3. Will narration be voice-over only or briefly presenter-led?
4. Which project URL, Backblaze marks, credits, and call to action may appear?
5. Who supplies technical review, social-impact review, and publication
   approval?
