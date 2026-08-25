# Contributing

## Working with Combat Extended and Simple Sidearms

This mod patches two mods it cannot ship, cannot vendor, and cannot copy from.

- **Simple Sidearms has no published license.** Nothing from it may be redistributed or
  reproduced in any form.
- **Combat Extended is CC BY-NC-SA 4.0.** Its non-commercial and share-alike terms are
  incompatible with this project's MIT license, so its code cannot be incorporated either.

Both are decompiled locally during development to understand their behaviour. Those
decompiles are **behavioural reference only**.

### The rule

**Read upstream to learn what it does, then call it. Never retype it.**

This covers more than whole methods. A transcribed predicate, formula, or magic constant
is still a copy — including short ones. If you found it by reading a decompile and typed
it into this repository, that is copying, regardless of length.

### When upstream has no entry point for what you need

This happens, and it is not licence to reimplement. In order of preference:

1. **Compose the public API that does exist.** Several narrower calls beat one
   reimplementation.
2. **Use vanilla RimWorld's own API** where the rule is really a vanilla concept that
   upstream merely re-expressed.
3. **Ask upstream to expose it.** A missing accessor is a reasonable feature request, and
   this suite's standing preference is to fix things upstream where possible.
4. **Decide not to enforce it, and document that.** An explicit, stated gap in the README
   is honest. A local reimplementation that silently drifts from upstream is not.

### Reviews and AI assistance

If you brief a reviewer, human or otherwise, **state this constraint as a rule about
their recommendations, not just their report.** Telling a reviewer "don't quote upstream
source in your findings" leaves them free to recommend reimplementing it — and they will,
because it is usually the shortest fix. An unstated constraint is invisible.

Treat a finding that says some code "matches upstream exactly" as a report of copying,
not as a confirmation of correctness.
