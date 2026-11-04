# #255 Noctis Plus phone app variant (2026-11-04)

Branch `android-plus-app` (based on `android-plus`). Goal: the Plus phone app installs
next to heartached's Noctis beta instead of replacing it.

## What changed

| File | Change |
|---|---|
| `src/Noctis.Android/Noctis.Android.csproj` | New `NoctisPlus` property, **default `true` in this fork**. `true` -> `ApplicationId` `com.alfamin.noctisplus` + compile constant `NOCTIS_PLUS`; `false` -> `com.heartached.noctis` (unchanged original). |
| `src/Noctis.Android/AppVariant.cs` (new) | `AppVariant.Label` = "Noctis Plus" / "Noctis" from `NOCTIS_PLUS`. |
| `src/Noctis.Android/MainActivity.cs` | Activity (launcher) `Label = AppVariant.Label`. |
| `src/Noctis.Android/NoctisApplication.cs` | `[Application(Label = AppVariant.Label)]` (app info / settings name). |
| `src/Noctis.Android/Properties/AndroidManifest.xml` | `android:label="Noctis"` removed from `<application>` (the attribute above now sets it, so it is not set twice). |
| `.github/workflows/noctis-plus-android.yml` (new) | Release APK build + artifact, see below. |

Why "Plus is the default" instead of an opt-in switch: same number of edits either way, but
with Plus as default every build in this fork (owner's local builds, the existing `android`
compile job, scripts/android/build-release.ps1) produces the side-by-side app without anyone
having to remember a flag. heartached's app is still one flag away.

### Package-name audit (grep `com.heartached.noctis` / `heartached` in src/Noctis.Android, src/Noctis.Mobile)

- `ApplicationId` in the csproj: switched (above).
- `NoctisPlaybackService` `[Service(Name = "com.heartached.noctis.PlaybackService")]`: this is
  the **Java class name**, not an authority. Components are scoped per package, so two apps
  each having this class do not clash. Left unchanged on purpose (renaming it would only add
  merge friction with upstream). Its intent filter is the standard Media3
  `MediaSessionService` action, which every media app declares.
- `SettingsPageViewModel.ReportProblemUrl` points at heartached/Noctis issues: a URL, no
  clash. (Could point to Alfamin/NoctisPlus later; not changed here.)
- No content/file providers, no custom authorities, no custom notification channel ids
  in our code: Media3's notification channel is per app, AndroidX startup's provider uses
  `${applicationId}` automatically. Nothing else hard-codes the package; the code reads
  `context.PackageName` at run time.
- Phone<->PC protocol: untouched.
- Icon: same icon as heartached's (could get a Plus badge later).

## How to build each variant

Plus (default):

```powershell
dotnet build src/Noctis.Android/Noctis.Android.csproj -c Debug
```

heartached's original:

```powershell
dotnet build src/Noctis.Android/Noctis.Android.csproj -c Debug -p:NoctisPlus=false
```

Release APK like CI (debug key):

```powershell
dotnet publish src/Noctis.Android/Noctis.Android.csproj -c Release -p:AndroidPackageFormat=apk -p:AndroidPackageFormats=apk
```

(Local builds need the Android SDK + JDK paths from the csproj header; this PC has none yet.)

## CI: `.github/workflows/noctis-plus-android.yml`

- Runs on `workflow_dispatch` and on push to `android-plus*` branches, ubuntu-latest.
- Same SHA pins as the `android` job in `dotnet.yml` (checkout v4.4.0, setup-dotnet v4.3.1,
  upload-artifact v4.6.2), .NET 10, `dotnet workload restore`.
- `dotnet publish -c Release -p:NoctisPlus=true` as a single universal APK (arm64 + x64),
  `ApplicationVersion` = run number (so each CI APK can update the previous one when
  signed with the same key).
- Artifact `noctis-plus-android-apk`, file `NoctisPlus-<upload|debug>-<run>.apk`, kept 14 days.
- Signing: if all four secrets exist it signs with them, otherwise the debug key, and the
  job summary says which.

### Signing secrets (repo Settings > Secrets and variables > Actions)

| Secret | Value |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | the keystore file, base64 |
| `ANDROID_KEYSTORE_PASSWORD` | keystore password |
| `ANDROID_KEY_ALIAS` | key alias (e.g. `noctisplus-upload`) |
| `ANDROID_KEY_PASSWORD` | key password (same as the keystore password for PKCS12) |

Owner makes the keystore once (keytool ships with Android Studio's JDK; it asks for the
password itself):

```powershell
& 'C:\Program Files\Android\Android Studio\jbr\bin\keytool.exe' -genkeypair -v -storetype PKCS12 -keystore "$env:USERPROFILE\.noctis\noctisplus-upload.jks" -alias noctisplus-upload -keyalg RSA -keysize 4096 -validity 10000
[Convert]::ToBase64String([IO.File]::ReadAllBytes("$env:USERPROFILE\.noctis\noctisplus-upload.jks")) | Set-Clipboard
```

Paste the clipboard into `ANDROID_KEYSTORE_BASE64`. Back the .jks up somewhere safe: an
app signed with it can only be updated by APKs signed with the same key. Or use
`scripts/android/create-upload-key.ps1 -Keystore ... -Alias noctisplus-upload`.
Important: a debug-signed CI APK and an upload-key APK cannot update each other
(uninstall first when switching).

## CI run result

Run 41889351938 on 2d571891: **success** in about 4 minutes, all steps green, signed with the
**debug key** (no ANDROID_* secrets set yet). Artifact `noctis-plus-android-apk`, 69.4 MB (zip).
https://github.com/Alfamin/NoctisPlus/actions/runs/41889351938

Not yet checked: installing the APK on the phone next to heartached's beta (package name and
"Noctis Plus" label on the home screen), and the upload-key signing path (needs the secrets).
The existing `android` job in dotnet.yml only runs on main/cross-platform, so it was not run here.
