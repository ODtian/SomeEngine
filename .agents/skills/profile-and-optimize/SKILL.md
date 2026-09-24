---
name: profile-and-optimize
description: Profiler-driven performance investigation and optimization for SomeEngine. Use when Codex is asked to profile, diagnose, speed up, reduce frame time, reduce CPU/GPU/.NET overhead, or optimize runtime behavior, especially with Tracy, PIX, dotnet-trace, ETW/PerfView, or similar external profilers. The skill enforces recursive call-path analysis, progress documentation, structural fixes, and a strict ban on in-engine self profiling or ad hoc profile counters.
---

# Profile And Optimize

## Hard Rules

- Treat every request as end-to-end delivery: collect profiler evidence, identify a real bottleneck, implement a defensible structural optimization, validate correctness, measure the result, and update the progress document.
- Use external profilers as the source of truth. Preferred tools include, but are not limited to, Tracy, PIX, dotnet-trace, ETW/PerfView, GPU vendor tools, and benchmark harness output when it is tied back to profiler evidence.
- Do not add managed/self profiler behavior to SomeEngine. Do not add engine-side profile storage, timers, timing tables, call trees, count aggregation, local reports, or profile output files.
- If counts are needed, route them through an external profiler sink or an existing external-profiler bridge. The engine profiler layer must remain a bridge, not the profiler implementation.
- Do not flatten hotspots into a plain list. Preserve and analyze the recursive call path: parent operation -> hot child -> hot grandchild -> actionable leaf or ownership boundary.
- Do not paste in generic optimizations such as caches, early-outs, pooling, batching, pass toggles, or memoization. Only change code after profiler evidence, correctness reasoning, lifetime/invalidation analysis, and local pattern review support that exact change.
- Do not achieve performance goals by tuning scene parameters, benchmark load, pass switches, feature degradation, validation/static/placed/alias/fine-grained pass disabling, warmup/window cutoff changes, or other changes that fail across scenarios.
- Report any heuristic algorithm before implementing it. State why the heuristic is needed, what invariant it preserves, what cases it may miss, and how it will be validated.
- For dotnet build/run commands that may restore, do not use the sandbox. For Python, prefer uv plus a venv and do not use the sandbox.

## Tracy Tool Locations

Before using or documenting Tracy tooling, verify the actual path with `Test-Path`, `Get-ChildItem`, or `Get-Command`.

Known Tracy paths checked on 2026-06-16 with explicit `C:\Users\boqi` paths:

- `C:\Users\boqi\Apps` does not exist.
- `tracy-capture.exe`, `tracy-csvexport.exe`, and `tracy-profiler.exe` are in `C:\Users\boqi\App\windows-0.13.1`.

Use those tools first for Tracy capture/export/viewing. Do not record the path as `~/Apps`. Do not temporarily build or modify `external/tracy` tooling unless the user explicitly asks for that.

## Required Progress Document

Maintain a progress document for every profiling/optimization task. If the user does not specify a path, use `profile-and-optimize-progress.md` in the task workspace or the closest repo-local docs/work area already used for performance notes.

Create it from `assets/progress-template.md` when absent. Update it before the final response and after every meaningful profiler pass, code change, and before/after measurement.

Every progress table must include at least these columns:

| Profile conclusion | Optimization method | Optimization result |
| --- | --- | --- |
| Profiler-backed finding with the recursive call path and artifact references. | Detailed implementation approach, including exact ownership boundaries, invalidation/lifetime rules, correctness risks, and files changed or proposed. | Before/after numbers, profiler artifact references, correctness checks, and remaining risk. |

The optimization method cell must be detailed. A statement like "add cache" or "early out" is not acceptable; it must explain why the cache or early-out is correct, what invalidates it, what state owns it, why it is not hiding a deeper issue, and what tests or traces prove it.

## Workflow

1. Establish the target scenario and performance claim.
   Record the exact command, scene/workload, configuration, commit/branch state, profiler command, and raw artifact path in the progress document.

2. Capture profiler evidence with external tools.
   Prefer a profiler that can preserve call stacks or GPU event hierarchy. Use Tracy for instrumented CPU/GPU zones, PIX for GPU/frame captures, dotnet-trace for managed runtime stacks/events, and ETW/PerfView or vendor tools when they reveal the missing layer.

3. Recursively decompose the dominant cost.
   Start from the top-level frame, update, render, load, or job operation. Descend through inclusive time into child calls until reaching an actionable function, data structure, synchronization point, allocation pattern, IO boundary, shader/pass, or profiler-resolution limit. If the profiler cannot show the next level, collect a better trace instead of flattening the remaining cost.

4. Separate cause from symptom.
   Confirm whether the leaf cost is compute, allocation/GC, synchronization, IO, GPU pipeline state, shader work, memory traffic, lock contention, dispatch overhead, or repeated state derivation. Check nearby code and existing project patterns before designing a change.

5. Design one structural optimization tied to the call path.
   The change must reduce or remove the measured cause across scenarios. It must keep business rules, protocols, and state knowledge in one place without forcing unrelated code into an abstraction. Keep IO, parsing, validation, state updates, rendering, and presentation responsibilities separated.

6. Implement narrowly and preserve behavior.
   Do not add profiler counters or engine-local reports while implementing. Avoid broad refactors unless they are required by the measured bottleneck. For new class or method names, perform GitHub search research first and avoid `XXPlan`, `XXRun`, and `XXProgram` naming.

7. Validate correctness and performance.
   Run targeted tests and the same profiler-backed workload before and after the change. Compare comparable captures. If results are noisy, say so and gather enough evidence to justify the conclusion rather than inventing a threshold.

8. Finalize with evidence.
   Ensure the progress document contains the recursive profile conclusion, detailed optimization method, optimization result, commands, artifacts, and residual risk. The final response must link the progress document and summarize the measured result.

## External Profiler Examples

- Tracy: verify tools under `C:\Users\boqi\App\windows-0.13.1`, capture with `tracy-capture.exe`, export with `tracy-csvexport.exe`, and inspect with `tracy-profiler.exe` when visual call-path review is needed.
- PIX: use for GPU frame/event hierarchy, pass timing, barriers, pipeline state, resource hazards, and shader cost. Preserve capture paths in the progress document.
- dotnet-trace: use for managed CPU stacks, allocation/GC events, runtime contention, exceptions, and JIT/runtime overhead. Convert to an inspectable format when needed and store the artifact path.
- ETW/PerfView or Windows Performance Analyzer: use when OS scheduling, file IO, thread contention, GPU/CPU overlap, or runtime events are not visible in the first profiler.

## Output Expectations

- Keep the progress document current; do not leave optimization reasoning only in chat.
- Preserve profiler artifact paths and commands so the result can be audited.
- Explain every optimization through the measured recursive call path.
- Prefer one correct structural fix over several speculative tweaks.
- State explicitly when the evidence does not justify a code change yet, then gather the missing external-profiler evidence rather than guessing.
