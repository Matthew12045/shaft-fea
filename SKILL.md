---
name: shaft-fea
---

# Shaft FEA in SOLIDWORKS Simulation

Given a shaft's CAD and its conditions, this skill produces a solved static study without anyone clicking through SOLIDWORKS. The conditions live in a small JSON case file, so "do it again" or "what if the load were 30 % higher" is an edit and one command.

Scope: one shaft part, linear static, on this PC (Windows, PowerShell 5.1, SOLIDWORKS with Simulation). What it does not cover is listed under "Limits"; say so early if the request falls outside.

## Workflow

```powershell
$f = "$HOME\.claude\skills\shaft-fea\scripts\shaftfea.ps1"
& $f -Action prepare                          # builds the tools, VPN for the licence, starts SOLIDWORKS: 1 to 5 min
& $f -Action inspect -Cad "<shaft CAD>"       # axis, ends, diameters, keyways; changes nothing
#   write the case file (see below)
& $f -Action solve -Case "<case.json>"        # about 1 min for a small shaft
& $f -Action sweep -Case "<case.json>" -Factors "1,0.75,0.5"   # optional: mesh convergence
& $f -Action finish                           # closes SOLIDWORKS, VPN off
```

Each tool call starts a new shell, so define `$f` again in every call, and never rely on a variable or `$env:` value set in an earlier call. Run `prepare`, `solve` and `sweep` in the background with a 10 minute timeout.

### 1. Inspect before asking anything

`inspect` prints where the shaft's axis is, where it starts and ends, every diameter step with its position, and the keyways and flats. Positions are `s`, in mm along the axis. With that table in hand you can check the user's numbers against the CAD (a bearing "at 168" should sit on a seat), and you can ask precise questions instead of open ones.

### 2. Get the conditions

A study needs four things. Take them from what the user gave; ask only for what is missing, in one message.

- **Supports**: where the shaft is held (bearing seats, a chuck, a clamp) and what each one holds.
- **Loads**: each force with its position, direction and size, and each torque. Weights of mounted parts (gear, pulley, flywheel) are loads too.
- **Material**: elastic modulus, Poisson's ratio and yield strength, or a SOLIDWORKS library material name.
- **Where results are wanted**: positions for deflection and section stress, and any limit to check against (clearance, allowable slope, required factor of safety).

Do not invent a load or a material value. A guessed number looks exactly like a given one in the report. If the user wants a quick look with assumed values, state each assumption in the reply.

### 3. Write the case file

```json
{
  "name": "pump shaft",
  "cad": "C:/work/pump shaft.SLDPRT",
  "origin": "min",
  "material": { "name": "S45C", "E_MPa": 205000, "nu": 0.29, "density": 7850, "yield_MPa": 343 },
  "supports": [
    { "name": "bearing A", "type": "bearing", "from": 20, "to": 35, "axial": true, "spin": true },
    { "name": "bearing B", "type": "bearing", "from": 180, "to": 195 }
  ],
  "loads": [
    { "name": "gear", "at": 90, "from": 80, "to": 100, "force": [0, -850, 310], "torque": 12000 },
    { "name": "coupling", "at": 230, "from": 215, "to": 245, "torque": -12000 }
  ],
  "probes": [60, 110, 150]
}
```

Save the case file next to the user's CAD or in their project folder, not in a temp folder: it is the record of what was analysed. The work folder (`<case name> FEA`, beside the case file unless `workDir` says otherwise) receives a copy of the CAD with the study in it, the result pictures and `<case name> results.json`. The CAD the user gave is never modified.

### 4. Solve and read the result

`solve` prints the geometry it found, each support and load as applied, the mesh, and then the results: largest displacement and von Mises with their positions, factor of safety, a table of sections (centre displacement, surface von Mises, fitted bending moment), and the slope of the shaft through each bearing. Read the echo of supports and loads before the results: a wrong band or sign shows there.

Then look at the pictures (`... displacement (side).png`, `... von Mises.png`). The deformed shape should be what a person would expect: sagging between supports, the right end moving the right way. If it surprises you, the case is wrong, not the physics.

Judging the numbers:

- **The stress peak** usually sits at a keyway end, groove or shoulder. That is a stress concentration: it keeps rising as the mesh gets finer. Report it as the peak of this mesh, and use the section table for the nominal stress.
- **Convergence**: run `sweep` when the result will be quoted in a report. Displacements and section stresses should move by well under 1 % between the last two sizes.
- **Cross-check**: the fitted bending moments should match a hand free-body calculation within about 1 %. If the user has hand values, compare them; this catches a wrong load faster than anything else.
- **Bearing slope**: deep-groove ball bearings tolerate only a few arcminutes of misalignment (roughly 2 to 10). Mention a slope above that even if nobody asked.
- **Factor of safety** here is static yield over the peak von Mises. It says nothing about fatigue.

### 5. Report

Lead with what the user asked for (is it strong enough, does it clear, how much does it deflect), then the key numbers with units and positions, then what the model assumed (support model, material values, loads as applied, mesh size), then anything that looked wrong. Send the side-view pictures when they help. Say where the case file and the work folder are.

## While SOLIDWORKS is running

- One SOLIDWORKS, one job: never run two tool commands at once, and never give SOLIDWORKS work to parallel subagents.
- `finish` and `close` only act on a SOLIDWORKS that `prepare` started. If it was already open, the user's own documents may be in it and they would be closed without saving, so leave it unless you have checked (`-Force`).
- The licence on this PC needs the `KMUTTVPN` connection, named in `machine.json`. `prepare` connects it by clicking Connect in Windows Settings (the user asked for that route, since `rasdial` fails with error 645) and `finish` disconnects it.
- PowerShell cannot call the SOLIDWORKS COM object directly (TYPE_E_ELEMENTNOTFOUND); everything goes through the compiled tools in `scripts\bin`.
- `selftest` solves a plain bar against beam theory in about a minute. Use it when a result looks wrong and you need to know whether the tool or the case is at fault.

## Limits

Say these plainly when they apply, rather than bending the model to fit:

- **Axis parallel to a model axis** (X, Y or Z; it need not pass through the origin). A skew shaft has to be re-oriented first.
- **No gravity on the shaft itself, no contact, no nonlinearity, no fatigue, no dynamics.** Put weights in as loads.

## Reference files

- [references/case-file.md](references/case-file.md): every case field, conventions, recipes for common conditions, and the output files. Read it before writing a case.
- [references/method.md](references/method.md): how the model is built and why, how each result is computed, what was validated and how closely, setup on another PC, and troubleshooting. Read it before defending a result or when something fails.

