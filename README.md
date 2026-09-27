# PFE-Unity

Unity project for PFE.

## Opening The Project

Open the project in Unity `6000.3.10f1`.

For normal use, the repository should be self-contained:
- You do not need to set `PFE_IMPORT_ROOT` just to open the project.
- You do not need to reimport legacy source data for gameplay, scenes, or builds.

## `PFE_IMPORT_ROOT`

`PFE_IMPORT_ROOT` is an optional environment variable used by editor-only import tools.

It is only needed if you want to rerun the original data or asset import pipeline from external source files such as:
- `pfe/scripts/fe/AllData.as`
- `pfe/scripts/fe/Snd.as`
- `pfe/scripts/_assets/assets.swf`
- `pfe/sprites`
- `texture.swf`
- `texture1.swf`

Example:

```powershell
$env:PFE_IMPORT_ROOT = "C:\path\to\pfeToUnity"
```

With that set, importer tools will look for source files under that root.

### You usually do not need to set it

The importers resolve the root themselves, in this order:

1. `PFE_IMPORT_ROOT`, if it is set **and** the folder it names actually contains `pfe/scripts/fe/AllData.as`.
2. A root saved from the editor — menu **`PFE/Data/Import Source Root…`**.
3. **Auto-discovery** — a bounded search of the folders around the Unity project and a few
   conventional dev locations, looking for a folder that contains `pfe/scripts/fe/AllData.as`.

So a source tree sitting beside the project is found with no setup at all, and a copied project only
needs the menu item once. The window also shows **which** root is in use and where it came from,
which is the thing worth knowing when an import behaves oddly; `PFE/Data/Log Import Source Resolution`
prints the same information to the console.

If the editor finds more than one source tree it says so rather than choosing silently — importing
from a stale copy produces plausible, wrong data. Use the menu item to pin the one you mean.

> `PFE_IMPORT_ROOT` is per-process environment state. The Unity Editor does not inherit a variable
> set in a shell you opened afterwards, which is why the editor-launched case is the one that fails
> even though the same command works from a terminal.

If you are only cloning the repo to open, run, or build the Unity project, you can ignore all of this.
