# Backblaze contest recorded-demo script

Status: draft for rehearsal and iteration

Target duration: `1:56`

Hard maximum: `2:00`, including all titles, transitions, end frames, and silence

Companion plan: `docs/backblaze-contest-recorded-demo-plan.md`

## Recording setup

- Format: 16:9, 1080p.
- Keep `NON-PRODUCTION DEMO` visible in a corner for the entire video.
- Record the seven scenes as separate clips and assemble them in Clipchamp.
- Put narration on its own audio track. Drag each section to its timestamp, then
  split or trim it to fit before adding transitions.
- If music is used, keep it quiet beneath narration and use only a free or
  properly licensed track. Fade it out before `1:56`.
- Do not show terminals, cloud consoles, sensitive configuration, private
  identifiers, browser address bars, or network-inspection tools.
- Show only the product journey and simple states such as `Upload checked`,
  `Provider working`, and `Result ready`.

## Timestamped script

| Time | Visual and edit | Narration | On-screen text |
| --- | --- | --- | --- |
| **0:00–0:14** | Open on the MutualGPU title, then show a person requesting work and another person offering spare GPU capacity. Keep the small demo label visible. | **“MutualGPU is a shared GPU network. It connects people who need demanding creative or AI work with people willing to contribute spare graphics power from their own computers.”** | `MutualGPU`<br>`Share compute. Expand access.`<br>`NON-PRODUCTION DEMO` |
| **0:14–0:30** | Turn several ordinary computers into one growing pool of available GPU capacity. Show students, independent creators, researchers, and a small team as simple illustrations. | **“That turns hardware that might sit idle into useful community capacity. Our goal is simple: make powerful computing more accessible to students, independent creators, researchers, and small teams that cannot justify buying a high-end GPU.”** | `Idle hardware → community capacity`<br>`More people can create, learn, and research` |
| **0:30–0:48** | Show one stored file branching into many downloads, then reveal the Backblaze B2 and S3-compatible labels. Use one smooth zoom. | **“Backblaze makes this model affordable. MutualGPU stores a file once, but that file may be downloaded many times. At our projected scale, Backblaze’s egress model takes that download cost off the bill, and its S3 compatibility lets us keep the storage work we already built.”** | `Write once → read many times`<br>`Lower cost at projected scale`<br>`Built on S3-compatible storage` |
| **0:48–1:08** | In the live application, choose an available capability, add the synthetic image and prompt, and submit. Replace technical diagnostics with a short, friendly status card. | **“Here, a requestor chooses an available capability, adds an image and prompt, and submits the job. MutualGPU checks the upload and safely stores it in Backblaze B2, ready for a participating GPU.”** | `Upload checked`<br>`Saved to Backblaze B2`<br>`Finding available compute…` |
| **1:08–1:28** | Show the provider accept the task and the progress indicator advance. Time-compress the workload and label the speed-up. End on `Result ready`. | **“A provider accepts the task, downloads the input, and runs the workload on its GPU. When the work is complete, the result comes back through MutualGPU, where it is checked and stored in B2.”** | `Provider connected`<br>`4× — processing shortened`<br>`Result checked and ready` |
| **1:28–1:44** | Return to the requestor view and open the finished result. Use a simple arrow from Backblaze to the requestor; avoid showing the browser address bar or network tools. | **“The requestor opens the finished result. After MutualGPU approves access, Backblaze delivers the file directly, keeping downloads fast and keeping heavy file traffic away from the application.”** | `Access approved`<br>`Direct delivery from Backblaze B2` |
| **1:44–1:56** | Finish on the generated result beside the MutualGPU and Backblaze names. Fade narration and any music by `1:56`; hold no extra end frame. | **“MutualGPU turns spare hardware into shared opportunity. Backblaze makes that exchange practical: lower costs, direct delivery, and room for more people to take part.”** | `Spare hardware → shared opportunity`<br>`MutualGPU × Backblaze B2` |

## On-screen status labels

Keep product feedback short and human-readable:

```text
Upload checked
Saved to Backblaze B2
Provider connected
Work in progress
Result checked and ready
Direct delivery from Backblaze B2
```

Do not display internal storage names, private links, or diagnostic details.

## Clipchamp timeline layout

```text
V3  persistent NON-PRODUCTION DEMO label and simple status cards
V2  titles, social-impact illustrations, product arrows, and captions
V1  application capture and synthetic input/result media
A2  optional low-volume licensed music, ending before 1:56
A1  final narration, split and aligned to the timestamps above
```

Keep each scene independent so a retake replaces one timeline block rather than
the whole recording. Use hard cuts or very short dissolves; long transitions
consume the four-second safety margin.

## Rehearsal checklist

- Read the narration naturally in `1:48`–`1:52`, leaving room for visual pauses.
- Confirm the complete export is at most `2:00`; aim for exactly `1:56` or less.
- Verify `NON-PRODUCTION DEMO` remains visible in every frame.
- Verify the opening explains MutualGPU before introducing storage.
- Verify the social-impact claim is framed as MutualGPU's goal.
- Verify the requestor, provider, and completed result are easy to follow.
- Verify the upload is checked by MutualGPU and stored in B2.
- Verify the result is delivered directly from B2 after access approval.
- Verify every visible label and spoken line supports the product journey,
  social impact, or Backblaze story.
- Review every raw clip, caption, and audio track for sensitive information
  before export.
