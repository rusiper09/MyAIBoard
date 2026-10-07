# MyAIBoard

Windows 바탕화면에 떠 있는 글래스 스타일 메모 위젯입니다. Claude, Codex 같은 AI가 MCP로 메모를 추가하고 읽을 수 있습니다.
"이거 aiboard에 추가해"라고 말하면 AI가 보드에 메모를 남깁니다.

## 구성

| 프로젝트 | 역할 |
|---|---|
| `src/MyAIBoard.Core` | SQLite 메모 저장소 (`%LOCALAPPDATA%\MyAIBoard\board.db`, `MYAIBOARD_DB`로 경로 변경 가능), 해시태그, 내보내기 |
| `src/MyAIBoard.App` | WPF 위젯 (`MyAIBoard.exe`) |
| `src/MyAIBoard.Mcp` | MCP stdio 서버 (`MyAIBoard.Mcp.exe`) |
| `tests/` | Core 테스트, MCP 서버 테스트(도구 단위 + 실제 stdio 접속) |

위젯과 MCP 서버는 같은 DB를 씁니다. 위젯은 DB의 변경 카운터를 1초마다 확인해서 AI가 추가한 메모를 바로 보여줍니다.
DB 구조는 `PRAGMA user_version`으로 버전을 관리하며, 새 버전이 처음 실행될 때 기존 DB를 자동으로 올립니다.

## 설치

.NET 10 SDK가 필요합니다.

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install.ps1
```

`%LOCALAPPDATA%\MyAIBoard\bin`에 설치하고, 시작 메뉴 바로가기와 로그인 시 자동 실행을 등록한 뒤 위젯을 띄웁니다.
다시 실행하면 업데이트됩니다. 자동 실행 설정을 건드리지 않으려면 `-NoAutostart`를 붙이세요(위젯 메뉴에서도 끌 수 있습니다).
AI 세션이 실행 중인 MCP 서버는 그 세션을 다시 시작해야 새 버전으로 바뀝니다.

## AI 연결

Claude Code:

```powershell
claude mcp add --scope user myaiboard -- "$env:LOCALAPPDATA\MyAIBoard\bin\MyAIBoard.Mcp.exe"
```

Codex (CLI가 PATH에 없으면 Codex 앱에 들어 있는 `codex.exe`를 쓰면 됩니다):

```powershell
codex mcp add myaiboard --env "MYAIBOARD_DB=$env:LOCALAPPDATA\MyAIBoard\board.db" -- "$env:LOCALAPPDATA\MyAIBoard\bin\MyAIBoard.Mcp.exe"
```

Codex 앱은 MCP 서버를 샌드박스 계정(`CodexSandboxUsers` 그룹)으로 실행합니다. 그래서 두 가지가 필요합니다.
- `MYAIBOARD_DB`: 샌드박스 계정의 `%LOCALAPPDATA%`는 다른 폴더라서, 지정하지 않으면 위젯과 다른 빈 보드를 씁니다.
- 폴더 권한: `install.ps1`이 이 그룹을 찾으면 `%LOCALAPPDATA%\MyAIBoard`에 수정 권한을, `bin`에는 읽기·실행 권한만 줍니다. 권한이 없으면 Codex 로그에 `MCP startup failed ... (os error 3)`이 남습니다. Codex를 나중에 설치했다면 `install.ps1`을 다시 실행하세요.

등록한 뒤 새로 시작한 세션부터 적용됩니다. Codex 앱은 다시 시작하세요.

### MCP 도구

| 도구 | 설명 |
|---|---|
| `add_memo` | 메모 추가. AI 세션의 작업 폴더 이름이 해시태그로 자동으로 붙습니다(예: `#MyAIBoard`). |
| `list_memos` | 보드 순서대로 목록. 검색어(`query`)와 해시태그(`tag`)로 거를 수 있습니다. |
| `get_memo`, `update_memo` | 메모 하나 조회, 제목·본문 수정 |
| `delete_memo` | 휴지통으로 이동 |
| `list_trash`, `restore_memo` | 휴지통 목록, 복원 |

AI는 메모를 영구 삭제할 수 없습니다. 영구 삭제는 위젯의 휴지통에서만 할 수 있습니다.

## 사용법

**메모**
- **+** 또는 **Ctrl+Alt+N**(어디서나): 새 메모. Enter는 본문으로 이동, Ctrl+Enter는 저장, Esc는 취소입니다. 카드를 더블클릭하면 편집합니다.
- 편집하는 동안 AI가 같은 메모를 고치거나 지우면 AI의 변경이 우선이고, 편집은 취소되며 알림이 뜹니다.
- 카드에는 작성 시각이 보이고, 고친 메모는 `2일 전 · 수정 5분 전`처럼 수정 시각도 함께 보입니다. 시간에 마우스를 올리면 정확한 시각이 나옵니다.

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

`%LOCALAPPDATA%\MyAIBoard\settings.json`에 창 위치·크기, 창 모드, 정렬이 저장됩니다.
`NewMemoHotkey`로 새 메모 단축키를 바꿀 수 있습니다(예: `"Ctrl+Shift+Space"`, 빈 값이면 끔). 위젯을 다시 실행하면 적용됩니다.
다른 프로그램이 이미 쓰는 단축키라 등록하지 못하면, 메뉴의 "새 메모" 옆에 "사용 불가"가 표시됩니다.

디자인 값은 `src/MyAIBoard.App/Themes/Glass.xaml`, 아이콘은 `tools/make-icon.ps1`로 다시 만듭니다.

## 개발

```powershell
dotnet test MyAIBoard.slnx
```
