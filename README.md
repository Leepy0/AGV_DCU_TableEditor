# AGV DCU 표 편집기

AGV DCU 펌웨어의 사이트 표(프로파일, 태그, 시퀀스, 수동 표)를 코딩 없이 고치는 Windows 프로그램.

- 사이트 C 표 파일을 읽어 블록으로 보이고, 고친 줄만 같은 C 파일로 다시 쓴다.
- 저장 전에 표를 검사한다(가려진 줄, 종류가 다른 이름, 닫히지 않은 사슬).
- 압축 태그 표 생성, C28x 빌드, 커밋, 회차 내기를 한 화면에서 한다.

![태그 화면](docs/shot/tag.png)

## 받기

[Releases](https://github.com/Leepy0/AGV_DCU_TableEditor/releases) 의 `TableEditor-vX.Y.Z-win-x64.zip` 을 풀어 `TableEditor.exe` 를 실행한다. 설치가 없다. 폴더에 `tedit.exe`(명령줄), `gcc\`(휴대용 컴파일러), `VERSION` 이 같이 있다.

## v0.2.0 변경

- 자동 업데이트: 시작·1시간마다 새 버전 확인, 팝업·토스트, 설정에서 켜고 끔
- 시퀀스 검색·편집: 태그·시퀀스 거르기, 스텝 추가·바꾸기·지우기·끌기, 표 추가·지우기
- 칸 편집: 표 안에서 바로(F2·Enter·두 번 누르기)
- 동작 칩, ON/OFF 칸, TAPE 꼴 고르기, 프로파일 구분
- 장애물 감지 켬/끔 문구
- **v0.1.0 사용자**: 자동 업데이트가 없다. v0.2.0 을 Releases 에서 손으로 한 번 받아 바꿔 넣는다. 이후는 자동.

## 필요한 것

| 필요 | 용도 |
|---|---|
| 앱 저장소 clone | 편집할 사이트 표가 있는 저장소 |
| Git for Windows | 받기, 올리기, 이력, 되돌리기 |
| TI C2000 CGT 18.12.2.LTS | C28x 이미지 빌드 |
| PowerShell | 빌드 보조 |

압축 태그 표 생성용 gcc 는 zip 에 들어 있다. 따로 설치하지 않는다.

## 명령줄 `tedit`

| 명령 | 하는 일 |
|---|---|
| `tedit roundtrip <사이트 폴더>` | 읽고 그대로 쓴 바이트를 견줌 |
| `tedit stat <사이트 폴더>` | 문장·구역 개수 |
| `tedit headers <core 폴더>` | 문법 표와 코어 헤더 견줌 |
| `tedit check <사이트 폴더> [--core <core 폴더>]` | 표 검사 |
| `tedit pack <앱 루트> <사이트> [--gcc <경로>]` | 압축 태그·시퀀스 표 생성 |
| `tedit build <앱 루트> <사이트> [--field] [--name <이미지 이름>] [--gcc <경로>]` | 이미지 빌드 |
| `tedit round <앱 루트> <사이트> --app-url <주소> [--gcc <경로>]` | 회차 내기 |
| `tedit core-update <앱 루트> <사이트> <vX.Y.Z> --app-url <주소> [--from <주소>] [--gcc <경로>]` | 코어 올리기 |
| `tedit core-versions <앱 루트> [--core-url <주소>]` | 코어 버전 목록 |
| `tedit git-log-check <git.log> [--core-url <주소>]` | 깃 기록 검사 |

`--gcc` 를 주지 않으면 실행 파일 폴더의 `gcc\bin\gcc.exe` 를 쓴다.

## 만드는 법

| 하는 일 | 명령 |
|---|---|
| 개발 빌드(패키지 0개) | `dotnet build` |
| 배포 묶음·zip | `tools\release.ps1 -Version vX.Y.Z [-Gcc <mingw64 폴더>] [-Out publish] [-AppRepo <앱 저장소> -Site <사이트 폴더 이름>]` |

`release.ps1` 은 빌드·시험, 게시, 휴대용 gcc 추리기, 묶음 시험, zip·SHA-256 까지 한다. 결과는 `publish\` 에 나오고 커밋하지 않는다. 앱 저장소를 주면 그 사본에서 `pack`·`build` 까지 시험한다.

## 문서

| 문서 | 내용 |
|---|---|
| [docs/01_설계서.md](docs/01_설계서.md) | 구성, 엔진, 화면, 배포, 단계, 확인 요청 |
| [docs/02_구현계획.md](docs/02_구현계획.md) | 작업 순서와 공통 규칙 |

문법과 엔진은 공개 저장소 [AGV_DCU_Core](https://github.com/Leepy0/AGV_DCU_Core) 의 표 매크로를 따른다.

## 같이 넣은 gcc

zip 의 `gcc\` 는 WinLibs(MinGW-w64) 에서 C 용만 추린 것이다. 라이선스와 출처는 `gcc\licenses\`, `gcc\SOURCE.txt`.
