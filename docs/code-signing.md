# Code signing policy

## Status

The 0.0.1 and 0.0.2 releases are unsigned. SignPath integration is prepared,
but Foundation approval and account configuration are pending. This document
does not claim sponsorship or that existing downloads have been signed.
Each release's notes identify whether its executable is signed.

Once approved and enabled: Free code signing provided by
[SignPath.io](https://signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

## Team and release process

- Author and reviewer: [LJW1216](https://github.com/LJW1216).
- Signing approver: [LJW1216](https://github.com/LJW1216).

Contributed changes must be reviewed by the maintainer. Signing participants
must enable multi-factor authentication on GitHub and SignPath.
GitHub-hosted runners build the executable from this repository.
Production signing requests require manual maintainer approval in SignPath.
Only Limen's executable is submitted for signing; upstream libraries are not
signed under Limen's identity.

With signing enabled, publication stops if signing or verification fails.
The workflow checks the executable's product metadata, trusted Authenticode
signature and timestamp before packaging it.

## Privacy

Limen has no application telemetry or automatic update checks. SSH connections
and SFTP transfers communicate with servers selected by the user. Saved
profiles, encrypted credentials and session logs stay on the user's computer
unless the user chooses to share them.

The terminal uses Microsoft's WebView2 Runtime. Its runtime servicing and
diagnostic behavior are governed by
[Microsoft's privacy statement](https://privacy.microsoft.com/privacystatement).
Limen's JavaScript terminal assets are bundled locally.

Signed new releases can still show SmartScreen warnings while reputation
builds; company software policies may require administrator approval.
