# The app contract

What a Juggle app can rely on: how it's installed and run, what it may read and write, the hardware it may use, and
what it may ask the OS for. From v1.0 this is what Juggle OS's version number protects. Everything not written here
(the card layout, the A/B slots, `os-update`, the setup page, the login screens) may change in any release.

**Status: draft for v1.0.** The changes it needed are made for the next release (see Before v1); one switch is left
for v1.0 itself.

## Versions

Juggle OS is versioned `MAJOR.MINOR.PATCH`, and the contract goes with it:

- **Major**: something an app relies on changes or goes away (a request, a path, a manifest setting, the Python
  major version, a library). Apps may need changing.
- **Minor**: additions only: a new request, a new manifest setting, a new field in a request's output, a new
  library, a new group for the app user. An app made for 1.0 runs unchanged on every 1.x.
- **Patch**: fixes, with no change an app can see except the fix.

An app says which contract it needs in its manifest (`"juggle": "1.2"`: Juggle OS 1.2 or a later 1.x), and is told
the one the OS gives in `JUGGLE` (`"1.2"`), the version of the last release that added to the contract: a release
that adds nothing for apps leaves it as it was. An app that uses something added in 1.2 says 1.2, so an older OS
refuses it with a clear message ("needs Juggle 1.2 or later") instead of starting it and failing later.

Rules for both sides, so additions never break anything:
- The OS never removes a request, a manifest setting or an output field within a major version, and never changes
  what one means.
- Apps ignore fields they don't know in JSON from the OS, and check the OS version (not the presence of a file or
  some text) to know whether something is there.
- The OS refuses a manifest with settings it doesn't know. A setting added in 1.x is only used by apps that say
  1.x, which the version check catches first.

## The manifest: `app.json`

In the app's folder, beside `main.py`:

```json
{"juggle": "1.0", "name": "hello", "version": "1.0.0", "root": false, "pwm": false}
```

| Setting | Required | Meaning |
|---|---|---|
| `juggle` | yes (from v1.0) | The contract the app needs, `"MAJOR.MINOR"`. Refused if the OS's major differs, or its minor is lower. |
| `name` | yes | Lowercase letters, digits and hyphens, 1 to 32, not starting or ending with a hyphen. Installing an app with another name starts it with empty state. |
| `version` | yes | Letters, digits and `.` `_` `+` `-`, up to 32. Shown on the login screens and to the app's own update. |
| `root` | no (false) | Run as root, for libraries that need `/dev/mem` (`rpi_ws281x`). Otherwise the app runs as the `app` user. |
| `pwm` | no (false) | The app uses the PWM hardware (WS2812 LEDs on GPIO 18). The onboard audio uses it too, so its driver is unloaded while the app runs. |

A manifest that's missing, isn't valid, or asks for a contract the OS doesn't meet: the app isn't started (exit
status 78, so it isn't restarted either) and the login screens say why.

## Files

| Path | What it is | Who writes it |
|---|---|---|
| `/data/app/` | The app: `main.py`, `app.json`, its pure-Python libraries in `lib/`, its web files and anything else it ships | Installing and updating only. Read-only to the app. |
| `/data/app-state/` | Everything the app writes: settings, history, caches. Kept across app updates and OS updates; emptied when an app with another name is installed, or the app is removed. | The app |
| `/data/app-state/update.tar` | A new version of the app, for `app-request update` | The app |
| `/data/app-state/update-result.json` | How the last update went: `{"version", "success", "message", "time"}` (`time` in Unix seconds) | The OS |
| `/tmp` | Scratch space in RAM, gone at every start | The app |

The root file system is read-only, and writes anywhere else are lost at the next start (they go to RAM). The data
partition has the rest of the card, less the OS's own settings.

## How the app runs

- **Start**: `python3 main.py` in `/data/app`, by the `juggle-app` service, once the data partition is mounted and
  the settings applied. Not in setup mode (the setup page has port 80).
- **User**: `app` (uid and gid 800), in the groups `gpio`, `i2c`, `spi`, `video`, `input` and `dialout`, and allowed
  to listen on ports below 1024. `root: true` runs it as root instead.
- **Environment**: `PYTHONPATH=/data/app/lib`, `PYTHONUNBUFFERED=1`, `PYTHONDONTWRITEBYTECODE=1`,
  `JUGGLE_STATE=/data/app-state`, `HOME=/data/app-state`, and `JUGGLE`, the contract the OS gives (`"1.0"`).
- **Its web page** is on port 80, at `http://<name>.local`, by convention (`juggle` and App Base expect it there).
  Nothing else on the Pi listens on 80. The app may listen on other ports too, on IPv4 and IPv6.
- **Restarts**: whenever it stops, after 5 seconds, however often. Exit status 78 means "don't restart me" (a
  configuration the app can't run with): the service stays stopped until the next start or install.
- **Stopping**: `SIGTERM`, then `SIGKILL` 90 seconds later.
- **Watchdog**: optional. The app asks for it itself with `sd_notify` (`WATCHDOG_USEC=` then `WATCHDOG=1` while
  it's well, as App Base does), and is restarted if it stops sending. `NotifyAccess=main`: only the main process may
  send.
- **Logs**: standard output and error go to the journal, which is in RAM (gone at each start).
- **Network**: outgoing connections are allowed. The Pi has no firewall; nothing else listens but SSH and mDNS.

## The runtime

- **Python 3.13 or later** (Debian's `python3`), with its standard library. A minor release may move to a newer
  Python 3 once apps are checked on it; Python 4, or dropping a standard module apps use, would be a major.
- **Libraries in the image** (compiled, so apps needn't compile anything): `rpi_ws281x` 5.0. Adding libraries is a
  minor; removing one, or a change to one that breaks its users, is a major.
- An app's pure-Python libraries (Microdot, App Base) come in its own `lib/`.
- 32-bit ARM (armhf), for the Pi 2 and later. Pure Python doesn't notice.

## Hardware

What the app user may open without root: GPIO (`/dev/gpiochip*`, `/dev/gpiomem`), I2C (`/dev/i2c-*`), SPI
(`/dev/spidev*`), USB serial ports (`/dev/ttyUSB*`, `/dev/ttyACM*`), the display (`/dev/dri`, `/dev/fb*`) and
input devices (touch, keyboards: `/dev/input/*`). I2C and SPI are off unless switched on in `config.txt` *(see Later)*.

## Requests: `app-request`

The app may run `sudo -n /usr/lib/juggle/app-request <request> [arguments]` and nothing else as root. Each request
is checked by the OS.

- Exit status 0: done, with any answer on standard output (JSON where it says so).
- Exit status 1: refused or failed, with the reason (one line, for people) on standard error.
- Exit status 2: a request this OS doesn't know, or the wrong arguments.

| Request | Since | What it does | Answer |
|---|---|---|---|
| `update` | 0.4 | Installs `/data/app-state/update.tar` (a `.tar` of the app's folder, up to 64 MiB) as a new version, on trial: in the background, the app is restarted with it, and the old version goes back if the new one doesn't keep running for 30 seconds. | `update-result.json` afterwards |
| `time-zone <zone>` | 0.4 | Sets the time zone (an IANA name, such as `Europe/London`), at once and at every start | |
| `ntp <server>` | 0.4 | Sets the time server (`""` for the default) | |
| `restart` | 0.4 | Restarts the Pi | |
| `settings` | 0.5 | The Pi's settings | `{"time_zone", "ntp", "wifi_fallback": {"enabled", "after_minutes", "for_minutes"}, "wifi_ssid"}` (`wifi_ssid` null on Ethernet) |
| `wifi-fallback <on\|off> <after> <for>` | 0.5 | Whether the Pi starts setup mode when it has no network, after how many minutes (1 to 1440) and for how many (5 to 120), from the next start | |
| `setup` | 0.5 | Restarts into setup mode, keeping the settings until new ones are saved there | |
| `factory-reset` | 0.5 | Forgets the Pi's settings (name, Wi-Fi, SSH keys, time zone) and restarts into setup mode. The app and its state stay. | |
| `wifi <ssid> [<country>]` | 0.6 | A new Wi-Fi network from the next start, the password on standard input (one line). On trial: if it doesn't connect within 5 minutes, the network before goes back. The app asks for the restart. | |
| `networks` | 0.6 | The Wi-Fi networks in range | a JSON list of names, strongest first |

## Installing and removing (for deploy tools)

Over SSH as the admin user `juggle`, with a `.tar` of the app's folder:

- `sudo /usr/lib/juggle/app install <file.tar>`: replaces the app, starts it, and fails (exit status 1, with the
  app's output) if it doesn't keep running for 10 seconds.
- `sudo /usr/lib/juggle/app remove`: stops the app and removes it and its state.

## Not in the contract

These are the OS's own, and may change in any release: the partitions and slots, `/data/settings`, `juggle.json`
(that's the `juggle` tool's, released with the OS), `os-update`, the setup page, the login screens, the boot
splash, which services run, the packages beyond Python and the libraries above, and the admin user's sudo.

## Before v1

Changes made before the contract is frozen, because they can't be added later without breaking something:

1. **`juggle` in `app.json`**, checked as above (the `app` command's `check_juggle`). Without it, an OS can't tell
   an app made for a later 1.x from one it can run, and the first app to use something new would fail at runtime
   instead of being refused. The check comes before the one for unknown settings, so an app made for a later 1.x
   gets "update the OS", not "unknown setting".
2. **`JUGGLE` in the app's environment**, so an app checks a version to know what it can ask for. App Base used to
   read `app-request`'s source for a request's name, which made the text of a script part of the contract.
3. **App Base uses `JUGGLE`** for `os_requests`, `os_setup` and `os_wifi`, falling back to the old check on an OS
   from before it. App Base also runs on Picos, with about 190 KB of heap and 640 KB of flash on the smallest, so
   the check is in `base/pi.py`, which a Pico's deploy leaves out: shared code doesn't change.
4. **Exit status 2 for an unknown request**, kept as it was, written down (above) so App Base can tell "not on this
   OS" from "refused".

Left for v1.0 itself: `JUGGLE_REQUIRED` in the `app` command becomes true, so an `app.json` without `juggle` is
refused. Until then it's taken, so apps from before it keep running while they gain it (an OS before this one
refuses `juggle` as a setting it doesn't know, so an app adds it only once its Pis have this OS).

## Later (minor releases)

Additions that fit the rules above, so they can wait for an app that needs them:

- **Audio playback** (the onboard 3.5 mm jack, and USB audio adapters): the `audio` group for the app user, a
  default ALSA device (a USB adapter if there is one, else the jack), and a playback library if `aplay` isn't
  enough. With `pwm`, only USB audio. Neither official touch display has a speaker or buzzer; a piezo buzzer on a
  GPIO pin needs nothing new.
- **The display** (the official 7" Touch Display on the Pi 2, by DSI): a manifest setting (`"display": true`) that
  gives the screen to the app (no login screen or splash on it), the backlight's brightness and power for the app
  user, the touch screen as an input device, and SDL2 and pygame in the image for drawing straight to the screen
  (KMS) with no desktop. The overlay and rotation in `config.txt` need checking on the display when it arrives.
- **I2C and SPI** switched on for apps that say so (a manifest setting; `config.txt` is read at boot, so the first
  start after installing restarts the Pi).
- **More requests**: switching the Pi off (`poweroff`, for a Pi with a screen people can unplug safely afterwards),
  the display's rotation, and the app's own mDNS service (`_http._tcp`) so it can be found as a web page.
- **More libraries**, as apps need them (a camera, sensors with compiled drivers), each pinned to its checksum as
  `rpi_ws281x` is.
