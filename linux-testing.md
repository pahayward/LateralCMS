# Linux testing (Debian)

Run on 2026-11-07 against commit `a541f0e`.

## Machine

| | |
|---|---|
| OS | Debian GNU/Linux 13 (trixie) |
| Kernel | 6.12.69+deb13-amd64, x86_64 |
| .NET SDK | 10.0.103 (`global.json` asks for 9.0.100 with `latestMajor` roll-forward) |
| ASP.NET Core runtime | 10.0.3 (the projects target `net9.0` and run on it by roll-forward) |

## Results

| Step | Command | Result |
|---|---|---|
| Strict build (warnings are errors) | `CI=true dotnet build -c Release` | **Passed**: 0 warnings, 0 errors |
| Console tests | `dotnet test` | **Passed**: 134 of 134 |
| Service tests | `dotnet test` | **Passed**: 684 of 684 (5 m 17 s) |
| Home-lab smoke test | `bash tools/homelab/verify.sh --no-pull --no-tests` | **Passed**: 30 of 30 |

The smoke test builds and runs the service in Production on a temporary data folder and port 5192. It checks these things:

- The first-run admin password file is mode 600 and the session key folder is mode 700.
- The forced password change works, and the temporary password file is removed afterwards.
- An event goes through end to end: the webhook answers 202, the worker applies it, and an admin can read the record.
- An asset is uploaded, scanned and downloaded in ranges, and the bytes match.
- The SSH-style admin password reset works while the service is running.
- No password or secret appears in the log, and the log has no error lines.

## Failures and fixes

There were no failures, so I changed no code and had nothing to retest.

## Notes

- This machine has only the .NET 10 SDK and runtime. Everything built and ran through roll-forward, as the README says it will.
- `verify.sh` could only note that `systemd-run` is present. It does not exercise the `systemd-run` admin command from `docs/OPERATIONS.md`. That needs trying against a real install.
- I did not run the screenshot tool (`tools/screenshots/capture.mjs`), the load smoke test or the mutation check. They are not part of the Linux verification path.
