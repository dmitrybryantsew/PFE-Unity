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

If you are only cloning the repo to open, run, or build the Unity project, you can ignore this setting.
