"""Check Git's publishable file set without printing secret values. Run with Gitleaks too."""
import ipaddress
from pathlib import Path
import re
import subprocess
import sys

ROOT=Path(__file__).resolve().parents[1]
files=subprocess.check_output(['git','ls-files','--cached','--others','--exclude-standard','-z'],cwd=ROOT).decode().split('\0')
files=sorted(set(f for f in files if f))
forbidden_suffixes={'.key','.pem','.pfx','.p12','.token','.bin','.wav','.mp3','.gguf','.exe','.dll','.pdb','.sqlite','.db','.log','.lnk'}
forbidden_names={'settings.json','homelab.json','homelab-snapshot.json','speech-link.json','last-speech-metrics.json','.env','id_rsa','id_ed25519'}
patterns={
 'private key':re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'),
 'personal Windows path':re.compile(r'[A-Za-z]:[\\/]+Users[\\/]+[A-Za-z0-9_.-]+'),
 'personal Unix path':re.compile(r'/(?:Users|home)/[A-Za-z0-9_.-]+/'),
 'private tailnet name':re.compile(r'\b[a-z0-9.-]+\.ts\.net\b',re.I),
 'provider credential':re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9]{30,}|sk-proj-[A-Za-z0-9_-]{30,}|AKIA[A-Z0-9]{16})\b'),
}
addresses=re.compile(r'(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])')
private=[ipaddress.ip_network(n) for n in ('10.0.0.0/8','172.16.0.0/12','192.168.0.0/16','100.64.0.0/10')]
findings=[]
for name in files:
 path=ROOT/name
 if path.is_symlink(): findings.append((name,0,'symlink requires explicit review'));continue
 if path.name in forbidden_names or path.suffix.lower() in forbidden_suffixes or path.name.startswith('.env.') and path.name!='.env.example': findings.append((name,0,'private/runtime file type'));continue
 if path.suffix.lower() in ('.png','.ico','.webp','.gif'): continue
 try: content=path.read_text(encoding='utf-8-sig')
 except (UnicodeError,OSError): findings.append((name,0,'unreviewed binary or unreadable file'));continue
 for number,line in enumerate(content.splitlines(),1):
  for category,pattern in patterns.items():
   if pattern.search(line): findings.append((name,number,category))
  if name=='tools/audit-public.py': continue # network-range definitions, not inventory
  for match in addresses.finditer(line):
   try: address=ipaddress.ip_address(match.group())
   except ValueError: continue
   if any(address in network for network in private): findings.append((name,number,'private network address'))
for name,line,category in findings: print(f'{name}:{line}: {category}')
print(f'Audited {len(files)} publishable files; {len(findings)} findings. Values are intentionally omitted.')
sys.exit(bool(findings))
