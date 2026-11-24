# Method, validation and troubleshooting

Contents: how the model is built, the bearing model, how results are computed, validation, setup on another PC, the tools, troubleshooting.

## How the model is built

1. **Working copy.** The CAD is copied (or, for a neutral file, imported and saved) to `<name> (FEA).SLDPRT` in the work folder. The original is opened read-only at most.
2. **Axis.** Every cylindrical face whose axis is parallel to model X, Y or Z is grouped by axis line. The group that spans the greatest length is the shaft. Its faces give the diameter steps; cylinders inside a larger one are bores; plane faces parallel to the axis are reported as keyways and flats.
3. **Split circles.** A sketch on a default plane that contains the axis direction gets one line per station (every band edge, load point and probe), and is projected onto the shaft surface as a split line. Each band then has faces of its own and each station has a ring of nodes exactly on it.
4. **Study.** Static, with the material on every body, the supports and loads below, 10-node tetrahedra, blended curvature-based mesher.
5. **Loads** are SOLIDWORKS remote loads with direct transfer: the force and moment act at a point on the axis and are spread over the band's faces.
6. **Supports**: see the next section. `fixed` is a Fixed Geometry restraint.

## The bearing model

A `bearing` is a remote displacement with a rigid connection: the seat's faces are tied rigidly to a point on the axis; that point's radial translations are zero (and the axial one if `axial`), its rotations are free (except about the axis if `spin`, and all of them if `selfAlign` is false).

Why not SOLIDWORKS' Bearing Fixture: the Simulation API offers no call that creates one. It can only be added by clicking in the window.

How the two compare, measured on the G27 main shaft (Ã˜12 to Ã˜16 POM shaft, bearings 168 mm apart, 55 mm overhang), same mesh settings:

| Result | Bearing Fixture (self-aligning, rigid) | This model |
|---|---|---|
| Deflection between the bearings (flywheel) | 0.610 mm | 0.613 mm |
| Section moments at three stations | 363.1 / 853.9 / 690.1 NÂ·mm | 363.1 / 853.0 / 690.1 NÂ·mm |
| Largest displacement (free overhung end) | 0.861 mm | 0.818 mm |

Between the bearings the difference is about 0.3 %. At the overhung end this model is 5 % stiffer, because the seat stays rigid over its whole width and so the shaft leaves the bearing at a slightly smaller slope. A real inner ring also stiffens the seat, so neither is simply "right"; say which model was used. To soften the seat, narrow the band. If a report has to state "Bearing Fixture", build the study with this skill, then replace each bearing by a Bearing Fixture in the window.

A distributed (flexible) coupling for the support was tried and does not work in SOLIDWORKS 2025: the load type is refused, and with the connection type set to distributed the solve fails.

## How results are computed

All from the nodal results of the solved study.

- **Largest displacement and von Mises**: over all nodes, with the position `s` and the radius of the node.
- **Section centre displacement**: the mean displacement of the surface nodes on the split circle at that station.
- **Section von Mises**: the largest value on that ring.
- **Bending moment**: a plane `Ïƒ = aÂ·y + bÂ·z + c` is fitted through the axial normal stress around the ring; `M = IÂ·âˆš(aÂ² + bÂ²)` with `I = Ï€(Dâ´ âˆ’ dâ´)/64`. Reported only where the section is a full circle. This recovers the internal moment even though the API does not return the reactions of remote-displacement supports.
- **Slope through a bearing**: the difference of the centre displacements at the two edges of the seat, divided by the seat width.
- **Factor of safety**: yield over the largest von Mises.

A station that falls on an existing edge (a shoulder) gets no new split circle and is evaluated on the existing edge; at a shoulder the larger diameter is used, so read stress there with care. A station on a non-cylindrical stretch (a taper, a thread) gets no section result.

## Validation

- **Self-test** (`shaftfea.ps1 -Action selftest`): a Ã˜20 Ã— 200 steel bar drawn along Z and off the origin, two bearings 190 mm apart, 1000 N in the middle. Deflection under the load 0.08845 mm against 0.08872 mm from beam theory with shear (âˆ’0.3 %); bending moment at the quarter point within 0.1 % of statics. It fails loudly if either is off by more than 5 % or 3 %.
- **G27 main shaft**, against the hand calculation of that project: section moments within 0.7 %, deflections +11 to +15 % because the hand calculation ignores the keyways (+2.2 % with the keyways suppressed, which is shear deformation).
- **STEP input**: the same shaft exported to STEP and solved from the STEP file gives the same results to four digits (0.6125 mm, 853.6 NÂ·mm).
- **Mesh**: on the G27 shaft, displacement and section stress change by less than 0.2 % from about 20,000 elements upwards; the keyway-end peak moves by 2 to 3 %.

## Setup on another PC

`machine.json` in the skill folder:

```json
{ "solidworksExe": "C:\\Program Files\\SOLIDWORKS Corp\\SOLIDWORKS\\SLDWORKS.exe",
  "interopDir": "C:\\Program Files\\SOLIDWORKS Corp\\SOLIDWORKS\\api\\redist",
  "vpn": { "name": "KMUTTVPN" } }
```

Remove `vpn` (or set it to `null`) where the licence needs no VPN. Delete `scripts\bin` to force a clean rebuild; `prepare` compiles the three tools with the .NET Framework compiler that ships with Windows and copies the SOLIDWORKS interop libraries from `interopDir`. No Python, no Visual Studio.

## The tools

| File in `scripts` | Role |
|---|---|
| `shaftfea.ps1` | Session helper: `status`, `build`, `prepare`, `inspect`, `solve`, `sweep`, `selftest`, `close`, `finish`. |
| `ShaftFea.cs` | The FEA driver: `inspect`, `solve`, `sweep`, `selftest`, and `export <part> <target.step>` to save a part in a neutral format. |
| `SwDocs.cs` | Read-only: is SOLIDWORKS answering, which documents are open. |
| `CloseNoSave.cs` | Closes every document without saving; exits SOLIDWORKS unless given `keep`. |
| `vpn_connect.ps1` | Clicks Connect for a VPN profile in Windows Settings. |

The compiler is C# 5: no `?.`, no string interpolation, no `out var`. After editing a `.cs`, `shaftfea.ps1 -Action build` recompiles what changed.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `prepare` times out | A licence or start-up dialog is waiting on screen. Check the VPN, look at a screenshot. |
| "no cylindrical face between s = ..." | The band is not on the shaft's surface, or `origin` is not what you assumed. Compare with the table `solve` printed. |
| "the solve failed (error ...)" | The supports leave a free motion (commonly: one self-aligning bearing only), or the mesh failed. |
| "meshing failed" | Element size too large for a thin feature. Use a smaller `factor`, or explicit sizes. |
| A warning about split circles close together | Two stations nearly coincide. Move or drop one; sliver faces make a heavy, poor mesh. |
| Wrong axis chosen | Set `"axis"` explicitly. |
| "cylinders are skew to X, Y and Z" | The shaft is not parallel to a model axis. Re-orient the part (Move/Copy Body, or re-export) first. |
| Results or pictures empty after many runs | Restart SOLIDWORKS: `finish`, then `prepare`. |
| A displacement picture in strange units | The tool resets the legend to mm; if a picture still looks off, open the work part and check the plot. |
| A command fails right after another session used SOLIDWORKS | Documents left open under the same file name are reused by SOLIDWORKS. `close` first. |

