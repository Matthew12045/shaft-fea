# The case file

Contents: conventions, fields, supports, loads, mesh, recipes for common conditions, outputs.

A case is one JSON file. Paths may use `/` or doubled `\\`, and a relative path is taken from the case file's folder. Solving a case always starts from the CAD again, so the file is the whole truth about the study: change the file, solve again.

## Conventions

- **Lengths in mm, forces in N, moments and torques in N·mm, stresses in MPa.**
- **Position `s`** is measured along the shaft axis in mm. It always increases in the positive direction of the model axis the shaft lies along. `origin` sets where `s = 0` is:
  - `"model"` (default): `s` is the model coordinate itself. Right when the CAD was drawn from a meaningful origin, such as a bearing centre.
  - `"min"`: `s = 0` at the end of the shaft with the smallest coordinate (the "left" end). The easiest choice for a drawing dimensioned from one end.
  - `"max"`: `s = 0` at the other end; positions on the shaft are then negative.
  - a number: the model coordinate along the axis where `s = 0`.
- **Force and moment vectors** are `[X, Y, Z]` in the model's own axes, the ones the triad in SOLIDWORKS shows. For a shaft along X, a downward load in a Y-up model is `[0, -F, 0]`.
- **`torque`** is about the shaft axis, positive by the right-hand rule about the positive model axis. Torques put on and taken off the shaft should sum to zero; the tool prints the sum.
- `inspect` prints the axis, the ends and every diameter step in the same `s` the case will use (run it once more after choosing `origin` if in doubt: `solve` prints the table with the chosen origin).

## Fields

| Field | Meaning | Default |
|---|---|---|
| `name` | Names the work folder, the work part and every output file. | the case file's name |
| `cad` | The shaft: `.SLDPRT`, `.STEP`/`.STP`, `.IGS`/`.IGES`, `.X_T`/`.X_B`, `.SAT`. | required |
| `workDir` | Where the copy of the CAD, the study and the outputs go. | `<name> FEA` beside the case file |
| `study` | Name of the Simulation study in the work part. | `Static` |
| `axis` | `auto`, or `X`, `Y`, `Z` to force the choice. | `auto` |
| `origin` | Where `s = 0` is (see above). | `model` |
| `material` | See below. | required |
| `supports` | List, at least one. | required |
| `loads` | List, at least one. | required |
| `probes` | Extra positions `s` where the section results are wanted. | none |
| `mesh` | `{ "factor": 0.5 }` or `{ "max_mm": 1.5, "min_mm": 0.5 }`. | factor 0.5 |
| `up` | `[X, Y, Z]`: the direction that should point up in the side-view pictures, for example the opposite of gravity. | a model axis across the shaft |
| `plots` | `false` skips the pictures. | `true` |

`auto` picks the axis shared by the cylindrical faces that span the greatest length. Force it only if the part has a longer feature on another axis.

### Material

Either custom values:

```json
"material": { "name": "POM-C", "E_MPa": 2800, "nu": 0.35, "density": 1410, "yield_MPa": 59.2, "tensile_MPa": 55.3 }
```

`E_MPa`, `nu` and `yield_MPa` are required; `density` (kg/m³) defaults to 7850 and `tensile_MPa` to the yield. Or a SOLIDWORKS library material by its exact name:

```json
"material": { "library": "AISI 1045 Steel, cold drawn" }
```

Its yield strength is read from the library; add `"yield_MPa"` to override it. The factor of safety is the yield divided by the largest von Mises stress.

## Supports

Each support acts on the cylindrical surface between `from` and `to`.

| Field | Meaning | Default |
|---|---|---|
| `name` | For the report. | `support N` |
| `type` | `bearing` or `fixed`. | `bearing` |
| `from`, `to` | The seat: the length of shaft inside the bearing, or the gripped length. | required |
| `at` | Bearing only: where the pivot point is. | middle of the seat |
| `axial` | Bearing only: it also holds the shaft along its axis (the locating bearing). | `false` |
| `spin` | Bearing only: it holds rotation about the axis. | `false` |
| `selfAlign` | Bearing only: `false` also holds the tilt, for a support that cannot follow the shaft's slope. | `true` |

- **`bearing`**: the seat stays round and rigid, as inside an inner ring, and is tied to a pivot point on the axis. The pivot cannot move radially but the shaft may tilt about it, which is how a single-row ball bearing behaves and what a "simple support" means in a hand calculation.
- **`fixed`**: the surface is fully fixed. Use it for a clamped length, such as a cantilever in a vise.

Something has to stop the shaft sliding along its axis and spinning freely, or the solve fails. A real shaft is spun by its drive, but a static study needs a reaction for any torque left over. If no support has `axial` or `spin` and none is `fixed`, the first bearing is given both, and the output says so. Choose deliberately when it matters: `spin` belongs where the unbalanced torque is really reacted.

## Loads

Each load is applied to the cylindrical surface between `from` and `to` as a force and moment acting at the axis point `at`.

| Field | Meaning | Default |
|---|---|---|
| `name` | For the report. | `load N` |
| `at` | Position of the load's line of action. | required |
| `from`, `to` | The surface that carries it: the hub seat, the width of the gear or pulley. | required, unless `width` |
| `width` | Shorthand for a band centred on `at`. | - |
| `force` | `[X, Y, Z]` in N. | zero |
| `torque` | About the axis, N·mm. | 0 |
| `moment` | `[X, Y, Z]` in N·mm, for a bending moment given directly. | zero |

A force is applied at the shaft centre. A force that really acts at a radius (a gear tooth, a belt) is entered as the force vector plus the torque it produces, which is the same thing statically.

The band should be the real contact length. Spreading a load over its hub is closer to reality than a point and avoids a false stress spike. A band may cross a shoulder, but it must cover some cylindrical surface; `inspect` shows where those are.

## Mesh

10-node tetrahedra with the blended curvature-based mesher. `factor` multiplies SOLIDWORKS' default element size for the part: 1 is the middle of the Mesh Density slider, 0.5 its fine end. Explicit `max_mm` and `min_mm` override it. For a number that will be quoted, check convergence with `sweep` rather than trusting one size.

## Recipes

- **Spur gear on the shaft.** Tangential force `Ft = 2 T / d` (pitch diameter `d`), radial force `Fr = Ft · tan(pressure angle)`. Their directions depend on where the mating gear sits: the radial force points from the mesh point towards the shaft centre, the tangential force is square to it. Work out the vector in model axes and enter it with `torque: ±T`.
- **Belt or chain pulley.** The force is the vector sum of the two strand tensions, pointing along the line to the other pulley; the torque is the tension difference times the pitch radius.
- **Weight of a mounted part.** `m · 9.81` N along gravity in model axes, over the hub's seat. No torque.
- **Overhung load.** A band outside the bearings works like any other.
- **Shaft end held in a chuck or coupling that can tilt.** A `bearing` over the gripped length with `at` placed where the holder really pivots (the bearing behind the chuck), which may be off the end of the shaft.
- **Cantilever.** One `fixed` support over the clamped length.
- **Two bearings side by side, or a long plain bush.** One `bearing` over the whole length with `"selfAlign": false`, or two bearings entered separately.
- **A point support, as in a textbook beam.** A narrow band, 1 to 2 mm, at the support point. A wide rigid seat stiffens the shaft over its width.
- **Pure torsion.** Two loads with opposite `torque` and no force; one bearing with `spin` is not needed because the torques cancel, but the tool still assigns one, which is harmless.

## Outputs

In the work folder, all prefixed with the case name:

| File | Content |
|---|---|
| `<name> (FEA).SLDPRT` | The copy of the CAD with split circles and the solved study. Open it to look around. |
| `<name> results.json` | Every number of the report, for reuse. |
| `<name> von Mises.png`, `... von Mises (side).png` | Stress in MPa, isometric and side view. |
| `<name> displacement.png`, `... displacement (side).png` | Resultant displacement in mm. The shape is exaggerated by the deformation scale printed in the picture. |
| `<name> FOS.png`, `<name> mesh.png` | Factor of safety plot and the mesh. |
| `<name> mesh study.csv` | Only after `sweep`: one row per mesh size. |
| `*.CWR` and other solver files | SOLIDWORKS' result database, roughly 100 MB. Keep it with the part or delete it; never upload it. |

`results.json` holds: `max_displacement_mm` with its `_s` and `_r`, `max_von_mises_MPa` with its `_s` and `_r`, `factor_of_safety`, `yield_MPa`, `mesh` (sizes, element and node counts, quality), `sections` (for each band edge, load point and probe: `s`, `diameter`, `u_transverse_mm`, `u_mm` as `[X, Y, Z]`, `von_mises_MPa`, `moment_Nmm`), `supports` (`slope_rad`, `slope_arcmin`), and `warnings`.

What the section values mean: `u_mm` is the mean displacement of the ring of surface nodes at that section, that is the displacement of the section's centre; `u_transverse_mm` is its part across the axis (the deflection a beam calculation gives); `von_mises_MPa` is the largest value on that ring; `moment_Nmm` is the bending moment the section carries, fitted from the axial stress around the ring, and is given only where the section is a full circle.
