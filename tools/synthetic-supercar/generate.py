#!/usr/bin/env python3
"""Generate a reproducible fictional shop. JDK/Jar inputs are supplied by caller."""
import argparse, os, pathlib, struct, subprocess
p=argparse.ArgumentParser()
p.add_argument('output',type=pathlib.Path);p.add_argument('--seed',type=int,default=2502026)
p.add_argument('--customers',type=int,default=3000);p.add_argument('--vehicles',type=int,default=3500);p.add_argument('--orders',type=int,default=25000)
p.add_argument('--parts',type=int,default=20000);p.add_argument('--services',type=int,default=1200)
p.add_argument('--jdk',type=pathlib.Path,required=True);p.add_argument('--jars',type=pathlib.Path,required=True)
a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
here=pathlib.Path(__file__).resolve().parent
jars=os.pathsep.join(str(a.jars/n) for n in ('jackcess.jar','commons-lang3.jar','commons-logging.jar'))
classes=a.output/'.classes';classes.mkdir(exist_ok=True)
subprocess.run([str(a.jdk/'bin/javac'),'-cp',jars,'-d',str(classes),str(here/'SyntheticSupercar.java')],check=True)
subprocess.run([str(a.jdk/'bin/java'),'-cp',jars+os.pathsep+str(classes),'SyntheticSupercar',str(a.output),str(a.seed),str(a.customers),str(a.vehicles),str(a.orders),str(a.parts),str(a.services)],check=True)
# dBase III, CP950; a small supplemental customer name table exercises the DBF reader.
fields=[('NO',10),('CUSTNAME',40)];rows=[(str(i),'王○名'+str(i)) for i in range(1,11)]
header=bytearray(32);header[0]=3;header[1:4]=bytes([26,11,20]);struct.pack_into('<IHH',header,4,len(rows),32+len(fields)*32+1,1+sum(n for _,n in fields))
out=bytearray(header)
for name,length in fields:
    f=bytearray(32);f[:len(name)]=name.encode('ascii');f[11]=ord('C');f[16]=length;out.extend(f)
out.append(13)
for row in rows:
    out.append(32)
    for value,(_,length) in zip(row,fields):out.extend(value.encode('cp950').ljust(length,b' '))
out.append(26);(a.output/'CUSTNAME.DBF').write_bytes(out)
print('DBF cp950 rows=10')
