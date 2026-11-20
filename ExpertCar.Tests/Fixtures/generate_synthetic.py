#!/usr/bin/env python3
"""Rebuild only invented test fixtures. Requires JDK 11+ and Maven Central access for MDB."""
from pathlib import Path
import os
import struct
import subprocess
import urllib.request

here = Path(__file__).resolve().parent

fields = [('NAME', 'C', 30, 0), ('DATE_TEXT', 'C', 12, 0),
          ('AMOUNT', 'N', 12, 2), ('DATE', 'D', 8, 0)]
rows = [
    (False, '測試客戶', '113/11/20', '1234.56', '20241120'),
    (True, '已刪除虛構列', '2024-11-20', '9999.99', '20241120'),
    (False, '虛構乙', '', '0.25', '        '),
    (False, '虛構丙', '2024-11-20', '100.00', '20241120'),
]
header_length = 32 + len(fields) * 32 + 1
record_length = 1 + sum(f[2] for f in fields)
header = bytearray(32)
header[0] = 3
header[1:4] = bytes([26, 11, 20])
struct.pack_into('<IHH', header, 4, len(rows), header_length, record_length)
data = bytearray(header)
for name, kind, length, decimals in fields:
    field = bytearray(32)
    field[:len(name)] = name.encode('ascii')
    field[11] = ord(kind)
    field[16] = length
    field[17] = decimals
    data.extend(field)
data.append(13)
for deleted, *values in rows:
    data.append(42 if deleted else 32)
    for (_, kind, length, _), value in zip(fields, values):
        raw = value.encode('cp950')
        assert len(raw) <= length
        data.extend(raw.rjust(length, b' ') if kind == 'N' else raw.ljust(length, b' '))
data.append(26)
(here / 'synthetic-customers.dbf').write_bytes(data)

def write_dbf(name, field_names, records):
    field_length = 40
    record_length = 1 + len(field_names) * field_length
    header_length = 32 + len(field_names) * 32 + 1
    header = bytearray(32)
    header[0] = 3
    header[1:4] = bytes([26, 11, 20])
    struct.pack_into('<IHH', header, 4, len(records), header_length, record_length)
    content = bytearray(header)
    for field_name in field_names:
        field = bytearray(32)
        field[:len(field_name)] = field_name.encode('ascii')
        field[11] = ord('C')
        field[16] = field_length
        content.extend(field)
    content.append(13)
    for values in records:
        content.append(32)
        for value in values:
            encoded = value.encode('cp950')
            assert len(encoded) <= field_length
            content.extend(encoded.ljust(field_length, b' '))
    content.append(26)
    (here / name).write_bytes(content)

write_dbf('synthetic-custname.dbf', ['NO', 'CUSTNAME'],
          [('1', '虛構甲'), ('2', '虛構甲')])
write_dbf('synthetic-custphon.dbf', ['CAR_ID', 'PHONE1'],
          [('ZZ-5678', '0911111111'), ('NO-MATCH', '0922222222')])

artifacts = [
    ('jackcess.jar', 'com/healthmarketscience/jackcess/jackcess/4.0.5/jackcess-4.0.5.jar'),
    ('commons-lang3.jar', 'org/apache/commons/commons-lang3/3.10/commons-lang3-3.10.jar'),
    ('commons-logging.jar', 'commons-logging/commons-logging/1.2/commons-logging-1.2.jar'),
]
cache = Path(os.environ.get('EXPERTCAR_FIXTURE_CACHE', '/tmp/expertcar-synthetic-jars'))
cache.mkdir(parents=True, exist_ok=True)
for name, remote in artifacts:
    dest = cache / name
    if not dest.exists():
        urllib.request.urlretrieve('https://repo.maven.apache.org/maven2/' + remote, dest)
classpath = os.pathsep.join(str(cache / name) for name, _ in artifacts)
javac = Path(os.environ['JAVA_HOME']) / 'bin/javac' if 'JAVA_HOME' in os.environ else 'javac'
java = Path(os.environ['JAVA_HOME']) / 'bin/java' if 'JAVA_HOME' in os.environ else 'java'
subprocess.run([str(javac), '-cp', classpath, str(here / 'SyntheticMdb.java')], check=True)
output = here / 'synthetic-customers.mdb'
output.unlink(missing_ok=True)
subprocess.run([str(java), '-cp', classpath + os.pathsep + str(here), 'SyntheticMdb', str(output)], check=True)
print('Generated synthetic MDB and DBF.')
