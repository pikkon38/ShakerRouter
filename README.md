# Shaker Router

Routes Star Citizen's audio to your bass shakers through a second sound card (Sound Blaster X4). No VoiceMeeter. Your Windows default device and keyboard volume dial stay untouched.

## Download

Grab **ShakerRouter.exe** from the [latest release](https://github.com/pikkon38/ShakerRouter/releases/latest). It's a single self-contained exe. No installer and no .NET runtime needed.

Put it somewhere you can write to, like `%LOCALAPPDATA%\ShakerRouter\app`, and run it.

**Updates:** the app checks GitHub on startup. When a new release exists, an **Install** button appears at the top. Click it and the app downloads the new exe, swaps it in, and restarts. Settings carry over. You can also hit **Check for updates** at any time.

## Requirements

- Windows 11 (or Windows 10 build 20348+). Process loopback doesn't exist on older builds.
- Your shaker sound card (e.g. Sound Blaster X4) must NOT be the Windows default device. Keep your headset or speakers as the default.

## Releasing a new version

1. Bump `<Version>` in `ShakerRouter.csproj`.
2. Commit and push to `main`. GitHub Actions compiles every push. Check the Actions tab for a green build.
3. Tag and push: `git tag v0.3.0 && git push origin v0.3.0`.
4. Actions builds `ShakerRouter.exe` and publishes the release. Every running copy offers the update on its next launch.

## Building from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

Double-click **`Update.bat`**. It builds the source in this folder, installs it to `%LOCALAPPDATA%\ShakerRouter\app`, makes Start menu and Desktop shortcuts, and relaunches. If a build fails, the installed version is left alone and the output goes to `build-log.txt`.

Settings and routing profiles live in `%APPDATA%\ShakerRouter\settings.json`. Neither updates nor rebuilds touch them.

## First-time setup

1. **Pick the output.** Choose the Sound Blaster X4 under Shaker Output Device. Leave mode on Shared.
2. **Find your transducers.** Click **Test tone** on each column. You'll feel a pulsed 45 Hz thump on one transducer. Note which jack drives which shaker.
3. **Route.** Each matrix cell is a 0 to 100% fader for that game channel into that transducer. Press and drag left or right to change it (hold Shift for fine control). A plain click does nothing. Scroll for 5% steps, Shift+scroll for 1%. Right click turns a cell off and back on at its last level.
   The **Channel level** slider at the end of each row scales that game channel on every output.
4. **Tune.** Leave Bass only on. Set the cutoff (60 to 80 Hz is the sweet spot). Use gain up to 200% to drive weaker shakers harder.
5. **Play.** Launch Star Citizen. The status line changes to "Capturing StarCitizen.exe". It reattaches on its own when the game restarts.

Everything saves automatically to `%AppData%\ShakerRouter\settings.json`.

## How the profiles work

Star Citizen mixes for whatever your Windows default device is:

| Default device | What the game sends | Profile used |
|---|---|---|
| Stereo headset | FL, FR only | Stereo |
| 5.1 system | FL, FR, C, LFE, SL, SR | 5.1 |
| 7.1 device or virtual 7.1 | All 8 channels | 7.1 |

The app keeps a separate routing profile for each layout and switches automatically when you change your default device. Watch the meters next to each game channel. If a row never moves, the game isn't sending that channel.

Default routes (assuming jacks 1 to 3 are left, right, center):

- **7.1:** Left = FL + SL. Right = FR + SR. Center = RL + RR + LFE at 60 Hz.
- **5.1:** Left = FL + SL. Right = FR + SR. Center = LFE + 50% SL + 50% SR at 60 Hz.
- **Stereo:** Left = FL. Right = FR. Center = 50% of each at 60 Hz.

Click **Reset this profile** to go back to these.

**Headset tip:** Turn on Windows Sonic for Headphones (or your headset's virtual 7.1). The game then renders surround, and the shakers get directional cues. Set Game Layout to 7.1 manually if Auto shows Stereo.

## Troubleshooting

- **"Process loopback activation failed"**: You're on an older Windows build. Update Windows.
- **"Waiting for StarCitizen.exe"** while playing: Check the Game Process box says `StarCitizen`. Click Refresh.
- **Nothing on the rear rows on 5.1**: Expected. Windows 5.1 has one surround pair, and it lands on SL/SR.
- **Exclusive mode fails**: Use Shared. Latency difference is about 10 ms. You won't feel it.
- **Buzzing at high gain**: The soft clipper is engaging. Lower gain or the number of channels summed into that output.

## Architecture

```
StarCitizen.exe
   |  process loopback (Windows resamples to the X4 rate)
ProcessLoopbackCapture  -> normalizes to 8 slots: FL FR C LFE RL RR SL SR
   |
BufferedWaveProvider    -> drift trim keeps latency near 40 ms
   |
ChannelRouter           -> matrix sum, 24 dB/oct low pass, gain 0-200%, soft clip, test tone
   |
SampleToBytesProvider   -> float or int PCM for the device
   |
WasapiOut (X4)
```

| Folder | Contents |
|---|---|
| `Audio/Interop` | COM declarations for process loopback (NAudio doesn't support it) |
| `Audio` | Capture, router, engine, format helpers |
| `Audio/Dsp` | Biquad and Linkwitz-Riley low pass |
| `Services` | Device and process lists, settings, default profiles |
| `ViewModels` | MVVM layer |
