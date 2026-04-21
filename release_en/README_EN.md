# Unity Font Replacer AT

This folder is the English UI release package.

## Contents

```text
release_en/
├── UnityFontReplacer_EN.exe
├── CharList_3911.txt
├── KR_ASSETS/
├── Il2CppDumper/
└── README_EN.md
```

## Quick Start

```bat
UnityFontReplacer_EN.exe batch --gamepath "D:\Games\MyGame" --font mulmaru
```

Common commands:

```bat
:: Bulk replace all fonts with Mulmaru
UnityFontReplacer_EN.exe batch --gamepath "D:\Games\MyGame" --font mulmaru

:: Generate font mapping JSON
UnityFontReplacer_EN.exe parse --gamepath "D:\Games\MyGame"

:: Replace selected fonts from JSON
UnityFontReplacer_EN.exe list --gamepath "D:\Games\MyGame" --file ".\MyGame.json"

:: Export TMP fonts
UnityFontReplacer_EN.exe export --gamepath "D:\Games\MyGame"

:: Generate SDF data from TTF
UnityFontReplacer_EN.exe makesdf --ttf ".\MyFont.ttf"
```

## Notes

- `classdata.tpk` is not included. It is downloaded automatically on first run.
- `CharList_3911.txt` is the default charset list file.
- `KR_ASSETS/` contains the built-in `Mulmaru` and `NanumGothic` resources.
- `Il2CppDumper/` is used to auto-generate dummy DLLs for Il2Cpp games when `Managed` is missing.
- See the repository root `README_EN.md` for the full guide.
