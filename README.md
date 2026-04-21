[English README](README_EN.md)

# Unity Font Replacer AT

`Unity_Font_Replacer_AT`는 `Unity_Font_Replacer`의 C# / `AssetsTools.NET` 포트입니다.  
Unity 게임의 TTF 폰트와 TextMeshPro(TMP) SDF 폰트를 스캔, 교체, 추출, 생성합니다.

현재 배포판은 Python 원본처럼 실행 파일이 여러 개로 나뉘지 않고, 하나의 CLI에 기능이 통합되어 있습니다.  
언어 선택도 런타임 옵션이 아니라 실행 파일 자체가 분리되어 있습니다.

## 주요 특징

- TTF `Font` 에셋 교체
- TMP `MonoBehaviour` / atlas / material 교체
- `Managed` 폴더가 없는 Il2Cpp 게임에서 더미 DLL 자동 생성
- `parse` + `list` 기반 JSON 매핑 작업
- TMP 폰트 추출 (`export`)
- TTF -> TMP SDF 생성 (`makesdf`)
- PS5 swizzle 처리 옵션
- `Mulmaru`, `NanumGothic` 내장 폰트 지원

## 배포 구성

릴리즈 ZIP은 보통 아래처럼 구성됩니다.

```text
release/
├── UnityFontReplacer_KO.exe
├── CharList_3911.txt
├── KR_ASSETS/
├── Il2CppDumper/
└── README.md

release_en/
├── UnityFontReplacer_EN.exe
├── CharList_3911.txt
├── KR_ASSETS/
├── Il2CppDumper/
└── README_EN.md
```

- `UnityFontReplacer_KO.exe`: 한국어 UI
- `UnityFontReplacer_EN.exe`: 영어 UI
- `CharList_3911.txt`: 기본 문자셋 목록
- `KR_ASSETS/`: 내장 교체 폰트 및 SDF 리소스
- `Il2CppDumper/`: Il2Cpp 게임용 더미 `Managed` 생성 도구

`classdata.tpk`는 릴리즈에 포함하지 않습니다.  
파일이 없으면 프로그램이 첫 실행 시 자동 다운로드를 시도합니다.

`Managed` 폴더가 없는 Il2Cpp 게임은, 포함된 `Il2CppDumper`로 첫 실행 시 더미 DLL을 자동 생성합니다.

## 빠른 시작

### 일괄 교체

```bat
UnityFontReplacer_KO.exe batch --gamepath "D:\Games\MyGame" --font mulmaru
UnityFontReplacer_EN.exe batch --gamepath "D:\Games\MyGame" --font nanumgothic --sdfonly
```

### 폰트 매핑 JSON 생성

```bat
UnityFontReplacer_KO.exe parse --gamepath "D:\Games\MyGame"
```

실행 파일 기준 폴더에 `MyGame.json` 같은 파일이 생성됩니다.

### JSON 기반 개별 교체

```bat
UnityFontReplacer_KO.exe list --gamepath "D:\Games\MyGame" --file ".\MyGame.json"
```

### TMP 폰트 추출

```bat
UnityFontReplacer_KO.exe export --gamepath "D:\Games\MyGame"
```

실행 파일 기준 폴더 아래 `exported_fonts/`에 JSON, atlas PNG, material JSON이 생성됩니다.

### SDF 생성

```bat
UnityFontReplacer_KO.exe makesdf --ttf ".\MyFont.ttf"
```

출력은 현재 작업 디렉터리에 저장됩니다.

## 명령 요약

| 명령 | 설명 |
|------|------|
| `batch` | 내장 폰트 또는 사용자 폰트 폴더로 일괄 교체 |
| `parse` | 게임 폰트 정보를 JSON으로 저장 |
| `list` | JSON 매핑 파일 기준 개별 교체 |
| `export` | TMP 폰트 데이터를 `exported_fonts/`로 추출 |
| `makesdf` | TTF에서 TMP 호환 JSON/atlas 생성 |
| `diag` | 디버깅용 번들/에셋 진단 |

## `batch` 사용법

```bat
UnityFontReplacer_KO.exe batch --gamepath "D:\Games\MyGame" --font mulmaru
UnityFontReplacer_KO.exe batch --gamepath "D:\Games\MyGame" --font nanumgothic --ttfonly
UnityFontReplacer_KO.exe batch --gamepath "D:\Games\MyGame" --font ".\MyFontPack" --output-only "D:\output"
UnityFontReplacer_KO.exe batch --gamepath "D:\Games\MyGame" --font mulmaru --ps5-swizzle
```

### 주요 옵션

| 옵션 | 설명 |
|------|------|
| `--gamepath`, `-g` | 게임 루트 또는 `_Data` / `Data` 폴더 경로 |
| `--font`, `-f` | `mulmaru`, `nanumgothic`, 폰트 폴더, `.ttf`, `.otf`, `.json` |
| `--sdfonly` | TMP SDF만 교체 |
| `--ttfonly` | TTF만 교체 |
| `--output-only <dir>` | 원본 대신 수정본만 별도 폴더에 저장 |
| `--ps5-swizzle` | PS5 atlas swizzle 처리 |

### `--font` 입력 규칙

- `mulmaru`, `nanumgothic`: 내장 리소스 사용
- 폴더 경로: 폴더 안의 JSON/PNG/TTF 조합 사용
- `.ttf` / `.otf`: TTF 교체 소스로 사용
- `.json`: SDF 소스로 사용

내장 폰트는 원본 `atlas padding`에 가장 가까운 `Padding_5`, `Padding_7`, `Padding_15` 프리셋을 자동 선택합니다.

## `parse` + `list` 워크플로

1. `parse`로 JSON 생성
2. JSON의 `Replace_to` 값을 채움
3. `list`로 교체 실행

### 예시

```bat
UnityFontReplacer_KO.exe parse --gamepath "D:\Games\MyGame"
UnityFontReplacer_KO.exe list --gamepath "D:\Games\MyGame" --file ".\MyGame.json"
```

### JSON 예시

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

`Replace_to`를 비워두면 해당 항목은 건너뜁니다.

## `export`

```bat
UnityFontReplacer_KO.exe export --gamepath "D:\Games\MyGame"
```

출력 위치:

```text
exported_fonts/
├── Some Font SDF.json
├── Some Font SDF Atlas.png
└── Some Font SDF Material.json
```

## `makesdf`

```bat
UnityFontReplacer_KO.exe makesdf --ttf ".\Mulmaru.ttf"
UnityFontReplacer_KO.exe makesdf --ttf ".\Mulmaru.ttf" --padding 15
UnityFontReplacer_KO.exe makesdf --ttf ".\Mulmaru.ttf" --charset ".\charset.txt"
UnityFontReplacer_KO.exe makesdf --ttf ".\Mulmaru.ttf" --rendermode raster
```

| 옵션 | 설명 | 기본값 |
|------|------|--------|
| `--ttf` | 입력 TTF/OTF | 필수 |
| `--atlas-size` | atlas 크기 (`W,H`) | `4096,4096` |
| `--point-size` | point size (`0`이면 자동) | `0` |
| `--padding` | atlas padding | `7` |
| `--charset` | charset 파일 또는 문자열 | `./CharList_3911.txt` |
| `--rendermode` | `sdf` 또는 `raster` | `sdf` |

## 커스텀 폰트 추가

`KR_ASSETS/` 또는 사용자 지정 폴더에 아래 파일을 두면 됩니다.

| 파일 | 용도 |
|------|------|
| `FontName.ttf` 또는 `FontName.otf` | TTF 교체 |
| `FontName SDF.json` | TMP 데이터 |
| `FontName SDF Atlas.png` | TMP atlas |
| `FontName SDF Material.json` | TMP material 선택 항목 |

## 소스에서 빌드

요구 사항:

- .NET 8 SDK
- Git submodule 초기화

```bat
git submodule update --init --recursive
dotnet build .\Unity_Font_Replacer_AT\UnityFontReplacer.csproj -c Release
dotnet msbuild .\Unity_Font_Replacer_AT\UnityFontReplacer.csproj /t:PublishLocalizedVariants /p:Configuration=Release /p:VariantPublishDir="%CD%\publish\"
```

생성물:

- `publish\UnityFontReplacer_KO.exe`
- `publish\UnityFontReplacer_EN.exe`
- `publish\CharList_3911.txt`
- `publish\KR_ASSETS\`
- `publish\Il2CppDumper\`

## GitHub Release

`.github/workflows/release.yml`은 수동 실행 기준으로 KO/EN ZIP 두 개를 만듭니다.

- `Unity_Font_Replacer_AT_KO_vX.Y.Z.zip`
- `Unity_Font_Replacer_AT_EN_vX.Y.Z.zip`

워크플로는 `classdata.tpk`를 포함하지 않고 KO/EN ZIP만 만든 뒤 draft release를 생성합니다.  
`classdata.tpk`는 사용자가 프로그램을 실행할 때 자동 다운로드됩니다.

## 주의 사항

- 게임 파일 수정 전 백업을 권장합니다.
- 일부 게임은 무결성 검사로 수정 파일을 복구합니다.
- `diag`는 최종 사용자용 기능보다는 문제 분석용입니다.
- `AssetsTools.NET`은 서브모듈로 포함되며 이 저장소에서 직접 수정하지 않는 것을 전제로 합니다.
- `Managed`가 없는 Il2Cpp 게임에서는 `GameAssembly.dll`, `global-metadata.dat`, `Il2CppDumper`가 필요합니다.
