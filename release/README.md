# Unity Font Replacer AT

이 폴더는 한국어 UI 배포 패키지입니다.

## 구성

```text
release/
├── UnityFontReplacer_KO.exe
├── KR_ASSETS/
└── README.md
```

## 빠른 시작

```bat
UnityFontReplacer_KO.exe batch --gamepath "D:\Games\MyGame" --font mulmaru
```

자주 쓰는 명령:

```bat
:: 모든 폰트를 Mulmaru로 일괄 교체
UnityFontReplacer_KO.exe batch --gamepath "D:\Games\MyGame" --font mulmaru

:: 폰트 매핑 JSON 생성
UnityFontReplacer_KO.exe parse --gamepath "D:\Games\MyGame"

:: JSON 기준 개별 교체
UnityFontReplacer_KO.exe list --gamepath "D:\Games\MyGame" --file ".\MyGame.json"

:: TMP 폰트 추출
UnityFontReplacer_KO.exe export --gamepath "D:\Games\MyGame"

:: TTF에서 SDF 생성
UnityFontReplacer_KO.exe makesdf --ttf ".\MyFont.ttf"
```

## 참고

- `classdata.tpk`는 포함되지 않습니다. 첫 실행 시 자동 다운로드됩니다.
- `KR_ASSETS/`에는 `Mulmaru`, `NanumGothic` 내장 리소스가 들어 있습니다.
- 전체 문서는 저장소 루트의 `README.md`를 참고하세요.
