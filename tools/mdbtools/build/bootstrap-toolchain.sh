#!/usr/bin/env bash
# Run in an empty working directory on Ubuntu 24.04; downloads/extracts only, no sudo.
set -euo pipefail
apt-get download \
 gcc-mingw-w64-x86-64-posix=13.2.0-6ubuntu1+26.1 \
 binutils-mingw-w64-x86-64=2.41.90.20240122-1ubuntu1+11.4 \
 mingw-w64-x86-64-dev=11.0.1-3build1 mingw-w64-common=11.0.1-3build1 \
 gcc-mingw-w64-x86-64-posix-runtime=13.2.0-6ubuntu1+26.1 \
 gcc-mingw-w64-base=13.2.0-6ubuntu1+26.1
sha256sum -c "$(dirname "$0")/toolchain-SHA256SUMS"
for file in ./*.deb; do dpkg-deb -x "$file" root; done
ln -sf x86_64-w64-mingw32-gcc-posix root/usr/bin/x86_64-w64-mingw32-gcc
printf 'Add %s/root/usr/bin to PATH before running build-windows.sh\n' "$PWD"
