# Contributing

## Working with Combat Extended and Simple Sidearms

This mod patches two mods it references but must not mirror.

### The rule, and why it exists

**Read upstream to learn what it does, then call it. Don't retype it.**

This is an engineering rule before it is anything else. A mirrored predicate, formula, or
constant is a fork: it drifts silently when upstream changes, it breaks on their next
release, and it can faithfully copy their bugs — this suite once mirrored a range gate
*including* its squared-vs-unsquared defect, which is the anti-pattern in one line. Calling
upstream keeps one implementation and one owner per behaviour, which is the whole point of a
compatibility patch.

When upstream has no entry point for what you need, in order of preference: compose the
public API that exists; use vanilla RimWorld's API; ask upstream for a seam (this suite
prefers upstream fixes generally); or decide not to enforce the behaviour and say so in the
README. Publicized private members count as calls, not copies.

### Licensing, pragmatically

Nobody in this chain profits from this work. Harmony between the patches outranks
bureaucratic caution, within two real limits:

- **Combat Extended is CC BY-NC-SA 4.0.** A free mod does not trip the NC clause, and
  share-alike is satisfiable by marking a derived portion under CE's license in NOTICE —
  the same pattern this repo already uses for CE's badge artwork. If deriving from CE is
  ever the right engineering call, do that rather than laundering it.
- **Simple Sidearms has no published license.** Its source is public on GitHub; the
  project reads that as author indifference rather than prohibition, and treats it the
  same pragmatic way as CE. A formal permission ask to the SS team is planned before this
  suite's full release, which closes the question properly.

### Reviews and AI assistance

State the no-mirroring rule as a constraint on **recommendations**, not just report text —
reviewers otherwise propose transcription, because it is usually the shortest fix. Treat
"this code matches upstream exactly" as a report of mirroring (a design finding, rated by
fragility), not as a confirmation of correctness.
