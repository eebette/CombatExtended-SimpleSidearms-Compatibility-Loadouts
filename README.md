# CombatExtended-SimpleSidearms Compatibility Module - Loadouts

[![Combat Extended Compatible](Media/Badge_CE_compatible.png)](https://steamcommunity.com/sharedfiles/filedetails/?id=2890901044)
![CE + Simple Sidearms Compatibility Suite](Media/Badge_Suite.png)
![CE + Simple Sidearms Loadouts Module](Media/Badge_Loadouts.png)

RimWorld 1.6 mod unifying [Combat Extended](https://github.com/CombatExtended-Continued/CombatExtended)
loadouts and [Simple Sidearms](https://github.com/PeteTimesSix/SimpleSidearms) memory into one
mental model. Builds on (and requires) the
[CE + Simple Sidearms Compatibility patch](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Patch).

## The suite

[![Compatibility Patch](Media/Badge_Patch.png)](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Patch)

The core, repair-only patch — **required** by this module. Eleven repair axes so
CE and Simple Sidearms work as their authors intended.

[![Compatibility Module - Tactics](Media/Badge_Tactics.png)](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Tactics)

Sibling module (not required): combat-time weapon choice — reload-abort when
threatened, target-aware ammo and armor-aware melee scoring.

## The model

- **CE loadout = template layer** (shared template): consumables policy, and weapon slots as
  kit declarations.
- **SS sidearm memory = instance layer** (per-pawn working kit): the authority on which
  weapons a pawn maintains.
- Two one-way derivations connect them; no state can desync, no cycles.

## Features (each toggleable in mod settings)

**Loadout weapons as sidearms** — weapon defs listed in a CE loadout are auto-remembered as sidearms
by assigned pawns. *List order is role order*: first weapon = the main (sets SS default
ranged / preferred melee and combat mode), the rest are backups. Weapons removed from the
loadout are forgotten again — **whatever the loadout lists, the loadout owns**, including
weapons Simple Sidearms had already remembered on its own (it auto-remembers anything a pawn
equips as primary, which is the usual case when you build a loadout around a gun a pawn is
already carrying). Forgetting the memory is what lets CE clear the weapon out of the
inventory, so *removed from loadout → removed from the pawn*. Weapons the loadout never
listed are never touched (tracked per-def in a small saved component). Forget a declared
weapon in SS's own gizmo and it stays forgotten — that is how you say *carry this but don't
wield it*, which removing the loadout row cannot express (that would stop the pawn carrying
it at all). Put it back in the list and the loadout manages it again. A def the loadout
DOES list is claimed whoever remembered it first — that is the rule that makes "removed from
loadout, removed from the pawn" hold, since Simple Sidearms auto-remembers any weapon a pawn
equips as primary. The main-weapon role is the head of one ordered list — *the weapon you put in their hands*,
then *the loadout's order* — filtered to what the pawn is actually carrying. Equip something
the loadout doesn't list and it leads while they hold it; stow it and the loadout's first
takes over; pick it back up and it leads again. Forced weapons and "prefer unarmed" outrank
the list entirely and are never touched. Generic slots ("any ranged weapon") and multi-count
weapon slots (trade stock) are ignored — those are hauling/cargo semantics; kit declaration
is a single copy of a specific def.

**Ammo sustainment** — rides CE's own per-loadout **"Ad hoc"** checkbox. Vanilla CE uses it
to auto-supply ammo for the *equipped primary* only; with this mod it extends to every
weapon *declared in that loadout*, at the loadout's own magazine count. Unticked, behavior
is exactly vanilla CE's curated contract: no ammo rows means no ammo and no demand.
Derived slots are injected at `Loadout.GetSlotsFor` — the same point CE's fetch *and*
excess-drop logic consume, and where CE synthesizes its own ad-hoc slots (deduped against
ours). Hand-added caliber rows (or matching generics) suppress derived demand per ammo def:
explicit beats derived. A separate off-by-default setting extends derivation to ALL
remembered weapons (battlefield pickups included) for full-automation players, at a global
spare-magazine count. Sizing follows CE's own ad-hoc arithmetic, including its mass/bulk
clamps and the carried-amount band that keeps a pawn one round short from walking to storage
for one round.

**Capacity-aware retrieval** — Simple Sidearms already fetches remembered weapons a pawn
isn't carrying (`JobGiver_RetrieveWeapon`, in the vanilla think tree, on by default), and it
does so without consulting CE's weight and bulk model — neither its job giver nor its pickup
toil checks capacity. This cancels a retrieval CE says the pawn has no room for, rather than
letting them haul it back and have it count against everything else they carry. SS still
decides which weapons are worth fetching; this only supplies the limit it can't see.

## Building

Same pattern as the compat patch: `dotnet build Source/CESidearmsSupply/CESidearmsSupply.csproj -c Release`.
References the CE and SS workshop DLLs (`-p:RimWorldWorkshopDir=...` to override). The
compatibility patch is a runtime dependency but not a build one — this module binds to no
type in it. CI cannot build this repo; releases are manual.

## Load order

Harmony → Combat Extended → Simple Sidearms → CE+SS Compatibility → this mod (declared in About.xml).

## License

[MIT](LICENSE) — code, build files, and docs.

The badge artwork is not: `About/Preview.png` and the `Media/Badge_*.png` set remix the rifle
glyph from Combat Extended's own compatibility badge, so they stay under CE's CC BY-NC-SA 4.0
(attribution, non-commercial, share-alike). Details in [NOTICE](NOTICE).
