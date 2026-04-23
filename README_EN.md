[Korean README](README.md)

# Unity Font Replacer AT

`Unity_Font_Replacer_AT` is the C# / `AssetsTools.NET` port of `Unity_Font_Replacer`.  
It scans, replaces, exports, and generates Unity TTF and SDF fonts.

## At a Glance

- Replace Unity `Font` TTF assets
- Replace SDF `MonoBehaviour`, atlas, and material data
- Replace selected fonts through `parse -> edit JSON -> list`
- Bulk replacement through `batch` and `oneshot`
- Generate SDF/Raster sets from TTF with `makesdf`
- Auto-generate dummy DLLs for Il2Cpp games without `Managed`
- Auto-download `classdata.tpk`

## Release Layout

Release ZIPs usually contain:

```text
release/
├── UnityFontReplacer_EN.exe
├── CharList_3911.txt
├── ASSETS/
├── Il2CppDumper/
├── LICENSE
└── README_EN.md
```

- `UnityFontReplacer_KO.exe`: Korean UI
- `UnityFontReplacer_EN.exe`: English UI
- `CharList_3911.txt`: default charset
- `ASSETS/`: bundled fonts and generated/replacement assets
- `Il2CppDumper/`: helper for Il2Cpp dummy `Managed` generation
- `LICENSE`: license for code authored in this repository

`classdata.tpk` is not shipped in releases.  
The tool downloads it automatically when needed.

## Scenario-Based Examples

### When You Want To Replace Just One Font

The easiest path is `parse -> edit JSON -> list`.

1. Generate an SDF font from your TTF.

```bat
UnityFontReplacer_EN.exe makesdf --ttf "D:\Fonts\Galmuri14.ttf"
```

2. Put the generated files in `ASSETS/`.

- If you ran `makesdf` from the executable folder, the files are already in `./ASSETS/`.
- If you ran it somewhere else, move `Galmuri14 SDF.json`, `Galmuri14 SDF Atlas.png`, and `Galmuri14 SDF Material.json` into the executable folder's `ASSETS/`.

3. Generate the game's font mapping JSON.

```bat
UnityFontReplacer_EN.exe parse --gamepath "D:\Games\MyGame"
```

4. Edit the generated JSON and set `Replace_to` for the font you want to replace.

```json
"Replace_to": "Galmuri14 SDF.json"
```

5. Apply the edited JSON.

```bat
UnityFontReplacer_EN.exe list --gamepath "D:\Games\MyGame" --file "D:\Work\MyGame_fontmap.json"
```

Even if you are not comfortable with programming, this workflow is manageable as long as you follow the steps in order.  
The key part is that `Replace_to` should point to the generated SDF JSON file.

### When You Want To Replace Every Font With `Mulmaru`

```bat
UnityFontReplacer_EN.exe batch --gamepath "D:\Games\MyGame" --font mulmaru
```

- `mulmaru` is a built-in preset.
- SDF targets are replaced using auto-generated temporary Raster sets per original game `atlas padding`.
- TTF targets are replaced with the bundled `Mulmaru.ttf`.

### When You Want To Replace Every Font With Your Own TTF

```bat
UnityFontReplacer_EN.exe oneshot --gamepath "D:\Games\MyGame" --font "D:\Fonts\NanumMyongjo.ttf"
```

- TTF targets are replaced directly with the input TTF.
- SDF targets are replaced using temporary SDF sets auto-generated per original game `atlas padding`.

### When You Want To Force Raster Behavior For A Pixel Font

Use `--force-raster`. `--raster` is an alias.

```bat
UnityFontReplacer_EN.exe oneshot --gamepath "D:\Games\MyGame" --font "D:\Fonts\MyPixelFont.ttf" --force-raster
UnityFontReplacer_EN.exe batch --gamepath "D:\Games\MyGame" --font "D:\Fonts\MyPixelFont.ttf" --force-raster
```

- Raster mode defaults the texture `FilterMode` to `Point(0)`.
- Materials are rebuilt into a minimal raster-safe form, matching the original Python behavior.

## Command Summary

| Command | Description |
|---------|-------------|
| `batch` | Replace all fonts using a built-in preset or custom font source |
| `oneshot` | Replace all TTF/SDF fonts from a single TTF |
| `parse` | Generate a game font mapping JSON |
| `list` | Replace selected fonts from a JSON mapping |
| `export` | Export SDF font assets from the game |
| `makesdf` | Generate SDF/Raster assets from TTF |
| `diag` | Bundle/assets diagnostic helper |

## `batch`

```bat
UnityFontReplacer_EN.exe batch --gamepath "D:\Games\MyGame" --font mulmaru
UnityFontReplacer_EN.exe batch --gamepath "D:\Games\MyGame" --font nanumgothic --sdfonly
UnityFontReplacer_EN.exe batch --gamepath "D:\Games\MyGame" --font "D:\Fonts\MyFont.ttf"
UnityFontReplacer_EN.exe batch --gamepath "D:\Games\MyGame" --font "D:\Fonts\MyPixelFont.ttf" --force-raster
```

### Options

| Option | Description |
|--------|-------------|
| `--gamepath`, `-g` | Game root or `_Data` / `Data` directory |
| `--font`, `-f` | `mulmaru`, `nanumgothic`, a font folder, `.ttf`, `.otf`, or `.json` |
| `--sdfonly` | Replace SDF only |
| `--ttfonly` | Replace TTF only |
| `--force-raster`, `--raster` | Force raster behavior for all SDF replacements |
| `--output-only <dir>` | Write modified files to another folder instead of in-place |
| `--ps5-swizzle` | Enable PS5 atlas swizzle handling |

### Accepted `--font` forms

- `mulmaru`: built-in preset, default SDF mode is `raster`
- `nanumgothic`: built-in preset, default SDF mode is `sdf`
- directory path: if it contains TTF/OTF, temporary assets are auto-generated; otherwise provided JSON/PNG assets are used
- `.ttf` / `.otf`: source for both TTF replacement and auto-generated SDF replacement
- `.json`: prebuilt SDF source

For SDF targets, `batch` behaves like `oneshot`: it auto-generates temporary assets for each original game `atlas padding` value.

## `oneshot`

```bat
UnityFontReplacer_EN.exe oneshot --gamepath "D:\Games\MyGame" --font "D:\Fonts\NanumMyongjo.ttf"
UnityFontReplacer_EN.exe oneshot --gamepath "D:\Games\MyGame" --font "D:\Fonts\NanumMyongjo.ttf" --sdfonly
UnityFontReplacer_EN.exe oneshot --gamepath "D:\Games\MyGame" --font "D:\Fonts\MyPixelFont.ttf" --force-raster
UnityFontReplacer_EN.exe oneshot --gamepath "D:\Games\MyGame" --font "D:\Fonts\MyFont.ttf" --atlas-size 2048,2048 --point-size 48
```

### Options

| Option | Description |
|--------|-------------|
| `--gamepath`, `-g` | Game root or `_Data` / `Data` directory |
| `--font`, `-f` | Input TTF/OTF path or resolvable font name |
| `--sdfonly` | Replace SDF only |
| `--ttfonly` | Replace TTF only |
| `--force-raster`, `--raster` | Generate temporary raster atlases for SDF replacements |
| `--sdf` | Generate temporary SDF atlases for SDF replacements |
| `--atlas-size <W,H>` | Temporary atlas size |
| `--point-size <n>` | Temporary generation point size (`0` = auto) |
| `--charset <file-or-text>` | Temporary generation charset |
| `--filter-mode <mode>` | `auto` / `point` / `bilinear` / `trilinear` |
| `--output-only <dir>` | Write modified files to another folder instead of in-place |
| `--ps5-swizzle` | Enable PS5 atlas swizzle handling |

### Behavior

- The input TTF is used directly for Unity `Font` replacement.
- SDF targets are generated while preserving each original target's `atlas padding`.
- Default generation mode is `sdf`.
- Default charset is `CharList_3911.txt`.
- Default filter mode is `auto`.
  `auto` resolves to `bilinear` for SDF generation and `point` for `--force-raster/--raster`.
- `--force-raster` and `--sdf` cannot be used together.
- `oneshot` does not expose `--padding` because it preserves the target game's original padding values.

## `parse`

```bat
UnityFontReplacer_EN.exe parse --gamepath "D:\Games\MyGame"
UnityFontReplacer_EN.exe parse --gamepath "D:\Games\MyGame" --ps5-swizzle
```

### Options

| Option | Description |
|--------|-------------|
| `--gamepath`, `-g` | Game root or `_Data` / `Data` directory |
| `--ps5-swizzle` | Detect PS5 atlas swizzle state |
| `--max-workers <n>` | Number of parallel scan workers |

### Output

- Writes a mapping file such as `GameName.json` next to the executable or current run location.
- SDF entries include fields such as `atlas_padding`, `atlas_path_id`, and `force_raster`.

## `list`

```bat
UnityFontReplacer_EN.exe list --gamepath "D:\Games\MyGame" --file "D:\Work\MyGame_fontmap.json"
```

### Options

| Option | Description |
|--------|-------------|
| `--gamepath`, `-g` | Game root or `_Data` / `Data` directory |
| `--file <file>` | Path to the JSON created by `parse` |

### `Replace_to` rules

For SDF entries, `Replace_to` may point to:

- an SDF asset such as `Mulmaru`, `NanumGothic`, or `MyFont SDF.json`
- a TTF/OTF such as `D:\Fonts\MyFont.ttf` or `D:\Fonts\MyFont.otf`

If `Replace_to` points to a TTF/OTF, `list` behaves like `oneshot` for that entry: it auto-generates a temporary set using the original target SDF `atlas padding`, then applies the generated replacement.

## `export`

```bat
UnityFontReplacer_EN.exe export --gamepath "D:\Games\MyGame"
```

### Options

| Option | Description |
|--------|-------------|
| `--gamepath`, `-g` | Game root or `_Data` / `Data` directory |

### Output

```text
exported_fonts/
├── Some Font SDF.json
├── Some Font SDF Atlas.png
└── Some Font SDF Material.json
```

## `makesdf`

```bat
UnityFontReplacer_EN.exe makesdf --ttf "D:\Fonts\MyFont.ttf"
UnityFontReplacer_EN.exe makesdf --ttf "D:\Fonts\MyFont.ttf" --padding 15
UnityFontReplacer_EN.exe makesdf --ttf "D:\Fonts\MyFont.ttf" --rendermode raster
UnityFontReplacer_EN.exe makesdf --ttf "D:\Fonts\MyFont.ttf" --filter-mode point
```

### Options

| Option | Description | Default |
|--------|-------------|---------|
| `--ttf` | input TTF/OTF | required |
| `--atlas-size` | atlas size (`W,H`) | `4096,4096` |
| `--point-size` | point size (`0` = auto) | `0` |
| `--padding` | atlas padding | `7` |
| `--charset` | charset file or literal string | `./CharList_3911.txt` |
| `--rendermode` | `sdf` / `raster` | `sdf` |
| `--filter-mode` | `auto` / `point` / `bilinear` / `trilinear` | `auto` |

### Behavior

- Default `sdf` generation uses an internal SDFAA-style path for normal text fonts.
- `raster` is intended for pixel fonts and non-SDF atlas output.
- With `--filter-mode auto`, saved texture filtering becomes `bilinear` for `sdf` and `point` for `raster`.
- Generated files are written into `ASSETS/` under the current working directory.

## `diag`

```bat
UnityFontReplacer_EN.exe diag --file "D:\Games\MyGame\MyGame_Data\resources.assets"
```

### Options

| Option | Description |
|--------|-------------|
| `--file <file>` | Bundle or assets file to inspect |

`diag` is mainly for troubleshooting rather than normal end-user use.

## Adding Custom Fonts

Put these files into `ASSETS/` or another font folder:

| File | Purpose |
|------|---------|
| `FontName.ttf` or `FontName.otf` | TTF replacement |
| `FontName SDF.json` | SDF data |
| `FontName SDF Atlas.png` | SDF atlas |
| `FontName SDF Material.json` | SDF material |

## Build from Source

Requirements:

- .NET 8 SDK
- initialized Git submodules

```bat
git submodule update --init --recursive
dotnet build .\Unity_Font_Replacer_AT\UnityFontReplacer.csproj -c Release
dotnet msbuild .\Unity_Font_Replacer_AT\UnityFontReplacer.csproj /t:PublishLocalizedVariants /p:Configuration=Release /p:VariantPublishDir="%CD%\publish\"
```

Generated files:

- `publish\UnityFontReplacer_KO.exe`
- `publish\UnityFontReplacer_EN.exe`
- `publish\CharList_3911.txt`
- `publish\LICENSE`
- `publish\ASSETS\`
- `publish\Il2CppDumper\`

## GitHub Release

`.github/workflows/release.yml` creates two ZIP files from manual workflow dispatch:

- `Unity_Font_Replacer_AT_KO_vX.Y.Z.zip`
- `Unity_Font_Replacer_AT_EN_vX.Y.Z.zip`

The workflow does not ship `classdata.tpk`.  
Users download it automatically when the executable runs.

## Notes

- Back up game files before modifying them.
- Some games restore modified files through integrity checks.
- `AssetsTools.NET` is included as a submodule and should remain vendor-owned.
- For Il2Cpp games without `Managed`, `GameAssembly.dll`, `global-metadata.dat`, and `Il2CppDumper` are required.

## License

The original code written for this repository is released under the [MIT License](LICENSE).  
However, third-party components such as submodules, bundled external tools, auto-downloaded files, fonts, and game data remain under their own licenses and rights, and are not relicensed by this repository's `LICENSE`.

## Special Thanks

- The original [Unity_Font_Replacer](https://github.com/snowyegret23/Unity_Font_Replacer) project
- [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) for the Unity asset read/write foundation
- [AssetRipper/Tpk](https://github.com/AssetRipper/Tpk) for providing `classdata.tpk`
- [Perfare/Il2CppDumper](https://github.com/Perfare/Il2CppDumper) for Il2Cpp dummy `Managed` generation
- `System.CommandLine`, `Spectre.Console`, `SixLabors.ImageSharp`, `SixLabors.Fonts`, and `SixLabors.ImageSharp.Drawing` for CLI, rendering, and font-processing support

## Disclaimer

- This project is an unofficial independent tool and is not affiliated with, endorsed by, or sponsored by Unity Technologies, TextMeshPro, any game developer/publisher, or any font vendor.
- `Unity`, `TextMeshPro`, and all game/font names remain the property of their respective owners.
- This software is provided `as is` under the `LICENSE`, without express or implied warranties, and users assume responsibility for its use.
- Users are responsible for checking and complying with each game's terms, font licenses, copyright rules, and applicable laws.
