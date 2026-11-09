# Security policy

## Reporting a vulnerability

Please report security problems privately, not in a public issue. On the repository's
**Security** tab, choose **Report a vulnerability**
([direct link](https://github.com/tank104/gyrodial/security/advisories/new)). You'll get a reply
as soon as possible. Once there's a fix, the report can be published with credit to you, if you
like.

## Supported versions

Fixes go into the latest release. Please check that the problem still happens on the latest
release, or on `main`, before reporting it.

## What's worth reporting

Puck runs on your PC and listens for input from a few places. These are the places that matter most:

- **Bluetooth:** any Bluetooth LE device in range can connect to the puck and send it lines.
  Crashing it, hanging it, or getting past pairing over BLE is in scope.
- **Local endpoints:** the Claude Code hook endpoint and the DialKit bridge
  (`ws://127.0.0.1:47824/bridge`). Web pages from other sites must not reach them, and forged
  approvals must not be accepted.
- **The Claude Code hook installer:** it edits `%USERPROFILE%\.claude\settings.json`.
- **Secrets:** the Anthropic API key and the Teams token, which are stored encrypted with DPAPI.
- **Firmware updates:** the app and the web installer flash firmware to the board.
