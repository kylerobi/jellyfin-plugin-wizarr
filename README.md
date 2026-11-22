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

## 2. Get the plugin

**Download:** grab `wizarr-invites_x.y.z.0.zip` from the [Releases](../../releases) page. Every push also builds a zip, which you can find under the **Actions** tab → latest run → *Artifacts*.

To publish a new release, push a version tag:

```sh
git tag v1.0.1 && git push origin v1.0.1
```

**Or build it yourself**, which needs only Docker:

```sh
./build.sh
```

This produces `dist/Jellyfin.Plugin.Wizarr.dll`.

Targets **Jellyfin 10.11** (.NET 9). For 10.10, edit the `.csproj`: change `net9.0` to `net8.0` and change `10.11.*` to `10.10.*`. Then rebuild with the `sdk:8.0` image in `build.sh`.

## 3. Install

From a release zip (it already contains the `WizarrInvites_x.y.z.0` folder):

```sh
unzip wizarr-invites_*.zip -d /path/to/jellyfin/config/plugins/
docker restart jellyfin
```

From your own build:

```sh
mkdir -p /path/to/jellyfin/config/plugins/WizarrInvites_1.0.0.0
cp dist/Jellyfin.Plugin.Wizarr.dll /path/to/jellyfin/config/plugins/WizarrInvites_1.0.0.0/
docker restart jellyfin
```

Keep only one `WizarrInvites_*` folder. Delete the old one when you upgrade.

## 4. Configure

Dashboard → Plugins → **Wizarr Invites**:

| Setting | Example |
|---|---|
| Wizarr internal URL | `http://wizarr:5690` |
| Jellyfin public URL (optional) | `https://jellyfin.example.com`, used for share links |
| Separate invite address (optional, recommended) | `https://invite.example.com`, see below |
| Wizarr API key | from Wizarr → Settings → API Keys. **Recommended:** leave blank and set `WIZARR_API_KEY` on the Jellyfin container, so the key isn't stored in Jellyfin's config files |

Press **Save**, then **Test connection**. Then open `https://jellyfin.example.com/invite` from outside your network to check the code page.

### Recommended: a separate invite address

By default Wizarr's pages are served on your Jellyfin address, so they share a browser origin with the Jellyfin web app, which keeps users' logins in browser storage. A bug in Wizarr's public pages could then reach those logins.

To isolate them, point a second hostname (e.g. `invite.example.com`) at the same Jellyfin server and enter it as **Separate invite address**. The plugin then serves `/invite` only on that hostname (other hosts get a 404 that names the right address), share links use it, and Wizarr builds its links from it.

- Your reverse proxy must pass the original `Host` header to Jellyfin (nginx: `proxy_set_header Host $host;`; Caddy and Traefik do this by default).
- Don't sign in to Jellyfin on the invite address. The isolation comes from no Jellyfin logins being stored for that origin.

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

Jellyfin forwards each allowed request to Wizarr and adds `X-Forwarded-Host`, `X-Forwarded-Proto`, `X-Forwarded-For` and `X-Forwarded-Prefix: /invite` headers. The host and scheme come from the configured invite or Jellyfin public address when one is set, not from the visitor's `Host` header. With `TRUST_PROXY_HEADERS=true`, Wizarr then builds all its links under `/invite` and rate-limits by the visitor's real IP. As a safety net, the plugin also:
- rewrites any root-relative links Wizarr hard-codes,
- rewrites redirect `Location` headers,
- scopes Wizarr's cookies to `/invite` and renames them with a `wizarr-` prefix; only those cookies are sent back to Wizarr, so cookies set by other apps on your domain never reach it,
- passes only an approved list of Wizarr's response headers (so Wizarr can't set HSTS, CORS or `Clear-Site-Data` on your Jellyfin address), and adds `X-Content-Type-Options: nosniff` and `Referrer-Policy: same-origin` if Wizarr doesn't,
- refuses paths with percent-encoding, `.`/`..` segments or empty segments, and blocks Wizarr's admin-only `/wizard/bundle-preview`.

The saved API key is never sent to the browser; the dashboard only sees whether one is saved.

Jellyfin login tokens are never forwarded to Wizarr.

## Troubleshooting

- **"The invite service is unavailable"**: Jellyfin can't reach Wizarr. Check that both containers share a network: `docker exec jellyfin curl -s http://wizarr:5690/health`.
- **Pages load without styling, or links jump to `/wizard` without `/invite`**: `TRUST_PROXY_HEADERS=true` is missing on the Wizarr container.
- **Clicking "Sign in"/"Admin" on a Wizarr page gives 404**: this is expected. Admin pages are blocked from the internet on purpose.
- **401 in the dashboard page**: the API key is wrong or was deleted.
- **Plugin doesn't appear**: check the Jellyfin logs. Usually this means the build doesn't match your Jellyfin version (see Build).
