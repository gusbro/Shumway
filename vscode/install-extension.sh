#!/bin/sh
# ADR-036 — builds shumway-dap, packages the extension as a real .vsix (a folder copied
# into ~/.vscode/extensions is IGNORED by modern VS Code), installs via the code CLI.
#
#   sh vscode/install-extension.sh
#
# After it, RESTART VS Code and open a .pl file.
set -e
repo="$(cd "$(dirname "$0")/.." && pwd)"
ext="$repo/vscode/shumway-debug"
out="$repo/vscode/shumway-debug-0.1.4.vsix"

echo '[1/4] publishing shumway-dap (Release)...'
dotnet publish "$repo/src/Shumway.Dap" -c Release -v:q --nologo
publish="$repo/src/Shumway.Dap/bin/Release/net10.0/publish"
[ -f "$publish/shumway-dap" ] || { echo "no adapter at $publish"; exit 1; }

echo '[2/4] staging the adapter into the extension...'
rm -rf "$ext/bin"
mkdir -p "$ext/bin"
cp -r "$publish/." "$ext/bin/"

echo '[3/4] packaging the .vsix...'
stage="$(mktemp -d)"
mkdir -p "$stage/extension"
cp "$ext/extension.vsixmanifest" "$stage/"
cat > "$stage/[Content_Types].xml" << 'EOF'
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="json" ContentType="application/json"/>
  <Default Extension="vsixmanifest" ContentType="text/xml"/>
  <Default Extension="md" ContentType="text/markdown"/>
  <Default Extension="exe" ContentType="application/octet-stream"/>
  <Default Extension="dll" ContentType="application/octet-stream"/>
  <Default Extension="pdb" ContentType="application/octet-stream"/>
</Types>
EOF
for f in "$ext"/*; do
    [ "$(basename "$f")" = extension.vsixmanifest ] && continue
    cp -r "$f" "$stage/extension/"
done
rm -f "$out"
(cd "$stage" && zip -qr "$out" .)
rm -rf "$stage"
echo "packaged: $out"

echo '[4/4] installing with the code CLI...'
if command -v code > /dev/null 2>&1; then
    code --install-extension "$out" --force
    echo 'installed. RESTART VS Code, open a .pl file, and press F5.'
else
    echo 'the `code` CLI is not on PATH. In VS Code: Ctrl+Shift+P ->'
    echo "  'Extensions: Install from VSIX...' and pick $out"
fi
