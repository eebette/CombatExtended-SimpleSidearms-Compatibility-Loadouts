# Releasing

Same no-CI reality as the core patch (see its
[RELEASING.md](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Patch/blob/master/RELEASING.md)
for the full rationale): compile references are the CE / Simple Sidearms /
core-patch DLLs from local folders, none vendorable. Releases are manual local
builds with `Assemblies/CESidearmsSupply.dll` committed.

## Release checklist

1. **Build the core patch first** (this mod compiles against its DLL), then:

   ```bash
   dotnet build Source/CESidearmsSupply/CESidearmsSupply.csproj -c Release
   ```

2. **Automated test pass** (regenerate saves, then both scenarios):

   ```bash
   ./test/run-supply-stage.sh          # quit after the in-game letter
   ./test/run-supply-assert.sh supply1 SUPPLY-1-loadout-sidearms
   ./test/run-supply-assert.sh supply2 SUPPLY-2-refetch
   ```

   Both `test-results-*.json` must report `"passed": true`. Manual residue
   (gizmo rendering, CE-ammo-disabled run, save/load persistence, removal
   check) is listed in `TESTPLAN.md`.

3. **Commit the DLL** together with the source it was built from.

4. **Record versions**: CE, Simple Sidearms, AND the core compat patch version
   tested against — this module's contract includes the patch's public surface
   (`CompatUtil`, `HoldSync`).

5. **Tag and publish** (`git tag vX.Y.Z && git push --tags`, `gh release create`).

## Versioning & save compatibility

Semver; `v1.0.0` ships together with the core patch's Workshop release (suite
release order: see the patch repo's `docs/SUITE_RELEASE.md`).

- **Safe to ADD mid-save**: reconcile and derivation are lazy and stateless;
  template records build up from live state.
- **Safe to REMOVE mid-save**, with one cosmetic caveat: the scribed
  `SupplyGameComponent` produces a one-time "unknown GameComponent" load
  warning, then RimWorld drops it. Remaining footprint is SS sidearm memories —
  inert, SS-owned data. No corruption.

A change breaking either guarantee bumps major + documents migration.
