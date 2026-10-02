# SignPath 신청과 배포 설정

현재 상태: 로컬 연동 준비 완료. **신청 제출·Foundation 승인·실제 서명은 미완료**입니다.
기존 EXE에는 서명이 추가되지 않았습니다.

## 1. 변경 사항을 GitHub에 반영

README, `docs/code-signing.md`, 배포 워크플로와 `.github/signpath` 설정을 먼저
저장소에 반영하세요. 신청 담당자가 공개 링크에서 정책을 읽을 수 있어야 합니다.
아래 유지보수자 역할이 실제 팀 구성과 일치하는지도 확인하세요.

## 2. 무료 서명 신청

[공식 신청 화면](https://signpath.org/apply)에서 아래 내용을 사용하세요.
성명·이메일은 본인이 입력하고, 필수 약관과 개인정보 처리 동의는 직접 검토하세요.
마케팅 수신 동의는 선택 사항입니다.

| 항목 | 입력 내용 |
| --- | --- |
| Project Name | Limen |
| Repository URL | https://github.com/LJW1216/limen-ssh |
| Homepage URL | https://github.com/LJW1216/limen-ssh |
| Download URL | https://github.com/LJW1216/limen-ssh#download |
| Privacy Policy URL | https://github.com/LJW1216/limen-ssh/blob/main/docs/code-signing.md#privacy |
| Build System | GitHub Actions |
| First Name / Last Name | 본인의 영문 이름 / 성 |
| Email | 신청·계정 알림을 받을 본인의 이메일 |
| Maintainer Type | 실제 유지보수 형태에 맞는 선택지 |
| Primary Discovery Channel | 실제 발견 경로에 맞는 선택지 |
| Exact source | Codex / ChatGPT |

Tagline:

> A Windows SSH client with integrated terminal, SFTP and saved server profiles.

Description:

> Limen is an MIT-licensed Windows desktop application for connecting to user-selected servers over SSH and transferring files over SFTP. It combines an interactive terminal with saved connection profiles and local credential storage. It is distributed as a portable executable and built from its public source repository using GitHub Actions.

Reputation 초안 — 실제 통계·외부 소개가 있으면 사실에 맞게 추가:

> Limen is an early-stage project. Its public source and release history are available at https://github.com/LJW1216/limen-ssh and https://github.com/LJW1216/limen-ssh/releases. We do not claim widespread adoption or independent endorsements. Please assess the published source, release history and build workflow when considering this application.

심사에는 프로젝트 평판도 포함됩니다. MIT 라이선스만으로 승인이 보장되지는 않습니다.
[Foundation 조건](https://signpath.org/terms.html)을 확인하세요. WebView2 SDK와
단일 파일에 포함되는 런타임·외부 구성 요소의 적격성도 심사 시 확인하세요.

## 3. 승인 후 SignPath 설정

SignPath 담당자의 온보딩 안내를 우선 따르세요.

1. GitHub와 SignPath 계정에 MFA를 활성화합니다.
2. SignPath 프로젝트를 이 저장소와 GitHub.com 빌드 시스템에 연결합니다.
   GitHub App 접근은 필요한 저장소로 한정합니다.
3. `.github/signpath/artifact-configuration.xml`을 artifact configuration으로
   등록합니다. GitHub artifact ZIP 안의 `Limen.exe`만 대상으로 하며,
   `Limen` 제품명과 빌드에서 읽은 제품 버전을 제한합니다.
4. Foundation 인증서를 사용하는 production signing policy를 설정합니다.
   **각 요청에 유지보수자의 수동 승인**이 필요하도록 설정하세요.
   테스트 인증서는 정식 배포용으로 사용하지 않습니다.
5. 해당 프로젝트·정책에 제출 권한을 가진 토큰을 발급합니다.

## 4. GitHub Actions 설정

저장소 Settings → Secrets and variables → Actions에서 등록합니다.

| 종류 | 이름 | 값 |
| --- | --- | --- |
| Secret | `SIGNPATH_API_TOKEN` | SignPath 제출용 토큰 |
| Variable | `SIGNPATH_ORGANIZATION_ID` | 승인 후 제공받은 organization ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | 실제 project slug |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | production signing policy slug |
| Variable | `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | 등록한 artifact configuration slug |
| Variable | `SIGNPATH_ENABLED` | 모두 준비된 뒤 `true` |

토큰은 채팅·소스 파일에 붙여 넣지 마세요. 활성화 전에는 기존처럼 미서명
릴리스가 가능하며, 릴리스 설명에 미서명이라고 표시됩니다. 활성화한 뒤에는
설정 누락·서명 실패·검증 실패 시 릴리스가 중단되고 미서명 파일로 대체하지 않습니다.

## 5. 첫 서명 릴리스 확인

`v0.0.3` 같은 새 버전 태그를 사용하세요. 워크플로가 태그 버전을 빌드에 적용하고,
서명 요청을 보낸 뒤 최대 1시간 승인 완료를 기다립니다. SignPath에서 요청의
저장소·커밋·태그·artifact를 확인하고 수동 승인하세요.
기존 릴리스 파일을 조용히 교체하기보다는 새 버전으로 배포하세요.

서명 결과는 별도 디렉터리에서 검증한 뒤 EXE와 ZIP 양쪽에 사용됩니다.
Actions의 임시 `Limen-unsigned-*` artifact는 배포 파일이 아닙니다.
다운로드한 파일은 Windows에서 확인할 수 있습니다.

```powershell
Get-AuthenticodeSignature -LiteralPath .\Limen-0.0.3.exe |
  Format-List Status, SignerCertificate, TimeStamperCertificate
```

`Status`가 `Valid`인지 확인하세요. 서명해도 회사 보안 정책이나 SmartScreen
평판 경고가 모두 사라진다는 보장은 없습니다.

참고: [GitHub 연동](https://docs.signpath.io/trusted-build-systems/github),
[artifact 설정 예제](https://docs.signpath.io/artifact-configuration/examples).
