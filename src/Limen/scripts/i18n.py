"""Single source of truth for UI text.

Rewrites the XAML to `{loc:T Key}` and emits the C# lookup tables from the same
map, so a string can never drift between the markup and the table.
"""
import io, json, os, re, sys

# korean -> (key, english)
MAP = {
    # 공통
    "저장": ("Common.Save", "Save"),
    "취소": ("Common.Cancel", "Cancel"),
    "확인": ("Common.Ok", "OK"),
    "삭제": ("Common.Delete", "Delete"),
    "편집": ("Common.Edit", "Edit"),
    "복제": ("Common.Duplicate", "Duplicate"),
    "이름 바꾸기": ("Common.Rename", "Rename"),
    "새로고침": ("Common.Refresh", "Refresh"),
    "새 폴더": ("Common.NewFolder", "New folder"),
    "찾아보기": ("Common.Browse", "Browse"),
    "검색": ("Common.Search", "Search"),
    "다시 시도": ("Common.Retry", "Retry"),
    "붙여넣기": ("Common.Paste", "Paste"),
    "이름": ("Common.Name", "Name"),
    "크기": ("Common.Size", "Size"),
    "수정": ("Common.Modified", "Modified"),
    "상위 폴더": ("Common.ParentFolder", "Parent folder"),
    "상위 폴더 (Backspace)": ("Common.ParentFolderHint", "Parent folder (Backspace)"),
    "선택 항목 삭제": ("Common.DeleteSelection", "Delete selection"),

    # 메인 창
    "세션": ("Main.Sessions", "Sessions"),
    "새 세션": ("Main.NewSession", "New session"),
    "새 세션 (Ctrl+N)": ("Main.NewSessionHint", "New session (Ctrl+N)"),
    "선택된 세션 없음": ("Main.NoSelection", "No session selected"),
    "이름 · 폴더 · 호스트 검색": ("Main.SearchPlaceholder", "Search name, folder or host"),
    "검색 지우기 (Esc)": ("Main.ClearSearch", "Clear search (Esc)"),
    "SSH 터미널": ("Main.SshTerminal", "SSH terminal"),
    "SFTP 전송": ("Main.SftpTransfer", "SFTP transfer"),
    "SSH 터미널 열기": ("Main.OpenSsh", "Open SSH terminal"),
    "SFTP 전송 열기": ("Main.OpenSftp", "Open SFTP transfer"),
    "선택한 세션의 터미널 열기 (Enter)": ("Main.OpenSshHint", "Open a terminal for the selected session (Enter)"),
    "로컬 ↔ 원격 2분할 파일 전송 탭 열기": ("Main.OpenSftpHint", "Open a split local/remote transfer tab"),
    "세션 편집 (F2)": ("Main.EditHint", "Edit session (F2)"),
    "세션 복제": ("Main.DuplicateHint", "Duplicate session"),
    "세션 삭제 (Delete)": ("Main.DeleteHint", "Delete session (Delete)"),
    "세션 파일이 있는 폴더 열기": ("Main.OpenStoreFolder", "Open the folder holding the session file"),
    "열려 있는 세션이 없습니다": ("Main.EmptyTitle", "No open sessions"),
    "왼쪽 목록에서 세션을 고르고 SSH 터미널 또는 SFTP 전송을 여세요.":
        ("Main.EmptyBody", "Pick a session on the left, then open a terminal or a transfer tab."),
    "접속": ("Main.Connect", "Connect"),

    # 세션 편집
    "세션 설정": ("Editor.Title", "Session settings"),
    "접속 정보와 자격 증명을 저장합니다.": ("Editor.Subtitle", "Stores connection details and credentials."),
    "연결": ("Editor.GroupConnection", "Connection"),
    "인증": ("Editor.GroupAuth", "Authentication"),
    "경유 (BASTION)": ("Editor.GroupBastion", "Jump host"),
    "시작 위치와 자동 실행": ("Editor.GroupStartup", "Startup location and command"),
    "세션 이름": ("Editor.SessionName", "Session name"),
    "폴더": ("Editor.Folder", "Folder"),
    "호스트": ("Editor.Host", "Host"),
    "포트": ("Editor.Port", "Port"),
    "사용자": ("Editor.User", "User"),
    "인증 방식": ("Editor.AuthMode", "Method"),
    "비밀번호": ("Editor.Password", "Password"),
    "개인 키": ("Editor.PrivateKey", "Private key"),
    "키 파일": ("Editor.KeyFile", "Key file"),
    "키 암호": ("Editor.Passphrase", "Passphrase"),
    "점프 호스트": ("Editor.JumpHost", "Jump host"),
    "색 표시": ("Editor.Colour", "Tag colour"),
    "원격 경로": ("Editor.RemotePath", "Remote path"),
    "로컬 경로": ("Editor.LocalPath", "Local path"),
    "접속 후 명령": ("Editor.LoginCommand", "Command on connect"),
    "자격 증명 삭제": ("Editor.ForgetCredentials", "Forget credentials"),
    "예: 운영 DB 서버": ("Editor.NamePlaceholder", "e.g. Production DB"),
    "예: Production/DB — 비우면 최상위": ("Editor.FolderPlaceholder", "e.g. Production/DB — empty for top level"),
    "host.example.com 또는 10.0.0.5": ("Editor.HostPlaceholder", "host.example.com or 10.0.0.5"),
    "비우면 접속할 때마다 물어봅니다": ("Editor.PasswordPlaceholder", "Leave empty to be asked each time"),
    "키에 암호가 없으면 비워 두세요": ("Editor.PassphrasePlaceholder", "Leave empty if the key has no passphrase"),
    "비우면 홈 디렉터리": ("Editor.RemotePlaceholder", "Empty for the home directory"),
    "비우면 내 문서": ("Editor.LocalPlaceholder", "Empty for Documents"),
    "예: sudo -i — 비우면 실행하지 않음": ("Editor.CommandPlaceholder", "e.g. sudo -i — empty to run nothing"),
    "목록에서 묶일 위치입니다. 하위 폴더는 / 로 구분합니다.":
        ("Editor.FolderHint", "Where the session is grouped in the list. Separate sub-folders with /."),
    "저장하면 Windows DPAPI로 암호화되어 현재 Windows 계정에서만 복호화됩니다.":
        ("Editor.PasswordHint", "Stored with Windows DPAPI and readable only by this Windows account."),
    "대상 서버에 직접 연결합니다.": ("Editor.NoJumpHint", "Connects straight to the target host."),
    "목록·탭·터미널 상단에 이 색이 표시됩니다. 운영 서버를 눈에 띄게 해 두면 실수를 줄일 수 있습니다.":
        ("Editor.ColourHint", "Shown in the list, on the tab and above the terminal. Tagging production hosts makes them hard to mistake."),
    "이 세션에 저장된 비밀번호와 키 암호를 지웁니다.":
        ("Editor.ForgetHint", "Clears the password and passphrase stored for this session."),
    "SFTP 탐색기가 처음 여는 원격 디렉터리.": ("Editor.RemoteHint", "The directory the SFTP pane opens first."),
    "터미널 접속 직후 자동으로 실행할 명령.": ("Editor.CommandHint", "Run automatically once the terminal connects."),

    # 비밀번호 입력
    "입력 후 Enter": ("Prompt.Placeholder", "Type and press Enter"),
    "이 컴퓨터에 저장 (현재 Windows 계정만 복호화 가능)":
        ("Prompt.Remember", "Remember on this computer (readable only by this Windows account)"),

    # 붙여넣기 확인
    "여러 줄 붙여넣기": ("Paste.Title", "Multi-line paste"),
    "줄바꿈이 포함되어 있어 붙여넣는 즉시 실행됩니다. 내용을 확인하세요.":
        ("Paste.Body", "This contains newlines, so the shell runs it the moment it is pasted. Check the content."),

    # SFTP
    "로컬": ("Sftp.Local", "Local"),
    "원격": ("Sftp.Remote", "Remote"),
    "이 컴퓨터": ("Sftp.ThisComputer", "This computer"),
    "업로드": ("Sftp.Upload", "Upload"),
    "다운로드": ("Sftp.Download", "Download"),
    "권한 변경": ("Sftp.Permissions", "Permissions"),
    "경로 입력 후 Enter": ("Sftp.PathPlaceholder", "Type a path and press Enter"),
    "원격 경로 입력 후 Enter": ("Sftp.RemotePathPlaceholder", "Type a remote path and press Enter"),
    "선택한 항목 업로드 (로컬 → 원격)": ("Sftp.UploadHint", "Upload selection (local to remote)"),
    "선택한 항목 다운로드 (원격 → 로컬)": ("Sftp.DownloadHint", "Download selection (remote to local)"),
    "진행 중인 전송 중지": ("Sftp.CancelHint", "Stop the transfer in progress"),
    "선택한 항목을 Windows 탐색기로 끌어 다운로드할 수 있습니다":
        ("Sftp.DragHint", "Drag a selection to Windows Explorer to download it"),
    "이 폴더는 비어 있습니다": ("Sftp.EmptyFolder", "This folder is empty"),
    "대기 중": ("Sftp.Idle", "Idle"),
    "접속 중…": ("Sftp.Connecting", "Connecting…"),
    "루트 파일시스템 (/)": ("Sftp.RootFilesystem", "Root filesystem (/)"),

    # 워크스페이스 / 리소스
    "SFTP 패널 접기": ("Workspace.CollapseSftp", "Collapse the SFTP pane"),
    "SSH 연결이 열리면 이 서버의 SFTP 탐색기가 여기에 표시됩니다.":
        ("Workspace.SftpPending", "The SFTP browser for this host appears here once the SSH session is up."),
    "터미널 출력을 파일로 기록": ("Workspace.StartLog", "Record terminal output to a file"),
    "연결 대기": ("Metrics.Waiting", "Waiting"),
    "메모리": ("Metrics.Memory", "Memory"),
    "디스크": ("Metrics.Disk", "Disk"),

    # 코드에서 넣는 문자열
    "세션 설정 — {0}": ("Editor.TitleFor", "Session settings — {0}"),
    "접속 정보를 입력하면 목록에 추가됩니다.": ("Editor.SubtitleNew", "Fill in the connection details to add it to the list."),
    "없음 — 직접 연결": ("Editor.NoJump", "None — direct connection"),
    "저장된 비밀번호 유지 — 바꾸려면 새로 입력": ("Editor.KeepPassword", "Keeps the stored password — type to replace it"),
    "저장된 키 암호 유지 — 바꾸려면 새로 입력": ("Editor.KeepPassphrase", "Keeps the stored passphrase — type to replace it"),
    "저장된 비밀번호가 있습니다. 비워 두면 그대로 유지되고, 지우려면 아래 '자격 증명 삭제'를 누르세요.": ("Editor.HasPassword", "A password is stored. Leave the box empty to keep it, or use Forget credentials below to remove it."),
    "저장된 자격 증명을 지웠습니다. 저장을 눌러 반영하세요.": ("Editor.Forgotten", "Credentials cleared. Press Save to apply."),
    "호스트를 입력하세요.": ("Editor.NeedHost", "Enter a host."),
    "포트는 1~65535 사이의 숫자여야 합니다.": ("Editor.NeedPort", "Port must be a number between 1 and 65535."),
    "사용자 이름을 입력하세요.": ("Editor.NeedUser", "Enter a user name."),
    "개인 키 파일을 찾을 수 없습니다. 경로를 확인하세요.": ("Editor.NeedKeyFile", "Private key file not found. Check the path."),
    "개인 키 선택": ("Editor.PickKey", "Choose a private key"),
    "개인 키 (*.pem;*.key;id_*)|*.pem;*.key;id_*|모든 파일 (*.*)|*.*": ("Editor.KeyFilter", "Private keys (*.pem;*.key;id_*)|*.pem;*.key;id_*|All files (*.*)|*.*"),
    "신뢰한 호스트 키  {0}": ("Editor.HostKeyTrusted", "Trusted host key  {0}"),
    "아직 신뢰한 호스트 키가 없습니다. 첫 접속 때 지문을 확인합니다.": ("Editor.HostKeyNew", "No host key trusted yet. The fingerprint is shown on first connect."),
    "{0}에 먼저 연결한 뒤 대상 서버로 터널링합니다.": ("Editor.JumpHint", "Connects to {0} first, then tunnels through to the target."),
    "다크 모드로 전환": ("Main.ThemeToDark", "Switch to dark mode"),
    "라이트 모드로 전환": ("Main.ThemeToLight", "Switch to light mode"),
    "다크 모드를 적용했습니다.": ("Main.ThemeDarkApplied", "Dark mode applied."),
    "라이트 모드를 적용했습니다.": ("Main.ThemeLightApplied", "Light mode applied."),
    "사용자 이름 필요": ("Main.NeedUserTitle", "User name required"),
    "이 세션에는 사용자 이름이 없습니다. 먼저 편집에서 지정하세요.": ("Main.NeedUserBody", "This session has no user name. Set one in the editor first."),
    "세션 삭제": ("Main.DeleteTitle", "Delete session"),
    "'{0}' 세션을 삭제할까요?\n저장된 자격 증명도 함께 지워집니다.": ("Main.DeleteBody", "Delete the session '{0}'?\nStored credentials are removed with it."),
    "아직 세션이 없습니다.\n아래 '새 세션'으로 첫 서버를 등록하세요.": ("Main.TreeEmpty", "No sessions yet.\nUse New session below to add your first host."),
    "'{0}' 와(과) 일치하는 세션이 없습니다.": ("Main.NoMatch", "Nothing matches '{0}'."),
    "저장된 세션이 없습니다. '새 세션'으로 시작하세요.": ("Main.StoreEmpty", "No saved sessions. Start with New session."),
    "{0}개 세션을 불러왔습니다.": ("Main.Loaded", "Loaded {0} sessions."),
    "{0}개": ("Main.CountAll", "{0}"),
    "{0} (복사본)": ("Main.CopySuffix", "{0} (copy)"),
    "{0} 탭을 열었습니다: {1}": ("Main.TabOpened", "Opened a {0} tab: {1}"),
    "{0} — {1}": ("Main.SelectionStatus", "{0} — {1}"),
    "세션 목록을 읽지 못했습니다.\n{0}": ("Main.StoreReadFailed", "Could not read the session list.\n{0}"),
    "세션 목록을 읽지 못했습니다: {0}": ("Main.StoreReadFailedShort", "Could not read the session list: {0}"),
    "세션 파일 폴더 열기\n{0}": ("Main.StoreTip", "Open the session file folder\n{0}"),
    "세션을 복제했습니다: {0}": ("Main.Duplicated", "Duplicated session: {0}"),
    "세션을 삭제했습니다: {0}": ("Main.Deleted", "Deleted session: {0}"),
    "세션을 저장했습니다: {0}": ("Main.Saved", "Saved session: {0}"),
    "세션을 추가했습니다: {0}": ("Main.Added", "Added session: {0}"),
    "종료 확인": ("Main.QuitTitle", "Quit"),
    "연결된 세션이 {0}개 있습니다. 종료할까요?": ("Main.QuitBody", "{0} sessions are still connected. Quit anyway?"),
    "폴더를 열지 못했습니다: {0}": ("Main.FolderOpenFailed", "Could not open the folder: {0}"),
    "SFTP 패널 열기": ("Workspace.ExpandSftp", "Expand the SFTP pane"),
    "기록 중지": ("Workspace.StopLog", "Stop recording"),
    "세션 기록 저장": ("Workspace.LogDialogTitle", "Save session recording"),
    "세션 기록": ("Workspace.LogTitle", "Session recording"),
    "로그 파일 (*.log)|*.log|텍스트 파일 (*.txt)|*.txt|모든 파일 (*.*)|*.*": ("Workspace.LogFilter", "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*"),
    "{0}: 기록 중 — {1}": ("Workspace.LogStarted", "{0}: recording to {1}"),
    "{0}: 기록을 멈췄습니다": ("Workspace.LogStopped", "{0}: recording stopped"),
    "기록을 시작하지 못했습니다.\n{0}": ("Workspace.LogFailed", "Could not start recording.\n{0}"),
    "실시간": ("Metrics.Live", "Live"),
    "첫 표본 수집 중": ("Metrics.FirstSample", "Taking first sample"),
    "{0}코어": ("Metrics.Cores", "{0} cores"),
    "{0} · {1} 남음": ("Metrics.DiskDetail", "{0} · {1} free"),
    "{0}줄을 붙여넣습니다": ("Paste.LineCount", "Pasting {0} lines"),
    "… 그리고 {0}줄 더": ("Paste.More", "… and {0} more lines"),
    "없음": ("Colour.None", "None"),
    "빨강": ("Colour.Red", "Red"),
    "주황": ("Colour.Orange", "Orange"),
    "노랑": ("Colour.Yellow", "Yellow"),
    "초록": ("Colour.Green", "Green"),
    "파랑": ("Colour.Blue", "Blue"),
    "보라": ("Colour.Purple", "Purple"),
    "회색": ("Colour.Grey", "Grey"),
    "--- 기록 종료 {0} ---": ("Log.Ended", "--- recording ended {0} ---"),
    "--- {0} ({1}) 기록 시작 {2} ---": ("Log.Started", "--- {0} ({1}) recording started {2} ---"),

    # 전송/터미널/커넥터 메시지
    "접속을 취소했습니다.": ("Sftp.Cancelled", "Connection cancelled."),
    "Bastion 접속을 취소했습니다.": ("Sftp.BastionCancelled", "Jump host connection cancelled."),
    "{0} 접속 중…": ("Sftp.ConnectingTo", "Connecting to {0}…"),
    "{0}: SFTP 연결됨": ("Sftp.Connected", "{0}: SFTP connected"),
    "{0}: SFTP 접속 중": ("Sftp.ConnectingStatus", "{0}: connecting SFTP"),
    "{0}: SFTP 접속 실패": ("Sftp.ConnectFailedStatus", "{0}: SFTP connection failed"),
    "{0} 에 접속하지 못했습니다.": ("Sftp.ConnectFailed", "Could not connect to {0}."),
    "대상 서버 {0} SFTP 접속에 실패했습니다.": ("Sftp.StageDirect", "SFTP connection to {0} failed."),
    "Bastion 연결은 성공했지만 내부 서버 접속 단계에서 실패했습니다.": ("Sftp.StageBastion", "The jump host connected, but the target host did not."),
    "터미널의 현재 경로를 따라갑니다": ("Sftp.FollowsTerminal", "Follows the terminal's directory"),
    "만들 폴더 이름을 입력하세요.": ("Sftp.NewFolderLocal", "Name for the new folder."),
    "만들 원격 폴더 이름을 입력하세요.": ("Sftp.NewFolderRemote", "Name for the new remote folder."),
    "'{0}' 의 새 이름을 입력하세요.": ("Sftp.RenamePrompt", "New name for '{0}'."),
    "이름에 / 또는 \\ 는 쓸 수 없습니다.": ("Sftp.RenameBadName", "A name cannot contain / or \\."),
    "이름을 바꿨습니다: {0} → {1}": ("Sftp.Renamed", "Renamed: {0} → {1}"),
    "'{0}' 의 권한을 8진수로 입력하세요. 예: 644, 755": ("Sftp.ChmodPrompt", "Octal permissions for '{0}'. For example 644 or 755."),
    "권한은 0~7 사이 숫자 3~4자리여야 합니다.": ("Sftp.ChmodBad", "Permissions must be 3 or 4 digits, each 0-7."),
    "권한을 바꿨습니다: {0} → {1}": ("Sftp.Chmodded", "Permissions changed: {0} → {1}"),
    "삭제 확인": ("Sftp.DeleteTitle", "Confirm delete"),
    "{0}개 항목을 삭제합니다. 되돌릴 수 없습니다.": ("Sftp.DeleteBody", "Deleting {0} items. This cannot be undone."),
    "… 외 {0}개": ("Sftp.DeleteMore", "… and {0} more"),
    "폴더가 없습니다: {0}": ("Sftp.FolderMissing", "Folder not found: {0}"),
    "파일을 열 수 없습니다.": ("Sftp.OpenFailed", "Could not open the file."),
    "파일 실행 확인": ("Sftp.OpenExecutableTitle", "Confirm running a file"),
    "실행 가능한 파일입니다. 정말 여시겠습니까?": ("Sftp.OpenExecutable", "This is an executable file. Open it anyway?"),
    "{0} 열기": ("Sftp.OpenTitle", "Open {0}"),
    "열기용 다운로드": ("Sftp.DownloadForOpen", "Download to open"),
    "드래그 다운로드": ("Sftp.DragDownload", "Drag download"),
    "드래그 취소됨": ("Sftp.DragCancelled", "Drag cancelled"),
    "드래그가 취소되었습니다 — 다운로드가 끝날 때까지 마우스 버튼을 누르세요": ("Sftp.DragHold", "Drag cancelled — hold the mouse button until the download finishes"),
    "탐색기로 다운로드 완료": ("Sftp.DragDone", "Downloaded to Explorer"),
    "준비 중…": ("Sftp.Preparing", "Preparing…"),
    "{0} 완료": ("Sftp.TransferDone", "{0} complete"),
    "{0}: {1} 완료": ("Sftp.TransferDoneStatus", "{0}: {1} complete"),
    "{0} 중지됨": ("Sftp.TransferStopped", "{0} stopped"),
    "{0} 실패": ("Sftp.TransferFailed", "{0} failed"),
    "{0} — {1}%": ("Sftp.TransferProgress", "{0} — {1}%"),
    "터미널 화면을 초기화할 수 없습니다.": ("Terminal.InitFailed", "Could not initialise the terminal view."),
    "Microsoft Edge WebView2 Runtime 설치 여부를 확인하세요.": ("Terminal.InitHint", "Check that the Microsoft Edge WebView2 Runtime is installed."),
    "{0}: 터미널 초기화 실패": ("Terminal.InitFailedStatus", "{0}: terminal failed to start"),
    "{0}: 접속 중": ("Terminal.Connecting", "{0}: connecting"),
    "{0} 접속 중…": ("Terminal.ConnectingNotice", "Connecting to {0}…"),
    "{0}: 연결됨 ({1})": ("Terminal.Connected", "{0}: connected ({1})"),
    "{0}: 연결이 끊어졌습니다": ("Terminal.Disconnected", "{0}: disconnected"),
    "{0}: 접속 실패": ("Terminal.ConnectFailedStatus", "{0}: connection failed"),
    "{0} 에 접속하지 못했습니다.": ("Terminal.ConnectFailed", "Could not connect to {0}."),
    "대상 서버 {0} 접속에 실패했습니다.": ("Terminal.StageDirect", "Connection to {0} failed."),
    "Bastion 연결은 성공했지만 내부 서버 접속 단계에서 실패했습니다.": ("Terminal.StageBastion", "The jump host connected, but the target host did not."),
    "── 세션 종료됨 · Enter 를 누르면 다시 접속합니다 ──": ("Terminal.SessionEnded", "── session ended · press Enter to reconnect ──"),
    "Enter 를 누르면 다시 시도합니다.": ("Terminal.RetryHint", "Press Enter to try again."),
    "{0}: 선택한 텍스트를 복사했습니다": ("Terminal.Copied", "{0}: selection copied"),
    "{0}: 클립보드를 읽지 못했습니다 — {1}": ("Terminal.ClipboardFailed", "{0}: could not read the clipboard — {1}"),
    "비밀번호": ("Connector.Password", "Password"),
    "키 암호": ("Connector.Passphrase", "Passphrase"),
    "Bastion 비밀번호": ("Connector.BastionPassword", "Jump host password"),
    "{0} 의 비밀번호를 입력하세요.": ("Connector.AskPassword", "Password for {0}."),
    "{0} 의 암호를 입력하세요.": ("Connector.AskPassphrase", "Passphrase for {0}."),
    "{0} 추가 인증": ("Connector.ExtraAuth", "{0} — additional authentication"),
    "개인 키 파일을 찾을 수 없습니다.": ("Connector.NoKeyFile", "Private key file not found."),
    "Bastion 개인 키 파일을 찾을 수 없습니다.": ("Connector.NoBastionKeyFile", "Jump host private key file not found."),
    "Bastion 인증 정보가 없습니다.": ("Connector.NoBastionCredentials", "No credentials for the jump host."),
    "Bastion {0} 접속 또는 터널 생성에 실패했습니다.": ("Connector.BastionFailed", "Could not connect or tunnel through {0}."),
    "호스트 키 확인": ("Connector.HostKeyTitle", "Host key check"),
    "{0}:{1} 에 처음 접속합니다.": ("Connector.HostKeyBody", "First connection to {0}:{1}."),
    "호스트 키 ({0})": ("Connector.HostKeyFingerprint", "Host key ({0})"),
    "이 서버를 신뢰하고 지문을 저장할까요?": ("Connector.HostKeyTrustAsk", "Trust this host and remember the fingerprint?"),
    "호스트 키 불일치": ("Connector.HostKeyChangedTitle", "Host key mismatch"),
    "경고: {0}:{1} 의 호스트 키가 변경되었습니다.": ("Connector.HostKeyChanged", "Warning: the host key for {0}:{1} has changed."),
    "저장된 지문": ("Connector.HostKeyStored", "Stored fingerprint"),
    "서버가 보낸 지문": ("Connector.HostKeyOffered", "Fingerprint offered"),
    "중간자 공격일 수 있습니다. 계속할까요?": ("Connector.HostKeyMitm", "This may be a man-in-the-middle attack. Continue?"),

    "Bastion 연결은 성공했지만 내부 서버 {0} SFTP 접속에 실패했습니다.": ("Sftp.StageBastionTarget", "The jump host connected, but the SFTP connection to {0} failed."),
    "Bastion 연결은 성공했지만 내부 서버 {0} 접속에 실패했습니다.": ("Terminal.StageBastionTarget", "The jump host connected, but the connection to {0} failed."),
    "[세션 종료됨 — Enter 를 누르면 다시 접속합니다]": ("Terminal.SessionEndedBox", "[session ended — press Enter to reconnect]"),

    "{0}개 삭제 중…": ("Sftp.Deleting", "Deleting {0} items…"),
    "{0}개를 삭제했습니다": ("Sftp.Deleted", "Deleted {0} items"),
    "{0}개를 삭제했습니다 (서버에서 처리)": ("Sftp.DeletedFast", "Deleted {0} items (handled on the server)"),
    "폴더는 서버에서 rm -rf 로 삭제됩니다. 하위 내용이 모두 사라집니다.": ("Sftp.DeleteRecursiveWarning", "Folders are removed with rm -rf on the server. Everything inside goes with them."),

    # 테마 리소스
    "탭 닫기 (Ctrl+W)": ("Tab.Close", "Close tab (Ctrl+W)"),
}

SKIP = {"—"}

ATTRS = "Text|Content|Header|ToolTip|Title|local:Ui.Placeholder|Value"
PATTERN = re.compile(r'((?:' + ATTRS + r')=)"([^"]*)"')


def rewrite(path, unmapped):
    source = io.open(path, encoding="utf-8").read()

    def swap(match):
        attr, value = match.group(1), match.group(2)
        if not re.search(r"[가-힣]", value) or value in SKIP:
            return match.group(0)
        entry = MAP.get(value)
        if entry is None:
            unmapped.append((os.path.basename(path), value))
            return match.group(0)
        return f'{attr}"{{loc:T {entry[0]}}}"'

    updated = PATTERN.sub(swap, source)
    if updated != source:
        io.open(path, "w", encoding="utf-8", newline="").write(updated)
        return True
    return False


def emit_tables(path):
    def block(index):
        rows = sorted(((key, text[index]) for text, key in
                       ((v, v[0]) for v in MAP.values())), key=lambda r: r[0])
        return rows

    korean = sorted(((key, ko) for ko, (key, _) in MAP.items()), key=lambda r: r[0])
    english = sorted(((key, en) for _, (key, en) in MAP.items()), key=lambda r: r[0])

    def render(name, rows):
        lines = [f"    private static readonly Dictionary<string, string> {name} = new()", "    {"]
        for key, text in rows:
            lines.append(f'        ["{key}"] = {json.dumps(text, ensure_ascii=False)},')
        lines.append("    };")
        return "\n".join(lines)

    body = (
        "namespace Limen;\n\n"
        "/// Generated from scripts/i18n.py — edit the map there, not this file.\n"
        "public sealed partial class Strings\n{\n"
        + render("Korean", korean) + "\n\n" + render("English", english) + "\n}\n"
    )
    io.open(path, "w", encoding="utf-8", newline="").write(body)


if __name__ == "__main__":
    root = sys.argv[1]
    unmapped = []
    touched = []
    for folder, _, files in os.walk(root):
        if os.path.basename(folder) in ("bin", "obj"):
            continue
        for name in files:
            if name.endswith(".xaml") and rewrite(os.path.join(folder, name), unmapped):
                touched.append(name)
    emit_tables(os.path.join(root, "Services", "Strings.Tables.cs"))
    print(f"치환한 XAML: {len(touched)}개 — {', '.join(sorted(touched))}")
    print(f"문자열 항목: {len(MAP)}개")
    if unmapped:
        print("매핑 없음:")
        for where, value in unmapped:
            print(f"  {where}: {value}")
