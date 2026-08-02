#!/bin/bash

Arch="$1"
OutputPath="$2"
Version="$3"

# The .app bundle must be named after the binary the publish actually produces.
# v2rayN.Desktop.csproj sets <AssemblyName>departament</AssemblyName>, so the host is
# `departament`, not `v2rayN`. This script kept describing the upstream name, which meant
# CFBundleExecutable pointed at a file that is not in Contents/MacOS — macOS then refuses to
# launch the bundle, and it does so SILENTLY (unlike the .deb/.rpm launcher, which at least
# printed an error). AppName is resolved from what is on disk so the script still works if it
# is ever pointed at an upstream publish.
AppName="departament"
if [[ ! -f "$OutputPath/$AppName" && -f "$OutputPath/v2rayN" ]]; then
  AppName="v2rayN"
fi

# The core binaries (Xray, sing-box, geo assets) genuinely come from upstream's core-bin repo.
# That name is upstream's on purpose and is not ours to rebrand.
FileName="v2rayN-${Arch}.zip"
wget -nv -O $FileName "https://github.com/2dust/v2rayN-core-bin/raw/refs/heads/master/$FileName"
7z x $FileName
cp -rf v2rayN-${Arch}/* $OutputPath

PackagePath="departament-Package-${Arch}"
BundlePath="$PackagePath/${AppName}.app"
mkdir -p "$BundlePath/Contents/Resources"
cp -rf "$OutputPath" "$BundlePath/Contents/MacOS"

# The icon ships under whichever name the publish emitted; take the first .icns we find rather
# than assuming, so a rename upstream or here does not silently produce an icon-less bundle.
Icns="$(find "$BundlePath/Contents/MacOS" -maxdepth 1 -name '*.icns' | head -n1)"
if [[ -n "$Icns" ]]; then
  cp -f "$Icns" "$BundlePath/Contents/Resources/AppIcon.icns"
else
  echo "package-osx: no .icns in the publish output; the bundle will use the system default icon" >&2
fi

echo "When this file exists, app will not store configs under this folder" > "$BundlePath/Contents/MacOS/NotStoreConfigHere.txt"
chmod +x "$BundlePath/Contents/MacOS/${AppName}"

cat >"$BundlePath/Contents/Info.plist" <<-EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>ru</string>
  <key>CFBundleLocalizations</key>
  <array>
    <string>ru</string>
    <string>en</string>
  </array>
  <key>CFBundleDisplayName</key>
  <string>departament</string>
  <key>CFBundleExecutable</key>
  <string>${AppName}</string>
  <key>CFBundleIconFile</key>
  <string>AppIcon</string>
  <key>CFBundleIconName</key>
  <string>AppIcon</string>
  <key>CFBundleIdentifier</key>
  <string>com.departamentvpn.desktop</string>
  <key>CFBundleName</key>
  <string>departament</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${Version}</string>
  <key>CSResourcesFileMapped</key>
  <true/>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>LSMinimumSystemVersion</key>
  <string>13.7</string>
</dict>
</plist>
EOF

create-dmg \
    --volname "departament Installer" \
    --window-size 700 420 \
    --icon-size 100 \
    --icon "${AppName}.app" 160 185 \
    --hide-extension "${AppName}.app" \
    --app-drop-link 500 185 \
    "departament-${Arch}.dmg" \
    "$BundlePath"
