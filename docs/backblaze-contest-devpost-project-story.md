# MutualGPU × Backblaze B2

## Inspiration

Powerful GPUs are expensive, but access to them is uneven. Students, independent
creators, researchers, and small teams can have ideas worth exploring without
having the hardware budget to explore them. At the same time, capable graphics
cards in homes and workplaces spend much of their lives idle.

MutualGPU started with a simple question: what if people could share spare GPU
capacity as easily as they share files or bandwidth?

Our goal is to turn underused hardware into community capacity. By connecting
people who need compute with people willing to contribute it, MutualGPU can help
more people create, learn, experiment, and conduct research without requiring
everyone to own a high-end GPU.

## What it does

MutualGPU is a shared GPU work exchange. A requestor chooses a supported task,
adds the required input and prompt, and submits the job. MutualGPU checks the
request, finds a connected provider with the right capabilities, and offers that
provider the work.

Once the provider accepts, its GPU runs the workload and returns the result
through MutualGPU. The result is checked, stored in Backblaze B2, and made
available to the requestor. After access is approved, the finished file is
delivered directly from B2 through a short-lived download link.

For our demo, a participating computer contributes its WebGPU-capable graphics
card to complete an image-generation task. The requestor sees a simple journey:
upload checked, provider working, result ready.

## Why Backblaze B2

MutualGPU has a naturally read-heavy storage pattern: an input or result is
written once, but it may be downloaded many times. At projected scale,
Backblaze is the superior choice because of its egress cost model. It eliminates
the egress costs that would otherwise grow with every result download,
protecting the economics of a community compute network.

B2 also gives us an S3-compatible API, so we could reuse the object-storage
infrastructure we had already built instead of redesigning the application
around a new storage interface. That combination—predictable delivery costs and
easy integration—makes Backblaze an ideal fit for MutualGPU.

## How we built it

MutualGPU separates coordination from heavy file delivery. The application
authenticates participants, validates uploads, matches tasks to suitable
providers, tracks progress, and protects access to results. PostgreSQL keeps the
transactional state, while Backblaze B2 stores the input and result files.
Uploads pass through MutualGPU so they can be authenticated, checked, and
hashed. Authorized downloads then travel directly from B2 using short-lived
links, keeping large file transfers away from the application itself.

The provider side supports both browser-based WebGPU workers and Node.js
workers. A shared protocol keeps task acceptance, progress, recovery, and result
publication consistent across both environments.

MutualGPU is also built on **NetCats, a custom effects system**. Instead of
scattering asynchronous behavior across unrelated callbacks and background
jobs, we express important workflows—such as enrollment, submission,
scheduling, completion, and disconnect recovery—as small, composable effects.
NetCats runs long-lived work inside a structured tree of lightweight fibers,
giving ownership, cancellation, recovery, graceful shutdown, and diagnostics
one consistent model.

That foundation is especially useful for community compute, where a browser can
close, a provider can disconnect, or a requestor can cancel while work is in
flight. The effects system helps us make each of those paths explicit and lets
us inspect the live ownership tree without exposing task secrets.

## Challenges we ran into

The hardest part was not sending work to a GPU; it was making the exchange safe
and understandable when computers are owned by different people and can
disappear at any time.

We designed assignment-scoped authority so a provider can act only on the task
attempt it has accepted. We added bounded recovery for interrupted sessions,
single-use result publication, validated file handling, and one active task per
provider. We also kept control messages separate from large input and result
files so coordination stays responsive while Backblaze handles delivery.

Another challenge was making a distributed system feel simple in a short demo.
We replaced infrastructure detail with a few human-readable states so the
requestor can follow the experience without needing to understand the machinery
behind it.

## Accomplishments that we're proud of

- We built a working requestor-to-provider-to-result journey around real shared
  GPU capacity.
- A user can offer compute from a browser tab without installing a full native
  worker.
- The same provider lifecycle works across browser and Node.js environments.
- Backblaze B2 supports direct, authorized file delivery without making the
  application carry every download.
- Our custom effects system gives us a clear operational picture of long-lived,
  failure-prone work.
- The product connects a practical technical architecture to a broader social
  goal: widening access to useful GPU compute.

## What we learned

We learned that shared compute is fundamentally a coordination and trust
problem. Capability matching, limited authority, cancellation, recovery, and
clear user feedback matter just as much as raw GPU performance.

We also learned that storage economics are part of the product. A network meant
to widen access cannot succeed if every popular result makes delivery more
expensive. Backblaze gives MutualGPU a cost model that fits how the platform is
actually used.

Finally, building on effects made failure handling easier to reason about. When
ownership and cancellation are visible parts of the program, disconnects stop
being mysterious edge cases and become normal states the system can manage.

## What's next for MutualGPU

Next, we want to support more useful GPU workloads, make it easier for more
people to contribute compatible hardware, and continue strengthening provider
sandboxing, observability, and file-retention controls. We also want to test the
model with communities that feel the access gap most directly, including
students, independent creators, researchers, and small teams.

The long-term vision is a dependable shared-compute layer that turns spare local
hardware into opportunity—and uses Backblaze B2 to keep the results affordable
to deliver.

## Built with

- Backblaze B2 and its S3-compatible API
- NetCats custom effects system
- WebGPU
- C# and .NET 10
- ASP.NET Core
- PostgreSQL
- TypeScript and Node.js
- Protocol Buffers, gRPC, and WebSockets
