# Sidearms & Supply for CE

RimWorld 1.6 mod unifying [Combat Extended](https://github.com/CombatExtended-Continued/CombatExtended)
loadouts and [Simple Sidearms](https://github.com/PeteTimesSix/SimpleSidearms) memory into one
mental model. Builds on (and requires) the
[CE + Simple Sidearms Compatibility patch](https://github.com/eebette/CESimpleSidearmsCompat).

## The model

- **CE loadout = doctrine layer** (shared template): consumables policy, and weapon slots as
  doctrine statements.
- **SS sidearm memory = instance layer** (per-pawn working kit): the authority on which
  weapons a pawn maintains.
- Two one-way derivations connect them; no state can desync, no cycles.

## Features (each toggleable in mod settings)

**Doctrine projection** — weapon defs listed in a CE loadout are auto-remembered as sidearms
by assigned pawns. *List order is role order*: first weapon = the main (sets SS default
ranged / preferred melee and combat mode), the rest are backups. Weapons removed from the
loadout are forgotten again — but only if the projection added them; manually remembered
weapons are never touched (tracked per-def in a small saved component). Player overrides of
default/preferred/mode always stick. Generic slots ("any ranged weapon") are ignored —
those are hauling semantics.

**Ammo sustainment** — rides CE's own per-loadout **"Ad hoc"** checkbox. Vanilla CE uses it
to auto-supply ammo for the *equipped primary* only; with this mod it extends to every
weapon *declared in that loadout*, at the loadout's own magazine count. Unticked, behavior
is exactly vanilla CE's curated contract: no ammo rows means no ammo and no demand.
Derived slots are injected at `Loadout.GetSlotsFor` — the same point CE's fetch *and*
excess-drop logic consume, and where CE synthesizes its own ad-hoc slots (deduped against
ours). Hand-added caliber rows (or matching generics) suppress derived demand per ammo def:
explicit beats derived. A separate off-by-default setting extends derivation to ALL
remembered weapons (battlefield pickups included) for full-automation players, at a global
spare-magazine count. Stateless — recomputed every evaluation.

**Weapon refetch** — loadout-declared weapons refetch natively through their real slots.
An opt-in setting extends this to manually remembered weapons: when one goes missing it
becomes a virtual loadout slot, and CE's normal fetch machinery replaces it.

## Building

Same pattern as the compat patch: `dotnet build Source/CESidearmsSupply/CESidearmsSupply.csproj -c Release`.
References the CE and SS workshop DLLs (`-p:RimWorldWorkshopDir=...` to override) and the
compat patch DLL (`-p:CompatModDir=...`). CI cannot build this repo; releases are manual.

## Load order

Harmony → Combat Extended → Simple Sidearms → CE+SS Compatibility → this mod (declared in About.xml).
