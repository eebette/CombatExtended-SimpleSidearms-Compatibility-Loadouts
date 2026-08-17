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

**Ammo sustainment** — every remembered weapon (including the primary) derives spare-magazine
ammo demand into the pawn's loadout evaluation, injected at `Loadout.GetSlotsFor` — the same
point CE's fetch *and* excess-drop logic consume, and where CE itself already synthesizes
ad-hoc ammo slots. Hand-added caliber rows (or matching generics) in the loadout suppress
the derived demand for that ammo def: explicit beats derived. Stateless — recomputed from
memory every evaluation.

**Weapon refetch** — a remembered weapon that goes missing becomes a virtual loadout slot,
so CE's normal fetch machinery replaces it.

## Building

Same pattern as the compat patch: `dotnet build Source/CESidearmsSupply/CESidearmsSupply.csproj -c Release`.
References the CE and SS workshop DLLs (`-p:RimWorldWorkshopDir=...` to override) and the
compat patch DLL (`-p:CompatModDir=...`). CI cannot build this repo; releases are manual.

## Load order

Harmony → Combat Extended → Simple Sidearms → CE+SS Compatibility → this mod (declared in About.xml).
