[English README](README_EN.md)

# Unity Font Replacer AT

`Unity_Font_Replacer_AT`는 `Unity_Font_Replacer`의 C# / `AssetsTools.NET` 포트입니다.  
Unity 게임의 TTF 폰트와 SDF 폰트를 스캔, 교체, 추출, 생성합니다.


## Python판과의 호환 범위

- Python판의 평면 JSON과 `fonts` 래퍼 형식을 모두 읽습니다. 교체 대상은 파일명, 내부 에셋명, Path ID, 타입을 함께 확인합니다. 네 값이 같은 에셋이 여러 폴더에 있으면 모두 교체합니다.
- `list --output-only <경로>`로 원본 게임 데이터 밖에 저장할 수 있습니다. 임시 저장본을 다시 열어 바뀐 오브젝트의 해시를 확인한 후 설치하고, 교체 작업에서 예외가 나면 이번 작업에서 이미 설치한 파일을 복구합니다. 강제 종료나 전원 손실까지 보장하는 기능은 아닙니다.
- `parse --max-workers <수>`는 실제로 최대 작업자 수를 제한하며, JSON 결과는 입력 파일 순서를 유지합니다.
- UnityFS v9는 디스크에 임시 데이터를 쓰는 방식으로 읽고 저장하며, 출력 형식도 v9로 유지합니다. 지원하지 않는 암호화 플래그와 v10 이상은 거부합니다.
- SDF 교체 전에 공유 아틀라스의 소유자를 확인합니다. Dynamic/DynamicOS 폰트는 `--freeze-dynamic`을 지정해야 정적 아틀라스로 바뀝니다. 텍스처 이름만으로 별도 아틀라스를 찾아 수정하지 않습니다.
- 지원되는 로컬 Standalone Addressables의 압축 JSON 카탈로그를 번들과 함께 검증하고, 실패하면 함께 원복합니다. 배포용 원격 카탈로그, UnityWebRequest 방식, 다른 플랫폼 형식은 대상이 아닙니다.
- `list --keep-original-fallback`은 단일 아틀라스가 파일 안에 들어 있는 지원되는 standalone 정적 TMP 하나의 원본을 폴백으로 보존합니다. 번들 안의 원본 복제, 동적 TTF 우선순위, 외부·스트리밍 의존성과 기존 폴백 체인은 여전히 Python판 범위입니다.
- PS5는 `list` 매핑의 `"process_swizzle": true`로 명시한 단일 밉 Alpha8 아틀라스만 지원합니다. 입력 PNG는 선형 8비트 회색조/회색조+알파/RGBA이어야 합니다. 자동 감지와 BC 스위즐은 Python판을 사용하세요.

소스 빌드와 릴리스 워크플로에는 .NET 10 SDK가 필요합니다. AssetsTools.NET은 업스트림 `3ef7117`을 사용합니다. 릴리스 Windows x64 실행 파일은 자체 포함 빌드이므로 별도의 .NET 런타임을 설치할 필요가 없습니다.

## 한눈에 보기

- TTF `Font` 교체
- SDF `MonoBehaviour` / atlas / material 교체
- `parse -> JSON 수정 -> list` 방식의 개별 교체
- `batch` / `oneshot` 기반 일괄 교체
- `makesdf`로 TTF에서 SDF/Raster 세트 생성
- `Managed` 폴더가 없는 Il2Cpp 게임에서 더미 DLL 자동 생성
- `classdata.tpk` 자동 다운로드

## 릴리즈 구성

릴리즈 ZIP에는 보통 아래 파일이 들어 있습니다.

```text
release/
├── UnityFontReplacer_KO.exe
├── CharList_3911.txt
├── ASSETS/
├── Il2CppDumper/
├── LICENSE
└── README.md
```

- `UnityFontReplacer_KO.exe`: 한국어 UI
- `UnityFontReplacer_EN.exe`: 영어 UI
- `CharList_3911.txt`: 기본 문자셋
- `ASSETS/`: 내장 폰트와 생성/교체용 자산 폴더
- `Il2CppDumper/`: Il2Cpp 더미 `Managed` 생성 도구
- `LICENSE`: 이 저장소의 직접 작성 코드에 대한 라이선스

`classdata.tpk`는 릴리즈에 포함하지 않습니다.  
없으면 프로그램이 실행 중 자동 다운로드합니다.

## 상황별 예시

### 폰트 한 개만 골라서 바꾸고 싶을 때

가장 쉬운 방법은 `parse -> JSON 수정 -> list` 순서입니다.

1. 교체할 TTF로 SDF 폰트를 생성합니다.

```bat
UnityFontReplacer_KO.exe makesdf --ttf "D:\Fonts\Galmuri14.ttf"
```

2. 생성된 파일을 `ASSETS/`에 둡니다.

- `makesdf`를 실행 파일 폴더에서 실행했다면 이미 `./ASSETS/`에 생성되므로 그대로 쓰면 됩니다.
- 다른 폴더에서 실행했다면 생성된 `Galmuri14 SDF.json`, `Galmuri14 SDF Atlas.png`, `Galmuri14 SDF Material.json`을 실행 파일 폴더의 `ASSETS/`로 옮기면 됩니다.

3. 게임 폰트 목록 JSON을 생성합니다.

```bat
UnityFontReplacer_KO.exe parse --gamepath "D:\Games\MyGame"
```

4. 생성된 JSON에서 원하는 폰트 항목의 `Replace_to`를 수정합니다.

```json
"Replace_to": "Galmuri14 SDF.json"
```

5. 수정한 JSON으로 교체를 적용합니다.

```bat
UnityFontReplacer_KO.exe list --gamepath "D:\Games\MyGame" --file "D:\Work\MyGame_fontmap.json"
```

프로그래밍을 잘 몰라도 이 순서대로 하면 됩니다.  
가장 헷갈리기 쉬운 부분은 `Replace_to`에 SDF JSON 파일명을 넣는 점입니다.

### 게임 폰트를 전부 `Mulmaru`로 바꾸고 싶을 때

```bat
UnityFontReplacer_KO.exe batch --gamepath "D:\Games\MyGame" --font mulmaru
```

- `mulmaru`는 내장 preset입니다.
- SDF 대상은 게임 원본 `atlas padding` 값마다 자동으로 임시 Raster 세트를 생성해서 교체합니다.
- TTF 대상은 내장 `Mulmaru.ttf`로 바뀝니다.

### 내가 가진 TTF 하나로 게임 폰트를 전부 바꾸고 싶을 때

```bat
UnityFontReplacer_KO.exe oneshot --gamepath "D:\Games\MyGame" --font "D:\Fonts\NanumMyongjo.ttf"
```

- TTF 대상은 입력한 TTF 그대로 교체됩니다.
- SDF 대상은 게임 원본 `atlas padding` 값마다 임시 SDF 세트를 자동 생성해서 교체됩니다.

### 픽셀 폰트를 Raster 방식으로 강제하고 싶을 때

`--force-raster`를 쓰면 됩니다. `--raster`는 같은 의미의 alias입니다.

```bat
UnityFontReplacer_KO.exe oneshot --gamepath "D:\Games\MyGame" --font "D:\Fonts\MyPixelFont.ttf" --force-raster
UnityFontReplacer_KO.exe batch --gamepath "D:\Games\MyGame" --font "D:\Fonts\MyPixelFont.ttf" --force-raster
```

- Raster 모드에서는 텍스처 `FilterMode`가 기본적으로 `Point(0)`로 저장됩니다.
- Material도 Python 원본처럼 raster-safe 최소 구성으로 재구성됩니다.

## 명령어 요약

| 명령 | 설명 |
|------|------|
| `batch` | 내장 preset 또는 사용자 폰트 소스로 전체 교체 |
| `oneshot` | 단일 TTF로 TTF/SDF 전체 교체 |
| `parse` | 게임 폰트 목록 JSON 생성 |
| `list` | JSON 기준 개별 교체 |
| `export` | 게임의 SDF 폰트 추출 |
| `makesdf` | TTF에서 SDF/Raster 세트 생성 |
| `diag` | 디버깅용 번들/에셋 진단 |

## `batch`

```bat
UnityFontReplacer_KO.exe batch --gamepath "D:\Games\MyGame" --font mulmaru
UnityFontReplacer_KO.exe batch --gamepath "D:\Games\MyGame" --font nanumgothic --sdfonly
UnityFontReplacer_KO.exe batch --gamepath "D:\Games\MyGame" --font "D:\Fonts\MyFont.ttf"
UnityFontReplacer_KO.exe batch --gamepath "D:\Games\MyGame" --font "D:\Fonts\MyPixelFont.ttf" --force-raster
```

### 옵션

| 옵션 | 설명 |
|------|------|
| `--gamepath`, `-g` | 게임 루트 또는 `_Data` / `Data` 폴더 경로 |
| `--font`, `-f` | `mulmaru`, `nanumgothic`, 폰트 폴더, `.ttf`, `.otf`, `.json` |
| `--sdfonly` | SDF만 교체 |
| `--ttfonly` | TTF만 교체 |
| `--force-raster`, `--raster` | 모든 SDF 교체를 Raster 기준으로 강제 |
| `--output-only <dir>` | 원본 대신 수정본만 별도 폴더에 저장 |
| `--ps5-swizzle` | AT 미지원. Python판 사용 필요 |

### `--font` 입력 규칙

- `mulmaru`: 내장 preset, 기본 SDF 모드는 `raster`
- `nanumgothic`: 내장 preset, 기본 SDF 모드는 `sdf`
- 폴더 경로: 폴더 안의 TTF/OTF가 있으면 임시 생성, 없으면 JSON/PNG 자산 사용
- `.ttf` / `.otf`: TTF 교체 + SDF 자동 생성 소스
- `.json`: 미리 만들어 둔 SDF 세트

`batch`는 SDF 대상에 대해 `oneshot`처럼 게임 원본 SDF 폰트의 `atlas padding` 값마다 임시 세트를 생성해서 교체합니다.

## `oneshot`

```bat
UnityFontReplacer_KO.exe oneshot --gamepath "D:\Games\MyGame" --font "D:\Fonts\NanumMyongjo.ttf"
UnityFontReplacer_KO.exe oneshot --gamepath "D:\Games\MyGame" --font "D:\Fonts\NanumMyongjo.ttf" --sdfonly
UnityFontReplacer_KO.exe oneshot --gamepath "D:\Games\MyGame" --font "D:\Fonts\MyPixelFont.ttf" --force-raster
UnityFontReplacer_KO.exe oneshot --gamepath "D:\Games\MyGame" --font "D:\Fonts\MyFont.ttf" --atlas-size 2048,2048 --point-size 48
```

### 옵션

| 옵션 | 설명 |
|------|------|
| `--gamepath`, `-g` | 게임 루트 또는 `_Data` / `Data` 폴더 경로 |
| `--font`, `-f` | 입력 TTF/OTF 경로 또는 해석 가능한 폰트 이름 |
| `--sdfonly` | SDF만 교체 |
| `--ttfonly` | TTF만 교체 |
| `--force-raster`, `--raster` | SDF 교체용 임시 atlas를 Raster로 생성 |
| `--sdf` | SDF 교체용 임시 atlas를 SDF로 생성 |
| `--atlas-size <W,H>` | 임시 atlas 크기 |
| `--point-size <n>` | 임시 생성 point size (`0`이면 자동) |
| `--charset <file-or-text>` | 임시 생성 문자셋 |
| `--filter-mode <mode>` | `auto` / `point` / `bilinear` / `trilinear` |
| `--output-only <dir>` | 원본 대신 수정본만 별도 폴더에 저장 |
| `--ps5-swizzle` | AT 미지원. Python판 사용 필요 |

### 동작 규칙

- 입력한 TTF는 일반 `Font` 교체에 그대로 사용됩니다.
- SDF 대상은 원본 게임의 `atlas padding` 값을 유지한 채 자동 생성됩니다.
- 기본 생성 모드는 `sdf`입니다.
- 기본 문자셋은 `CharList_3911.txt`입니다.
- 기본 필터 모드는 `auto`입니다.
  `auto`는 `sdf`일 때 `bilinear`, `--force-raster/--raster`일 때 `point`를 선택합니다.
- `--force-raster`와 `--sdf`는 함께 사용할 수 없습니다.
- `oneshot`은 원본 padding을 유지해야 하므로 `--padding` 옵션이 없습니다.

## `parse`

```bat
UnityFontReplacer_KO.exe parse --gamepath "D:\Games\MyGame"
UnityFontReplacer_KO.exe parse --gamepath "D:\Games\MyGame" --max-workers 2
```

### 옵션

| 옵션 | 설명 |
|------|------|
| `--gamepath`, `-g` | 게임 루트 또는 `_Data` / `Data` 폴더 경로 |
| `--ps5-swizzle` | AT 미지원. Python판 사용 필요 |
| `--max-workers <n>` | 병렬 스캔 worker 수 |

### 결과

- 실행 위치 기준으로 `GameName.json` 같은 매핑 파일이 생성됩니다.
- 각 SDF 항목에는 `atlas_padding`, `atlas_path_id`, `force_raster` 등이 포함됩니다.

## `list`

```bat
UnityFontReplacer_KO.exe list --gamepath "D:\Games\MyGame" --file "D:\Work\MyGame_fontmap.json"
```

### 옵션

| 옵션 | 설명 |
|------|------|
| `--gamepath`, `-g` | 게임 루트 또는 `_Data` / `Data` 폴더 경로 |
| `--file <file>` | `parse`로 만든 JSON 경로 |
| `--output-only <dir>` | 게임 데이터 밖에 수정본 저장 |
| `--freeze-dynamic` | 동적 TMP를 정적 아틀라스로 전환 |
| `--keep-original-fallback` | 지원되는 standalone 정적 TMP 하나를 원본 폴백으로 보존 |

### `Replace_to` 규칙

SDF 항목의 `Replace_to`에는 아래 둘 다 넣을 수 있습니다.

- `Mulmaru`, `NanumGothic`, `MyFont SDF.json` 같은 SDF 자산
- `D:\Fonts\MyFont.ttf`, `D:\Fonts\MyFont.otf` 같은 TTF/OTF

TTF/OTF를 넣으면 `list`가 `oneshot`처럼 해당 게임 원본 SDF 폰트의 `atlas padding` 값을 사용해 임시 세트를 자동 생성한 뒤 교체합니다.

## `export`

```bat
UnityFontReplacer_KO.exe export --gamepath "D:\Games\MyGame"
```

### 옵션

| 옵션 | 설명 |
|------|------|
| `--gamepath`, `-g` | 게임 루트 또는 `_Data` / `Data` 폴더 경로 |

### 결과

```text
exported_fonts/
├── Some Font SDF.json
├── Some Font SDF Atlas.png
└── Some Font SDF Material.json
```

## `makesdf`

```bat
UnityFontReplacer_KO.exe makesdf --ttf "D:\Fonts\MyFont.ttf"
UnityFontReplacer_KO.exe makesdf --ttf "D:\Fonts\MyFont.ttf" --padding 15
UnityFontReplacer_KO.exe makesdf --ttf "D:\Fonts\MyFont.ttf" --rendermode raster
UnityFontReplacer_KO.exe makesdf --ttf "D:\Fonts\MyFont.ttf" --filter-mode point
```

### 옵션

| 옵션 | 설명 | 기본값 |
|------|------|--------|
| `--ttf` | 입력 TTF/OTF | 필수 |
| `--atlas-size` | atlas 크기 (`W,H`) | `4096,4096` |
| `--point-size` | point size (`0`이면 자동) | `0` |
| `--padding` | atlas padding | `7` |
| `--charset` | charset 파일 또는 문자열 | `./CharList_3911.txt` |
| `--rendermode` | `sdf` / `raster` | `sdf` |
| `--filter-mode` | `auto` / `point` / `bilinear` / `trilinear` | `auto` |

### 동작 규칙

- 기본 `sdf` 생성은 일반 텍스트용 SDFAA 계열 경로를 사용합니다.
- `raster`는 픽셀 폰트/비-SDF atlas용입니다.
- `--filter-mode auto`일 때 `sdf`는 `bilinear`, `raster`는 `point`로 저장됩니다.
- 결과 파일은 현재 작업 폴더의 `ASSETS/` 아래에 생성됩니다.

## `diag`

```bat
UnityFontReplacer_KO.exe diag --file "D:\Games\MyGame\MyGame_Data\resources.assets"
```

### 옵션

| 옵션 | 설명 |
|------|------|
| `--file <file>` | 진단할 bundle 또는 assets 파일 경로 |

`diag`는 최종 사용자용 기능보다는 문제 분석용입니다.

## 커스텀 폰트 추가

`ASSETS/` 또는 사용자 폰트 폴더에 아래 파일을 두면 됩니다.

| 파일 | 용도 |
|------|------|
| `FontName.ttf` 또는 `FontName.otf` | TTF 교체용 |
| `FontName SDF.json` | SDF 데이터 |
| `FontName SDF Atlas.png` | SDF atlas |
| `FontName SDF Material.json` | SDF material |

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
- `publish\LICENSE`
- `publish\ASSETS\`
- `publish\Il2CppDumper\`

## GitHub Release

`.github/workflows/release.yml`은 수동 실행 기준으로 KO/EN ZIP 두 개를 만듭니다.

- `Unity_Font_Replacer_AT_KO_vX.Y.Z.zip`
- `Unity_Font_Replacer_AT_EN_vX.Y.Z.zip`

워크플로는 `classdata.tpk`를 포함하지 않습니다.  
사용자가 프로그램을 실행할 때 자동 다운로드됩니다.

## 주의 사항

- 게임 파일 수정 전 백업을 권장합니다.
- 일부 게임은 무결성 검사로 수정 파일을 복구합니다.
- `AssetsTools.NET`은 서브모듈로 포함되며 이 저장소에서 직접 수정하지 않는 것을 전제로 합니다.
- `Managed`가 없는 Il2Cpp 게임에서는 `GameAssembly.dll`, `global-metadata.dat`, `Il2CppDumper`가 필요합니다.

## License

이 저장소의 직접 작성 코드에는 [MIT License](LICENSE)가 적용됩니다.  
단, 서브모듈, 번들된 외부 도구, 자동 다운로드되는 파일, 폰트/게임 데이터 등 제3자 구성요소에는 각자의 라이선스와 권리 조건이 그대로 적용되며, 이 저장소의 `LICENSE`로 재라이선스되지 않습니다.

## Special Thanks

- 원본 프로젝트 [Unity_Font_Replacer](https://github.com/snowyegret23/Unity_Font_Replacer)
- Unity 에셋 읽기/쓰기 기반을 제공하는 [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET)
- `classdata.tpk`를 제공하는 [AssetRipper/Tpk](https://github.com/AssetRipper/Tpk)
- Il2Cpp 더미 `Managed` 생성을 위해 사용하는 [Perfare/Il2CppDumper](https://github.com/Perfare/Il2CppDumper)
- CLI/이미지/폰트 처리에 사용한 `System.CommandLine`, `Spectre.Console`, `SixLabors.ImageSharp`, `FreeTypeSharp`

## 면책

- 이 프로젝트는 비공식 독립 도구이며 Unity Technologies, TextMeshPro, 개별 게임 개발사/배급사, 폰트 제작사와 관계가 없고 승인이나 후원을 받지 않습니다.
- `Unity`, `TextMeshPro` 및 각 게임/폰트 명칭은 각 권리자의 자산입니다.
- 이 소프트웨어는 `LICENSE`에 따라 `as is`로 제공되며, 명시적 또는 묵시적 보증 없이 사용자가 책임지고 사용해야 합니다.
- 사용자는 각 게임의 이용 약관, 폰트 라이선스, 저작권 및 관련 법규를 직접 확인하고 준수해야 합니다.
