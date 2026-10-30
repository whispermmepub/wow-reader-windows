#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS="$ROOT/artifacts/offline-installer"
PUBLISH="$ARTIFACTS/publish"
INPUT="$ARTIFACTS/input"
RUNTIME="$INPUT/MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
INSTALLER="$ARTIFACTS/WoWReader-Windows-Offline-Setup-x64.exe"
OFFICIAL_FWLINK='https://go.microsoft.com/fwlink/p/?LinkId=2124701'
MICROSOFT_ROOT_DER="$INPUT/MicrosoftRootCA2011.crt"
MICROSOFT_ROOT_PEM="$INPUT/MicrosoftRootCA2011.pem"
SIGNER_CRL_DER="$INPUT/CodeSigningPCA2024.crl"
SIGNER_CRL_PEM="$INPUT/CodeSigningPCA2024.crl.pem"
TSA_CERT_DER="$INPUT/PublicRSATimestampingCA2020.crt"
TSA_CERT_PEM="$INPUT/PublicRSATimestampingCA2020.pem"
TSA_ROOT_DER="$INPUT/IdentityVerificationRoot2020.crt"
TSA_ROOT_PEM="$INPUT/IdentityVerificationRoot2020.pem"
TSA_CRL_DER="$INPUT/PublicRSATimestampingCA2020.crl"
TSA_CRL_PEM="$INPUT/PublicRSATimestampingCA2020.crl.pem"
TSA_ROOT_CRL_DER="$INPUT/IdentityVerificationRoot2020.crl"
TSA_ROOT_CRL_PEM="$INPUT/IdentityVerificationRoot2020.crl.pem"

command -v dotnet >/dev/null || { echo 'dotnet SDK 10 is required.' >&2; exit 1; }
command -v makensis >/dev/null || { echo 'NSIS (makensis) is required.' >&2; exit 1; }
mkdir -p "$INPUT"
rm -rf "$PUBLISH"
mkdir -p "$PUBLISH"

echo 'Publishing the current working tree as self-contained win-x64...'
dotnet publish "$ROOT/src/WowReader.Windows/WowReader.Windows.csproj" \
  --configuration Release --runtime win-x64 --self-contained true \
  -p:EnableWindowsTargeting=true -p:DebugType=None -p:DebugSymbols=false \
  --output "$PUBLISH"

echo 'Resolving Microsoft’s official Evergreen Standalone Runtime (x64) link...'
RESOLVED_URL="$(curl --fail --silent --show-error --location --head --output /dev/null --write-out '%{url_effective}' "$OFFICIAL_FWLINK")"
EXPECTED_SIZE="$(curl --fail --silent --show-error --location --head --dump-header - --output /dev/null "$RESOLVED_URL" | tr -d '\r' | awk 'tolower($1) == "content-length:" { size=$2 } END { print size }')"
if [[ ! "$EXPECTED_SIZE" =~ ^[0-9]+$ ]]; then
  echo 'Could not determine the official runtime download size; refusing to package an unverified file.' >&2
  exit 1
fi
CURRENT_SIZE=0
if [[ -s "$RUNTIME" ]]; then CURRENT_SIZE="$(stat -c '%s' "$RUNTIME")"; fi
if [[ "$CURRENT_SIZE" != "$EXPECTED_SIZE" || -e "$RUNTIME.aria2" ]]; then
  rm -f "$RUNTIME" "$RUNTIME.aria2"
  if command -v aria2c >/dev/null; then
    aria2c --console-log-level=notice --summary-interval=15 \
      --max-connection-per-server=16 --split=16 --min-split-size=4M \
      --allow-overwrite=true --auto-file-renaming=false --file-allocation=none \
      --dir="$INPUT" --out="$(basename "$RUNTIME")" "$RESOLVED_URL"
  else
    curl --fail --location --retry 3 --retry-delay 2 --progress-bar \
      "$OFFICIAL_FWLINK" --output "$RUNTIME"
  fi
fi
if [[ "$(stat -c '%s' "$RUNTIME")" != "$EXPECTED_SIZE" || -e "$RUNTIME.aria2" ]]; then
  echo 'The runtime file size does not match Microsoft’s Content-Length; refusing to package a partial file or online bootstrapper.' >&2
  exit 1
fi

command -v osslsigncode >/dev/null || { echo 'osslsigncode is required for Authenticode verification.' >&2; exit 1; }
command -v openssl >/dev/null || { echo 'OpenSSL is required for Microsoft certificate verification.' >&2; exit 1; }
echo 'Downloading Microsoft-published signing roots and revocation lists...'
curl --fail --location --retry 3 --silent --show-error 'https://www.microsoft.com/pki/certs/MicRooCerAut2011_2011_03_22.crt' --output "$MICROSOFT_ROOT_DER"
curl --fail --location --retry 3 --silent --show-error 'https://www.microsoft.com/pkiops/crl/Microsoft%20Code%20Signing%20PCA%202024.crl' --output "$SIGNER_CRL_DER"
curl --fail --location --retry 3 --silent --show-error 'https://www.microsoft.com/pkiops/certs/Microsoft%20Public%20RSA%20Timestamping%20CA%202020.crt' --output "$TSA_CERT_DER"
curl --fail --location --retry 3 --silent --show-error 'https://www.microsoft.com/pkiops/crl/Microsoft%20Public%20RSA%20Timestamping%20CA%202020.crl' --output "$TSA_CRL_DER"
curl --fail --location --retry 3 --silent --show-error 'https://www.microsoft.com/pkiops/certs/microsoft%20identity%20verification%20root%20certificate%20authority%202020.crt' --output "$TSA_ROOT_DER"
curl --fail --location --retry 3 --silent --show-error 'https://www.microsoft.com/pkiops/crl/microsoft%20identity%20verification%20root%20certificate%20authority%202020.crl' --output "$TSA_ROOT_CRL_DER"

sha1_thumbprint() {
  openssl x509 -inform DER -in "$1" -noout -fingerprint -sha1 | sed 's/.*=//; s/://g' | tr '[:lower:]' '[:upper:]'
}
check_thumbprint() {
  local actual
  actual="$(sha1_thumbprint "$1")"
  if [[ "$actual" != "$2" ]]; then
    echo "Unexpected Microsoft certificate thumbprint for $(basename "$1"): $actual" >&2
    exit 1
  fi
}
check_thumbprint "$MICROSOFT_ROOT_DER" '8F43288AD272F3103B6FB1428485EA3014C0BCFE'
check_thumbprint "$TSA_CERT_DER" '27F0ABAC2877BA255F62B389B43FF539A0FB598E'
check_thumbprint "$TSA_ROOT_DER" 'F40042E2E5F7E8EF8189FED15519AECE42C3BFA2'
openssl x509 -inform DER -in "$MICROSOFT_ROOT_DER" -out "$MICROSOFT_ROOT_PEM"
openssl x509 -inform DER -in "$TSA_CERT_DER" -out "$TSA_CERT_PEM"
openssl x509 -inform DER -in "$TSA_ROOT_DER" -out "$TSA_ROOT_PEM"
openssl crl -inform DER -in "$SIGNER_CRL_DER" -out "$SIGNER_CRL_PEM"
openssl crl -inform DER -in "$TSA_CRL_DER" -out "$TSA_CRL_PEM"
openssl crl -inform DER -in "$TSA_ROOT_CRL_DER" -out "$TSA_ROOT_CRL_PEM"
TSA_CAS="$INPUT/Microsoft-TSA-CAs.pem"
TSA_CRLS="$INPUT/Microsoft-TSA-CRLs.pem"
cat "$TSA_CERT_PEM" "$TSA_ROOT_PEM" > "$TSA_CAS"
cat "$TSA_CRL_PEM" "$TSA_ROOT_CRL_PEM" > "$TSA_CRLS"

echo 'Verifying the primary Microsoft Authenticode signature and signer revocation status...'
VERIFY_LOG="$INPUT/Microsoft-WebView2-Authenticode.txt"
set +e
osslsigncode verify -index 0 -CAfile "$MICROSOFT_ROOT_PEM" \
  -CRLfile "$SIGNER_CRL_PEM" -TSA-CAfile "$TSA_CAS" \
  -TSA-CRLfile "$TSA_CRLS" -ignore-cdp -in "$RUNTIME" > "$VERIFY_LOG" 2>&1
VERIFY_STATUS=$?
set -e
cat "$VERIFY_LOG"
if [[ "$VERIFY_STATUS" -ne 0 ]] || ! grep -Fq 'Signature verification: ok' "$VERIFY_LOG" || ! grep -Fq 'Signature CRL verification: ok' "$VERIFY_LOG" || ! grep -Fq 'Microsoft Corporation' "$VERIFY_LOG" || ! grep -Fq 'Microsoft Code Signing PCA 2024' "$VERIFY_LOG"; then
  echo 'Microsoft primary Authenticode signature, signer, or CRL validation failed; refusing to package.' >&2
  exit 1
fi
if grep -Fq 'Timestamp is not available' "$VERIFY_LOG"; then
  echo 'WARNING: osslsigncode could not independently validate the embedded timestamp; signer certificate validity is checked at build time.' >&2
fi

echo 'Compiling the Windows x64-targeted offline installer...'
cd "$ROOT"
makensis -V3 "$ROOT/installer/WoWReader.nsi"

test -s "$INSTALLER"
echo
echo "Installer: $INSTALLER"
printf 'Size: '; stat -c '%s bytes' "$INSTALLER"
printf 'SHA-256: '; sha256sum "$INSTALLER" | awk '{print $1}'
printf 'WebView2 runtime: '; stat -c '%s bytes' "$RUNTIME"
printf 'Runtime SHA-256: '; sha256sum "$RUNTIME" | awk '{print $1}'
