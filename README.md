# MyAIBoard

Windows 바탕화면에 떠 있는 글래스 스타일 메모 위젯입니다. Claude, Codex 같은 AI가 MCP로 메모를 추가하고 읽을 수 있습니다.
"이거 aiboard에 추가해"라고 말하면 AI가 보드에 메모를 남깁니다.

## 구성

| 프로젝트 | 역할 |
|---|---|
| `src/MyAIBoard.Core` | SQLite 메모 저장소 (`%USERPROFILE%\.myaiboard\board.db`, `MYAIBOARD_DB`로 경로 변경 가능), 해시태그, 내보내기 |
| `src/MyAIBoard.App` | WPF 위젯 (`MyAIBoard.exe`) |
| `src/MyAIBoard.Mcp` | MCP stdio 서버 (`MyAIBoard.Mcp.exe`) |
| `tests/` | Core 테스트, MCP 서버 테스트(도구 단위 + 실제 stdio 접속) |

위젯과 MCP 서버는 같은 DB를 씁니다. 위젯은 DB의 변경 카운터를 1초마다 확인해서 AI가 추가한 메모를 바로 보여줍니다.
DB 구조는 `PRAGMA user_version`으로 버전을 관리하며, 새 버전이 처음 실행될 때 기존 DB를 자동으로 올립니다.

## 설치

Windows 10/11 (x64). [Releases](../../releases)에서 `MyAIBoard-<버전>-win-x64.zip`을 받아 압축을 풀고, 그 폴더에서 실행합니다.
.NET이 함께 들어 있어서 따로 설치할 것은 없습니다.
처음 쓰는 사람을 위한 단계별 안내는 [INSTALL.txt](INSTALL.txt)에 있습니다(zip에도 들어 있음).

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

소스에서 직접 빌드해 설치하려면(.NET 10 SDK 필요) 저장소 루트에서 `scripts/install.ps1`을 같은 방식으로 실행합니다.

`%USERPROFILE%\.myaiboard\bin`에 설치하고, 시작 메뉴 바로가기와 로그인 시 자동 실행을 등록한 뒤 위젯을 띄웁니다.
Claude Code나 Codex가 설치돼 있으면 보드에 연결할지 묻습니다(`-Mcp yes` / `-Mcp no`로 미리 정할 수 있음). Codex를 연결하면 `~/.codex/AGENTS.md`에 aiboard 요청을 이 도구로 처리하라는 짧은 안내도 넣습니다.
보드(`board.db`)와 설정도 `%USERPROFILE%\.myaiboard`에 있습니다. AppData를 쓰지 않는 이유: Codex 앱처럼 스토어(MSIX)로 설치된 앱과 그 앱이 띄운 MCP 서버는 가상화된 AppData만 보기 때문에, AppData에 두면 그쪽에서는 보드가 보이지 않습니다.
예전 버전(`%LOCALAPPDATA%\MyAIBoard`)에서 업데이트하면 보드와 설정을 한 번 복사해 옵니다. 옛 폴더는 확인 후 지워도 됩니다.
다시 실행하면 업데이트됩니다. 자동 실행 설정을 건드리지 않으려면 `-NoAutostart`를 붙이세요(위젯 메뉴에서도 끌 수 있습니다).
AI 세션이 실행 중인 MCP 서버는 그 세션을 다시 시작해야 새 버전으로 바뀝니다.

### 제거

```powershell
powershell -ExecutionPolicy Bypass -File uninstall.ps1
```

위젯, 바로가기, 자동 실행, Claude Code / Codex 연결, `AGENTS.md` 안내를 지웁니다. 보드와 설정(`%USERPROFILE%\.myaiboard`)은 남기며, 함께 지우려면 `-RemoveData`를 붙입니다.

## AI 연결

설치할 때 연결하지 않았거나 CLI를 찾지 못했다면 직접 등록합니다.

Claude Code (`claude` CLI가 없으면 `~/.claude.json`의 `mcpServers`에 같은 경로로 `myaiboard` 항목을 추가):

```powershell
claude mcp add --scope user myaiboard -- "$env:USERPROFILE\.myaiboard\bin\MyAIBoard.Mcp.exe"
```

Codex (CLI가 PATH에 없으면 Codex 앱에 들어 있는 `codex.exe`를 쓰면 됩니다):

```powershell
codex mcp add myaiboard -- "$env:USERPROFILE\.myaiboard\bin\MyAIBoard.Mcp.exe"
```

Codex 앱에서 `MCP startup failed ... (os error 3)`이 나면, MCP 서버 경로가 AppData 안에 있지 않은지 확인하세요.

등록한 뒤 새로 시작한 세션부터 적용됩니다. Codex 앱은 다시 시작하세요.

### MCP 도구

| 도구 | 설명 |
|---|---|
| `add_memo` | 메모 추가. AI 세션의 작업 폴더 이름이 해시태그로 자동으로 붙고(예: `#MyAIBoard`), 작업 폴더와 git 브랜치가 기록됩니다. `remind_at`으로 알림 시각을 함께 정할 수 있습니다. |
| `list_memos` | 보드 순서대로 목록. 기본은 할 일만이고 `status`로 `done`/`all`을 고릅니다. 검색어(`query`)와 해시태그(`tag`)로 거를 수 있습니다. |
| `get_memo`, `update_memo` | 메모 하나 조회, 제목·본문 수정 |
| `complete_memo` | 완료 처리(`completed=false`면 다시 할 일로) |
| `set_reminder` | 알림 설정·변경·해제 |
| `delete_memo` | 휴지통으로 이동 |
| `list_trash`, `restore_memo` | 휴지통 목록, 복원 |

시각은 PC 시간대 기준입니다. `remind_at`은 `2026-11-13T10:00` 같은 ISO 형식(오프셋 없으면 PC 시간대)이나 `내일 10:00`, `+2h` 같은 표현을 받고, 지난 시각은 거절합니다.

AI는 메모를 영구 삭제할 수 없습니다. 영구 삭제는 위젯의 휴지통에서만 할 수 있습니다.

## 사용법

**메모**
- **+** 또는 **Ctrl+Alt+N**(어디서나): 새 메모. Enter는 본문으로 이동, Ctrl+Enter는 저장, Esc는 취소입니다. 카드를 더블클릭하면 편집합니다.
- 편집하는 동안 AI가 같은 메모를 고치거나 지우면 AI의 변경이 우선이고, 편집은 취소되며 알림이 뜹니다.
- 카드에는 작성 시각이 보이고, 고친 메모는 `2일 전 · 수정 5분 전`처럼 수정 시각도 함께 보입니다. 시간에 마우스를 올리면 정확한 시각이 나옵니다.

**완료와 체크리스트**
- 제목 앞 동그라미를 누르면 완료됩니다. 완료한 메모는 보드 아래 "완료" 영역으로 가고(눌러서 펼치기), 5초 동안 되돌릴 수 있습니다. 위쪽 개수는 남은 할 일 수입니다.
- 본문에서 `- [ ] 할 일`로 시작하는 줄은 체크박스가 됩니다. 눌러서 체크하고, 카드 아래에 진행률이 보입니다.

**알림**
- 카드에 마우스를 올리고 시계 버튼: 30분 후, 1시간 후, 오늘 오후 6시, 내일 오전 9시, 다음 주 월요일, 직접 입력(`15:30`, `오후 3시`, `내일 9:00`, `11/20 14:00`, `+2h`, `30분 후`), 알림 해제.
- AI에게 "내일 10시에 알려줘"라고 해도 됩니다.
- 그 시각에 Windows 알림이 뜨고, 누르면 위젯에서 그 메모로 이동합니다. 위젯이 꺼져 있던 동안 지난 알림은 다음에 켤 때 뜹니다. 시각이 지난 알림은 카드에 붉게 표시됩니다.

**이어서 하기**
- 카드의 ▷ 버튼을 누르면 "aiboard 메모 #12 이어서 해줘"로 시작하는 프롬프트가 클립보드에 복사됩니다. 작업 폴더와 브랜치, 메모 내용이 들어 있어서 새 AI 세션에 붙여 넣으면 바로 이어서 할 수 있습니다.

**링크와 해시태그**
- 작은따옴표로 감싼 경로와 웹 주소만 링크가 됩니다: `'https://…'`, `'C:\Program Files\…'`, `'\\서버\공유'`, `'%LOCALAPPDATA%\…'`. 따옴표 덕분에 공백이 있는 경로도 한 링크가 됩니다.
- 링크를 눌러도 아무것도 실행되지 않습니다. 웹 주소는 브라우저, 폴더는 탐색기로 열리고, 파일은 탐색기에서 선택만 됩니다. 경로 뒤의 `:137` 같은 줄 번호는 무시합니다.
- 제목이나 본문의 `#ui`, `#버그` 같은 단어는 해시태그입니다(`#13`, `C#`은 아님). 태그를 누르면 그 태그 메모만 보입니다.

**검색**
- 돋보기 버튼 또는 **Ctrl+F**. 일반 단어는 제목과 본문에서 찾고, `#태그`는 그 태그가 있는 메모만 남깁니다. 여러 개를 넣으면 모두 만족해야 합니다(예: `#MyAIBoard 버그`). Esc로 닫습니다.

**삭제와 휴지통**
- 카드에 마우스를 올리면 편집/삭제 버튼이 나옵니다. 삭제하면 휴지통으로 가고, 5초 동안 되돌리기로 그사이 지운 메모를 모두 되살릴 수 있습니다.
- 메뉴 → 휴지통: 복원, 영구 삭제, 비우기. 휴지통의 메모는 30일이 지나면 자동으로 영구 삭제됩니다.

**메뉴(⋯ 또는 우클릭)**
- 정렬(직접/최신/오래된/제목). 직접 정렬은 드래그로 바꿉니다(검색 중에는 꺼짐).
- 모두 접기/펼치기
- 창 모드(바탕화면 고정/항상 위/일반). 바탕화면 고정 상태에서 단축키나 트레이로 부르면 잠깐 앞으로 나왔다가, 다른 곳을 누르면 다시 내려갑니다.
- 내보내기…: JSON(다시 가져올 수 있는 백업) 또는 Markdown(읽기용). 휴지통은 빼고 내보냅니다.
- 가져오기…: 내보낸 JSON의 메모를 보드 아래에 추가합니다. 이미 있는 메모는 건너뜁니다.
- Windows 시작 시 실행
- 트레이 아이콘: 왼쪽 클릭은 보이기/숨기기, 오른쪽 클릭은 메뉴입니다. Alt+F4는 종료가 아니라 숨기기입니다.

## 설정

`%USERPROFILE%\.myaiboard\settings.json`에 창 위치·크기, 창 모드, 정렬이 저장됩니다.
`NewMemoHotkey`로 새 메모 단축키를 바꿀 수 있습니다(예: `"Ctrl+Shift+Space"`, 빈 값이면 끔). 위젯을 다시 실행하면 적용됩니다.
다른 프로그램이 이미 쓰는 단축키라 등록하지 못하면, 메뉴의 "새 메모" 옆에 "사용 불가"가 표시됩니다.

디자인 값은 `src/MyAIBoard.App/Themes/Glass.xaml`, 아이콘은 `tools/make-icon.ps1`로 다시 만듭니다.

## 개발

```powershell
dotnet test MyAIBoard.slnx
```

### 배포

1. `Directory.Build.props`의 `<Version>`을 올리고 `CHANGELOG.md`에 변경 내용을 적습니다.
2. 커밋한 뒤 같은 버전의 태그를 올립니다: `git tag v0.1.0` → `git push origin v0.1.0`
3. GitHub Actions(`.github/workflows/release.yml`)가 테스트, 패키징 후 릴리스에 zip을 올립니다.

로컬에서 zip만 만들려면 `scripts/package.ps1`을 실행합니다(`artifacts/release/`).

## 라이선스

[MIT](LICENSE)
