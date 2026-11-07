# Running it at home

For a home server (Linux with systemd is assumed, but nothing here is Linux-only) where you are the only operator. The same
steps, as copyable blocks, are in the engineering console under **Settings and storage > Run it at home**.

What was and was not run: the first-run, sign-in, forced password change and SSH reset steps below were run for real against
a Production-mode service on Windows. The systemd unit, the reverse proxy and the backup commands are written from the way the
service behaves and have not been run on Linux.

## What you are running

One process, `Lateral.Cms`, that listens on one port. It keeps its data in two places:

| What | Where (setting) | Notes |
|---|---|---|
| Records, the queue, people, the audit log | one SQLite file (`Database:ConnectionString`) | WAL mode; the `-wal` and `-shm` files beside it belong with it |
| Asset bytes | one folder (`Assets:Root`) | `blobs/` (clean, named by SHA-256), `quarantine/` (waiting for the scanner), `parts/` (uploads in flight) |
| Session keys | `keys/` beside the database (`Auth:KeysPath`) | sign the browser cookies; losing them signs everyone out |
| Logs | standard output | JSON lines; under systemd they are in `journalctl`. No payload, password or credential is ever logged |

There is no other storage option. The asset store is behind an interface (`IAssetStore`), so an S3-compatible backend could
replace the folder; none is built.

## Install

```bash
dotnet publish src/Lateral.Cms -c Release -o /opt/lateral
sudo useradd --system --home /var/lib/lateral --shell /usr/sbin/nologin lateral
sudo install -d -o lateral -g lateral -m 700 /var/lib/lateral /var/lib/lateral/assets
```

Needs the .NET 9 runtime (or newer; the projects roll forward). Keep the data folder outside the install so an upgrade never
touches it, and private to the service account.

## Configure it

Settings come from `appsettings.json`, then `appsettings.<Environment>.json`, then environment variables, then the command
line; later wins. Write `:` as `__` in an environment variable. The service reads them once, at start-up. **Settings and
storage** in the console lists every one, with its value now, default, source and purpose (secrets show only as set or not set).

A minimal environment file, `/etc/lateral/lateral.env` (mode 600, owned by root):

```ini
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5080
AllowedHosts=cms.home.lan
Database__ConnectionString=Data Source=/var/lib/lateral/cms.db
Assets__Root=/var/lib/lateral/assets
Assets__MaxBytes=200000000000
Auth__Cms__Username=cms-ingest-svc
Auth__Cms__Passwords__0=<the output of: dotnet Lateral.Cms.dll admin new-service-secret>
Proxy__KnownProxies__0=127.0.0.1
```

- **Do not set `Auth__Users`.** That is for development: people defined there keep their password in clear text and cannot be
  changed while the service runs. In Production people live in the database.
- **`Auth:Cms`** is the machine account the CMS signs in with. It stays in configuration so it still works when everyone
  else is locked out. To rotate it, add a second GUID (`Auth__Cms__Passwords__1`), switch the CMS to it, then remove the first.
- `Assets:MaxBytes` is the most the store will hold; set it below the free space of the disk.
- Production refuses to start with `Admin:AllowReset` on.

Run as a service, `/etc/systemd/system/lateral.service`:

```ini
[Unit]
Description=Lateral CMS
After=network.target

[Service]
User=lateral
WorkingDirectory=/opt/lateral
EnvironmentFile=/etc/lateral/lateral.env
ExecStart=/usr/bin/dotnet /opt/lateral/Lateral.Cms.dll
Restart=on-failure
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths=/var/lib/lateral

[Install]
WantedBy=multi-user.target
```

`WorkingDirectory` matters: the service finds `appsettings.json` relative to it.

## Put HTTPS in front

The service speaks plain HTTP and Basic credentials travel with every request, so listen on the loopback address and let a
reverse proxy provide HTTPS. With Caddy:

```
cms.home.lan {
    reverse_proxy 127.0.0.1:5080
}
```

Name the proxy in `Proxy__KnownProxies` so the client address (which the sign-in throttle counts) is the real one, and
`X-Forwarded-Proto` is believed. For uploads of many gigabytes, raise or remove the proxy's own request-size and timeout limits.

## First sign-in

A fresh install has nobody. On first start it creates one administrator, `admin`, with a generated temporary password, writes
it to a file beside the database that only the service account can read, and **does not log it**:

```bash
sudo cat /var/lib/lateral/initial-admin-password.txt
```

Sign in with it (it can do nothing but change itself), choose your own password, and the file is deleted. Then add the people
who need access.

```bash
# the temporary password, then your own
curl -c jar -H 'Content-Type: application/json' -d '{"username":"admin","password":"<temporary>"}' https://cms.home.lan/api/auth/login
curl -b jar -H 'X-Requested-With: curl' -H 'Content-Type: application/json' \
     -d '{"current":"<temporary>","newPassword":"<a long new password>"}' https://cms.home.lan/api/auth/password
```

## People

- **Passwords** are 12 to 128 characters, not the person's name, not a common one. They are stored as PBKDF2-SHA256 with a
  random salt and 600000 iterations (`Auth:PasswordHashIterations`); nothing can show an existing password, only replace it.
- **Roles:** `User` reads; `Admin` manages people, settings, the queue and assets. The CMS account is separate and cannot sign
  in to a browser.
- **Sessions:** signing in gives an HttpOnly, SameSite=Strict cookie, good for 12 hours without use (`Auth:SessionHours`). A
  password change or reset, disabling the person, or "sign out everywhere" ends every session at once; a role change applies to
  an open session. Scripts can use Basic credentials instead.
- **Safeguards:** the last enabled administrator, and your own account, cannot be removed, disabled or demoted. Failed
  sign-ins are slowed per address and per username (`Auth:MaxFailuresPerMinute`). Every change is in the audit log.
- Manage people in the console (**People and sign-in**) or with `GET/POST /api/admin/users` and friends.

## Locked out? Reset a password over SSH

Whoever can log in to the machine can already read the database, so the same authority can reset any password. The tool
opens the same database with the same configuration as the service, works with the service running or stopped, and follows
the same rules.

The tool must see the same settings as the service (above all, the database path) and run as the account that owns the data.
`systemd-run` gives it exactly the service's user, working folder and environment file:

```bash
admin() {
  sudo systemd-run --pty --wait --collect -q -p User=lateral -p EnvironmentFile=/etc/lateral/lateral.env \
       -p WorkingDirectory=/opt/lateral /usr/bin/dotnet /opt/lateral/Lateral.Cms.dll admin "$@"
}
admin list-users
admin set-password admin
admin set-password admin --generate
admin create-user rescue --role admin --generate
```

(Not run on Linux; if `systemd-run` is not to your taste, run `dotnet Lateral.Cms.dll admin ...` from `/opt/lateral` as the
`lateral` user with the same environment variables set.)

`set-password` asks twice and shows nothing as you type; `--stdin` reads it from standard input; `--generate` prints a
temporary password once, which must be replaced at the next sign-in. Run it as the account that owns the database, with the
service's environment, so that it opens the same file. A change takes effect within `Auth:UserCacheSeconds` (5 seconds) on a
running service and signs the person out everywhere. Every change is audited as `cli:<operating-system user>`.

`dotnet Lateral.Cms.dll admin help` lists the rest (`set-role`, `disable-user`, `enable-user`, `revoke-sessions`,
`delete-user`, `new-service-secret`, `where`).

If the only enabled administrator is locked out, `set-password` on them (or `create-user` a new one) is the way back; the tool
will not delete or disable the last one.

## Back up

```bash
sqlite3 /var/lib/lateral/cms.db ".backup /backup/cms.db"
rsync -a /var/lib/lateral/assets/ /backup/assets/
```

The database backup is consistent while the service runs. Asset files never change once written, so a plain copy is enough.
The `keys` folder is optional (without it everyone signs in again). To restore: stop the service, put the files back, start it.

Remember what a backup is: it still holds what was later deleted. "Hard delete" covers the live database file only (see Known
limits in the README).

## Upgrade

```bash
sudo systemctl stop lateral
dotnet publish src/Lateral.Cms -c Release -o /opt/lateral
sudo systemctl start lateral
```

The database is migrated when the service starts. Back it up first.

## Things to know

- **The console is not part of this.** The photo library and engineering console are development tools: loopback only, fixed
  accounts from a settings file, and they refuse to run outside Development. They can be pointed at a real service
  (`Console__CmsBaseUrl`) for a look, but they are not a household-facing app, and they hold admin credentials.
- **One instance.** SQLite and one writer; no clustering.
- **The scanner is a mock.** Do not rely on it as malware protection.
- **Nothing here has been run behind a real reverse proxy.** Check the sign-in throttle sees real client addresses (it logs
  them) and that the cookie is marked Secure (it is when the request arrives as HTTPS, which `X-Forwarded-Proto` from a named
  proxy provides).
