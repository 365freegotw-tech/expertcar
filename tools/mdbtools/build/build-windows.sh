#!/usr/bin/env bash
# Ubuntu 24.04 + MinGW-w64 POSIX GCC 13.2.0 / binutils 2.41.90.
# No system installation required; see bootstrap-toolchain.sh.
set -euo pipefail
script_dir=$(cd -- "$(dirname -- "$0")" && pwd)
source_dir="$script_dir/../windows/sources"
if [ -d "$script_dir/../sources" ]; then source_dir="$script_dir/../sources"; fi
build_root=${1:?Usage: build-windows.sh NEW_BUILD_DIRECTORY OUTPUT_DIRECTORY}
output_dir=${2:?Output directory required}
mkdir -p "$build_root" "$output_dir"
build_root=$(cd "$build_root" && pwd)
output_dir=$(cd "$output_dir" && pwd)
export SOURCE_DATE_EPOCH=1734307200
cd "$build_root"
cp "$source_dir/mdbtools-1.0.1.tar.gz" .
cp "$source_dir/libiconv-1.18.tar.gz" .
printf '%s\n' \
 'ff9c425a88bc20bf9318a332eec50b17e77896eef65a0e69415ccb4e396d1812  mdbtools-1.0.1.tar.gz' \
 '3b08f5f4f9b4eb82f151a7040bfd6fe6c6fb922efe4b1659c66ea933276965e8  libiconv-1.18.tar.gz' | sha256sum -c -
tar -xf libiconv-1.18.tar.gz
tar -xf mdbtools-1.0.1.tar.gz
cd libiconv-1.18
./configure --host=x86_64-w64-mingw32 --prefix="$build_root/prefix" \
 --disable-shared --enable-static CC=x86_64-w64-mingw32-gcc-posix \
 CFLAGS="-O2 -g0 -ffile-prefix-map=$build_root=." LDFLAGS=-static
make -j4
make install
cd ../mdbtools-1.0.1
patch -p1 < "$script_dir/mingw-compat.patch"
./configure --host=x86_64-w64-mingw32 --disable-maintainer-mode --disable-shared --enable-static \
 --disable-glib --disable-man --without-bash-completion-dir --with-libiconv-prefix="$build_root/prefix" \
 CC=x86_64-w64-mingw32-gcc-posix CPPFLAGS="-I$build_root/prefix/include" LIBS=-liconv \
 CFLAGS="-O2 -g0 -ffile-prefix-map=$build_root=." \
 LDFLAGS="-static -Wl,--no-insert-timestamp -L$build_root/prefix/lib"
grep -q -- '-DHAVE_ICONV=1' src/libmdb/Makefile
grep -q '^HAVE_ICONV_H = 1$' src/libmdb/Makefile
grep -Eq '^LIBS = .* -liconv' src/libmdb/Makefile
make -C src/libmdb -j4
make -C src/util -j4 LDFLAGS="-all-static -Wl,--no-insert-timestamp -L$build_root/prefix/lib" mdb-tables.exe mdb-schema.exe mdb-export.exe
for tool in mdb-tables mdb-schema mdb-export; do
 x86_64-w64-mingw32-nm "src/util/$tool.exe" > "$build_root/$tool.symbols"
 grep -q 'libiconv_open' "$build_root/$tool.symbols"
 x86_64-w64-mingw32-strip "src/util/$tool.exe"
 cp "src/util/$tool.exe" "$output_dir/"
 grep -aq 'MDB_JET3_CHARSET' "$output_dir/$tool.exe"
 # Only Windows system DLLs are allowed. libiconv and compiler runtime are static.
 dependencies=$(x86_64-w64-mingw32-objdump -p "$output_dir/$tool.exe" | sed -n 's/.*DLL Name: //p')
 printf '%s\n' "$tool: $dependencies"
 if printf '%s\n' "$dependencies" | grep -Eiv '^(KERNEL32|msvcrt)\.dll$'; then
   echo 'Unexpected runtime DLL' >&2; exit 1
 fi
done
cd "$output_dir"
sha256sum mdb-tables.exe mdb-schema.exe mdb-export.exe > SHA256SUMS
