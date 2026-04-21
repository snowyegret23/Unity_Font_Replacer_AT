[Korean README](README.md)

# Unity Font Replacer AT

`Unity_Font_Replacer_AT` is the C# / `AssetsTools.NET` port of `Unity_Font_Replacer`.  
It scans, replaces, exports, and generates Unity TTF and TextMeshPro (TMP) SDF fonts.

Unlike the original Python release layout, the C# port exposes its features through one CLI, with two localized executables instead of a runtime language switch.

## Features

- Replace Unity `Font` TTF assets
- Replace TMP `MonoBehaviour`, atlas, and material data
- JSON-based `parse` + `list` workflow
- Export TMP font assets with `export`
- Generate TMP-compatible SDF data with `makesdf`
- Optional PS5 swizzle handling
- Built-in `Mulmaru` and `NanumGothic` presets

## Release Layout

Release ZIPs typically look like this:

```text
release/
├── UnityFontReplacer_KO.exe
├── KR_ASSETS/
└── README.md

release_en/
├── UnityFontReplacer_EN.exe
├── KR_ASSETS/
└── README_EN.md
```

- `UnityFontReplacer_KO.exe`: Korean UI
- `UnityFontReplacer_EN.exe`: English UI
- `KR_ASSETS/`: bundled replacement fonts and SDF resources

`classdata.tpk` is not included in releases.  
If it is missing, the tool downloads it on first run.

## Quick Start

### Bulk replacement

```bat
UnityFontReplacer_KO.exe batch --gamepath "D:\Games\MyGame" --font mulmaru
UnityFontReplacer_EN.exe batch --gamepath "D:\Games\MyGame" --font nanumgothic --sdfonly
```

### Generate font mapping JSON

```bat
UnityFontReplacer_EN.exe parse --gamepath "D:\Games\MyGame"
```

This writes a file such as `MyGame.json` next to the executable.

### Replace from JSON mapping

```bat
UnityFontReplacer_EN.exe list --gamepath "D:\Games\MyGame" --file ".\MyGame.json"
```

### Export TMP fonts

```bat
UnityFontReplacer_EN.exe export --gamepath "D:\Games\MyGame"
```

Exported files go into `exported_fonts/` next to the executable.

### Generate SDF data

```bat
UnityFontReplacer_EN.exe makesdf --ttf ".\MyFont.ttf"
```

Output files are written to the current working directory.

## Command Summary

| Command | Description |
|---------|-------------|
| `batch` | Bulk replacement using a built-in font or a custom font folder |
| `parse` | Save detected game font information as JSON |
| `list` | Replace selected fonts from a JSON mapping |
| `export` | Export TMP font data into `exported_fonts/` |
| `makesdf` | Generate TMP-compatible JSON/atlas data from TTF |
| `diag` | Bundle / assets diagnostic helper |

## `batch`

```bat
UnityFontReplacer_EN.exe batch --gamepath "D:\Games\MyGame" --font mulmaru
UnityFontReplacer_EN.exe batch --gamepath "D:\Games\MyGame" --font nanumgothic --ttfonly
UnityFontReplacer_EN.exe batch --gamepath "D:\Games\MyGame" --font ".\MyFontPack" --output-only "D:\output"
UnityFontReplacer_EN.exe batch --gamepath "D:\Games\MyGame" --font mulmaru --ps5-swizzle
```

### Important options

| Option | Description |
|--------|-------------|
| `--gamepath`, `-g` | Game root or `_Data` / `Data` directory |
| `--font`, `-f` | `mulmaru`, `nanumgothic`, a font folder, `.ttf`, `.otf`, or `.json` |
| `--sdfonly` | Replace TMP SDF only |
| `--ttfonly` | Replace TTF only |
| `--output-only <dir>` | Write modified files to another folder instead of in-place |
| `--ps5-swizzle` | Enable PS5 atlas swizzle handling |

### Accepted `--font` forms

- `mulmaru`, `nanumgothic`: built-in assets
- directory path: uses JSON/PNG/TTF files from that folder
- `.ttf` / `.otf`: TTF source for `Font` replacement
- `.json`: TMP SDF source

Built-in presets automatically choose the closest atlas padding variant from `Padding_5`, `Padding_7`, and `Padding_15`.

## `parse` + `list` workflow

1. Run `parse` to generate JSON.
2. Fill `Replace_to` for entries you want to replace.
3. Run `list` with that JSON file.

### Example

```bat
UnityFontReplacer_EN.exe parse --gamepath "D:\Games\MyGame"
UnityFontReplacer_EN.exe list --gamepath "D:\Games\MyGame" --file ".\MyGame.json"
```

### JSON example

```json
{
  "game_path": "D:\\Games\\MyGame",
  "unity_version": "2021.3.16f1",
  "fonts": {
    "resources.assets|resources.assets|NotoSansKR-Medium SDF|SDF|1827": {
      "File": "resources.assets",
      "assets_name": "resources.assets",
      "Path_ID": 1827,
      "Type": "SDF",
      "Name": "NotoSansKR-Medium SDF",
      "Replace_to": "Mulmaru",
      "schema": "New",
      "glyph_count": 11172,
      "atlas_path_id": 1828,
      "atlas_padding": 7
    }
  }
}
```

If `Replace_to` is empty, that entry is skipped.

## `export`

```bat
UnityFontReplacer_EN.exe export --gamepath "D:\Games\MyGame"
```

Output layout:

```text
exported_fonts/
├── Some Font SDF.json
├── Some Font SDF Atlas.png
└── Some Font SDF Material.json
```

## `makesdf`

```bat
UnityFontReplacer_EN.exe makesdf --ttf ".\Mulmaru.ttf"
UnityFontReplacer_EN.exe makesdf --ttf ".\Mulmaru.ttf" --padding 15
UnityFontReplacer_EN.exe makesdf --ttf ".\Mulmaru.ttf" --charset ".\charset.txt"
UnityFontReplacer_EN.exe makesdf --ttf ".\Mulmaru.ttf" --rendermode raster
```

| Option | Description | Default |
|--------|-------------|---------|
| `--ttf` | input TTF/OTF | required |
| `--atlas-size` | atlas size (`W,H`) | `4096,4096` |
| `--point-size` | point size (`0` = auto) | `0` |
| `--padding` | atlas padding | `7` |
| `--charset` | charset file or literal string | empty |
| `--rendermode` | `sdf` or `raster` | `sdf` |

## Adding custom fonts

Place these files under `KR_ASSETS/` or another font folder:

| File | Purpose |
|------|---------|
| `FontName.ttf` or `FontName.otf` | TTF replacement |
| `FontName SDF.json` | TMP font data |
| `FontName SDF Atlas.png` | TMP atlas |
| `FontName SDF Material.json` | optional TMP material |

## Build from source

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
- `publish\KR_ASSETS\`

## GitHub Release

`.github/workflows/release.yml` builds two ZIPs from a manual workflow dispatch:

- `Unity_Font_Replacer_AT_KO_vX.Y.Z.zip`
- `Unity_Font_Replacer_AT_EN_vX.Y.Z.zip`

The workflow does not ship `classdata.tpk`.  
Users download it automatically when the executable starts.

## Notes

- Back up game files before editing them.
- Some games restore modified files through integrity checks.
- `diag` is intended for troubleshooting rather than regular end users.
- `AssetsTools.NET` is included as a Git submodule and should remain vendor-owned.
