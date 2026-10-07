# MyAIBoard

Windows 바탕화면에 떠 있는 글래스 스타일 메모 위젯입니다. Claude, Codex 같은 AI가 MCP로 메모를 추가하고 읽을 수 있습니다.
"이거 aiboard에 추가해"라고 말하면 AI가 보드에 메모를 남깁니다.

## 구성

| 프로젝트 | 역할 |
|---|---|
| `src/MyAIBoard.Core` | SQLite 메모 저장소 (`%LOCALAPPDATA%\MyAIBoard\board.db`, `MYAIBOARD_DB`로 경로 변경 가능) |
| `src/MyAIBoard.App` | WPF 위젯 (`MyAIBoard.exe`) |
| `src/MyAIBoard.Mcp` | MCP stdio 서버 (`MyAIBoard.Mcp.exe`): `add_memo`, `list_memos`, `get_memo`, `update_memo`, `delete_memo` |

위젯과 MCP 서버는 같은 DB를 씁니다. 위젯은 DB의 변경 카운터를 1초마다 확인해서 AI가 추가한 메모를 바로 보여줍니다.

## 설치

.NET 10 SDK가 필요합니다.

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install.ps1
```

`%LOCALAPPDATA%\MyAIBoard\bin`에 설치하고, 시작 메뉴 바로가기와 로그인 시 자동 실행을 등록한 뒤 위젯을 띄웁니다.
자동 실행을 원하지 않으면 `-NoAutostart`를 붙이세요(위젯 메뉴에서도 끌 수 있습니다). 다시 실행하면 업데이트됩니다.

## AI 연결

Claude Code:

```powershell
claude mcp add --scope user myaiboard -- "$env:LOCALAPPDATA\MyAIBoard\bin\MyAIBoard.Mcp.exe"
```

Codex (`~/.codex/config.toml`):

```toml
[mcp_servers.myaiboard]
command = 'C:\Users\<사용자>\AppData\Local\MyAIBoard\bin\MyAIBoard.Mcp.exe'
```

등록한 뒤 새로 시작한 세션부터 적용됩니다.

## 사용법

- **+** 새 메모 (Enter: 본문으로 이동, Ctrl+Enter: 저장, Esc: 취소), 더블클릭으로 편집
- 카드에 마우스를 올리면 편집/삭제 버튼이 나옵니다. 삭제 후 5초 동안 되돌릴 수 있습니다.
- 메뉴(⋯ 또는 우클릭): 정렬(직접/최신/오래된/제목), 모두 접기/펼치기, 창 모드(바탕화면 고정/항상 위/일반), 자동 실행
- 트레이 아이콘: 왼쪽 클릭으로 보이기/숨기기, 오른쪽 클릭으로 메뉴. Alt+F4는 종료가 아니라 숨기기입니다.

디자인 값은 `src/MyAIBoard.App/Themes/Glass.xaml`, 아이콘은 `tools/make-icon.ps1`로 다시 만듭니다.
