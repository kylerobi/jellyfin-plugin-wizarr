# Wizarr Invites for Jellyfin

Serves Wizarr's invite sign-up through Jellyfin, so **Wizarr never has to be exposed to the internet**.

```
Internet ──► Jellyfin (/invite/...) ──private Docker network──► Wizarr
```

- **Invite links:** `https://jellyfin.example.com/invite/j/ABC123`
- **Code entry page:** `https://jellyfin.example.com/invite`
- **Dashboard page:** Dashboard → Plugins → **Wizarr Invites**, where you can create, list, copy and delete invites.

Jellyfin only passes through Wizarr's invitee pages: the code page, the `/j/` links, the sign-up wizard, password reset and static files. Wizarr's admin panel, login, setup, settings and API are **not** reachable through `/invite`. Every other path returns 404. The dashboard page uses Wizarr's API from inside Jellyfin, and only Jellyfin admins can use it.

## 1. Run Wizarr on a private network with Jellyfin

```yaml
services:
  wizarr:
    container_name: wizarr
    image: ghcr.io/wizarrrr/wizarr:latest
    environment:
      - TRUST_PROXY_HEADERS=true   # lets Wizarr build its links under /invite
    volumes:
      - ./wizarr/database:/data/database
    networks: [media]
    restart: unless-stopped
    # No public port. To reach Wizarr's own admin UI from your LAN only, bind it to localhost or a LAN IP:
    # ports:
    #   - "127.0.0.1:5690:5690"

  jellyfin:
    # ...your existing jellyfin service...
    networks: [media]
    # Optional: supply the Wizarr key here instead of saving it in the plugin settings
    # environment:
    #   - WIZARR_API_KEY=${WIZARR_API_KEY}

networks:
  media:
```

Remove any reverse-proxy entry or router port-forward you had for Wizarr.

In Wizarr's admin UI (reached from your LAN, or temporarily through an SSH tunnel):
1. Add your Jellyfin server with URL `http://jellyfin:8096` and a Jellyfin API key.
2. Set that server's **External URL** to your public Jellyfin address, so the sign-up wizard shows new users the right address.
3. Create an API key under **Settings → API Keys**.

## 2. Build

Needs only Docker:

```sh
./build.sh
```

This produces `dist/Jellyfin.Plugin.Wizarr.dll`.

Targets **Jellyfin 10.11** (.NET 9). For 10.10, edit the `.csproj`: change `net9.0` to `net8.0` and change `10.11.*` to `10.10.*`. Then rebuild with the `sdk:8.0` image in `build.sh`.

## 3. Install

```sh
mkdir -p /path/to/jellyfin/config/plugins/WizarrInvites_1.0.0.0
cp dist/Jellyfin.Plugin.Wizarr.dll /path/to/jellyfin/config/plugins/WizarrInvites_1.0.0.0/
docker restart jellyfin
```

If you installed the earlier version of this plugin, delete its folder first.

## 4. Configure

Dashboard → Plugins → **Wizarr Invites**:

| Setting | Example |
|---|---|
| Wizarr internal URL | `http://wizarr:5690` |
| Jellyfin public URL (optional) | `https://jellyfin.example.com`, used for share links |
| Wizarr API key | from Wizarr → Settings → API Keys (or leave blank and use `WIZARR_API_KEY`) |

Press **Save**, then **Test connection**. Then open `https://jellyfin.example.com/invite` from outside your network to check the code page.

## 5. (Optional) Link it from the Jellyfin login screen

Dashboard → General → Branding → **Login disclaimer** accepts HTML:

```html
<a href="/invite" style="color:#00a4dc">Have an invite code? Join here</a>
```

If Jellyfin uses a base URL (e.g. `/jellyfin`), use `/jellyfin/invite`.

## Endpoints

| Path | Access | Purpose |
|---|---|---|
| `/invite` | public | Wizarr's "enter invite code" page |
| `/invite/j/{code}` | public | Invite link, then Wizarr's sign-up wizard |
| `/invite/wizard/…`, `/invite/join`, `/invite/static/…`, `/invite/reset/…` | public | Rest of the invitee flow |
| `/InviteAdmin/Status`, `Servers`, `Libraries` | Jellyfin admin | Dashboard page |
| `GET/POST /InviteAdmin/Invitations`, `DELETE /InviteAdmin/Invitations/{id}` | Jellyfin admin | List / create / delete |

## How it works

Jellyfin forwards each allowed request to Wizarr and adds `X-Forwarded-Host`, `X-Forwarded-Proto`, `X-Forwarded-For` and `X-Forwarded-Prefix: /invite` headers. With `TRUST_PROXY_HEADERS=true`, Wizarr then builds all its links under `/invite` and rate-limits by the visitor's real IP. As a safety net, the plugin also:
- rewrites any root-relative links Wizarr hard-codes,
- rewrites redirect `Location` headers,
- scopes Wizarr's cookies to `/invite`.

Jellyfin login tokens are never forwarded to Wizarr.

## Troubleshooting

- **"The invite service is unavailable"**: Jellyfin can't reach Wizarr. Check that both containers share a network: `docker exec jellyfin curl -s http://wizarr:5690/health`.
- **Pages load without styling, or links jump to `/wizard` without `/invite`**: `TRUST_PROXY_HEADERS=true` is missing on the Wizarr container.
- **Clicking "Sign in"/"Admin" on a Wizarr page gives 404**: this is expected. Admin pages are blocked from the internet on purpose.
- **401 in the dashboard page**: the API key is wrong or was deleted.
- **Plugin doesn't appear**: check the Jellyfin logs. Usually this means the build doesn't match your Jellyfin version (see Build).
