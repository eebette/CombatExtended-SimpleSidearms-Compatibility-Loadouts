# CombatExtended-SimpleSidearms Compatibility Module - Loadouts

[![Combat Extended Compatible](Media/Badge_CE_compatible.png)](https://steamcommunity.com/sharedfiles/filedetails/?id=2890901044)
![CE + Simple Sidearms Compatibility Suite](Media/Badge_Suite.png)
![CE + Simple Sidearms Loadouts Module](Media/Badge_Loadouts.png)

RimWorld mod that syncs [Combat Extended](https://github.com/CombatExtended-Continued/CombatExtended)
loadouts and [Simple Sidearms](https://github.com/PeteTimesSix/SimpleSidearms). Builds on (and requires) the
[CE + Simple Sidearms Compatibility patch](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Patch).

## Features (each toggleable in mod settings)

* Weapons in a pawn's Loadout will be added to Simple Sidearms sidearm memory (as if equipped using _Equip as sidearm_).
* The first firearm in the Loadout will be added as primary firearm, and the first melee weapon will be added as the
  pawn's primary melee weapon.
* Removing a weapon from a pawn's Loadout will automatically forget that weapon from the pawn's SS memory.
* A pawn's loadout will periodically reconcile with the Simple Sidearms memory to ensure they're sync'ed. 
* Primary weapon can be overridden for non-Loadout weapons by simply Equipping them. 

## Load order

> Harmony → Combat Extended → Simple Sidearms → CE+SS Compatibility → this mod.

## My other mods

### The CE + Simple Sidearms suite

Two optional modules sit on top of this patch and require it. This patch only fixes core incompatibilities; enhancements
in these instead.

| Module                                                                                                                                             | What it does                                                         |
|----------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------|
| [![CE + Simple Sidearms Compatibility Patch](Media/Badge_Patch.png)](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Patch) | Core compatibility patch for Combat Extended and Simple Sidearms.    |
| [![Compatibility Module - Tactics](Media/Badge_Tactics.png)](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Tactics)       | Sensible tweaks to nonsense pawn behavior when CE + SS run together. |

### Standalone

| Mod                                                                                                                                     | What it does                                                                                                                    |
|-----------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------|
| [![Better Attack Orders for Simple Sidearms](Media/Badge_BAO.png)](https://github.com/eebette/Better-Attack-Orders-for-Simple-Sidearms) | Adds sidearm attack orders to the right-click target menu.                                                                      |
| [![Loadout Quality for Combat Extended](Media/Badge_LQ.png)](https://github.com/eebette/Loadout-Quality-for-Combat-Extended)            | Pawns will upgrade their held guns when a higher-quality copy is available.                                                     |
| [![Universal Patch for More Materials](Media/Badge_UPMM.png)](https://github.com/eebette/Universal-Patch-for-More-Materials)            | Adds materials from [More Materials](https://steamcommunity.com/sharedfiles/filedetails/?id=3055040889) to non-vanilla recipes. |

## FAQ

**CE compatible?**

I'm not answering that.

**Can I add or remove it mid-save?**

Yep.

**Does it change balance?**

It makes the game easier in the sense that 2 core combat mods work better together.

**AI?**

This mod was engineered with the help of an AI Coding Assistant (Claude Code, Fable 5, Max effort). The amount of
researching and deep-diving the compatibility interfaces of mods that it patches would have been insurmountable without
it.

Development followed a standard process driven and scrutinized by me (the real human person writing this):
explore, design, build, test, fix, review, scrutinize, test again over many rounds.

I have manually reviewed and verified all code in this mod.

I ask that if you have unconstructive feedback regarding the usage of AI while developing this mod, that it remains
outside of this community space. Thank you.

## Building

Same pattern as the compat patch:
`dotnet build Source/CESimpleSidearmsCompat.Loadouts/CESimpleSidearmsCompat.Loadouts.csproj -c Release`. References the
CE and SS workshop DLLs (`-p:RimWorldWorkshopDir=...` to override). The compatibility patch is a runtime dependency but
not a build one — this module binds to no type in it. CI cannot build this repo; releases are manual.

## License

[MIT](LICENSE) - code, build files, and docs.

The badge artwork is not: `About/Preview.png` and the `Media/Badge_*.png` set remix the rifle glyph from Combat
Extended's own compatibility badge, so they stay under CE's CC BY-NC-SA 4.0 (attribution, non-commercial, share-alike).
Details in [NOTICE](NOTICE).
