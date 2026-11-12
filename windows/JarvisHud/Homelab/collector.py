"""Read-only homelab snapshot. Stdlib; optional PyYAML/psutil on the existing hosts.
No chat bodies, environment files, credentials, agent prompts, or mutations are exported.
Remote code is streamed over existing verified SSH, with bounded subprocess lifetimes.
"""
import concurrent.futures as futures
import contextlib
import datetime as dt
import json
import os
import pathlib
import platform
import shutil
import socket
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

NOW = time.time()
HOME = pathlib.Path.home()



def run(args, timeout=8, stdin=None):
    return subprocess.run(args, input=stdin, capture_output=True, text=True,
                          encoding='utf-8', errors='replace', timeout=timeout,
                          creationflags=0x08000000 if os.name == 'nt' else 0)


@contextlib.contextmanager
def dbopen(path):
    db = sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True, timeout=2)
    db.row_factory = sqlite3.Row
    db.execute('pragma query_only=ON')
    try:yield db
    finally:db.close()


def stamp(value):
    if isinstance(value, (float, int)):
        return value / 1000 if value > 100000000000 else value
    if isinstance(value, str):
        try:
            parsed=dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
            if parsed.tzinfo is None:parsed=parsed.replace(tzinfo=dt.timezone.utc)
            return max(0,parsed.timestamp())
        except ValueError:
            pass
    return 0


def number(value):
    return value if isinstance(value, (float, int)) and value >= 0 else None


def codex_state(status, updated_at, now=None):
    if status in ('inProgress','in_progress','running'):
        return 'Open turn' if (time.time() if now is None else now)-stamp(updated_at)<600 else 'Unclosed turn'
    return 'Idle' if status=='completed' else 'Interrupted' if status in ('interrupted','failed') else 'Unknown'


def resources():
    result = {'sampledAt': time.time(), 'os': platform.system().lower()}
    try:
        import psutil
        result.update(uptimeSeconds=time.time()-psutil.boot_time(),
                      cpuPercent=psutil.cpu_percent(interval=.15),
                      memoryPercent=psutil.virtual_memory().percent,
                      memoryTotal=psutil.virtual_memory().total)
    except ImportError:
        if sys.platform == 'darwin':
            import re
            boot = run(['sysctl', '-n', 'kern.boottime']).stdout
            result['uptimeSeconds'] = time.time()-int(re.search(r'sec = (\d+)', boot).group(1))
            result['loadAverage'] = os.getloadavg()[0]
            result['memoryTotal'] = int(run(['sysctl', '-n', 'hw.memsize']).stdout)
            vm=run(['vm_stat']).stdout
            page=int(re.search(r'page size of (\d+)',vm).group(1))
            counts={k.strip():int(v.strip().rstrip('.')) for k,v in (line.split(':',1) for line in vm.splitlines()[1:] if ':' in line)}
            used=sum(counts.get(k,0) for k in ['Pages active','Pages wired down','Pages occupied by compressor'])*page
            result['memoryPercent']=min(100,100*used/result['memoryTotal'])
        elif sys.platform.startswith('linux'):
            result['uptimeSeconds'] = float(pathlib.Path('/proc/uptime').read_text().split()[0])
            result['loadAverage'] = os.getloadavg()[0]
            mem = {k: int(v.split()[0])*1024 for k,v in (line.split(':',1) for line in pathlib.Path('/proc/meminfo').read_text().splitlines())}
            result.update(memoryTotal=mem['MemTotal'],memoryPercent=100*(1-mem['MemAvailable']/mem['MemTotal']))
        elif os.name == 'nt':
            import ctypes
            class Memory(ctypes.Structure):
                _fields_ = [('length', ctypes.c_ulong), ('load', ctypes.c_ulong)] + [(s, ctypes.c_ulonglong) for s in ('total','avail','page','availpage','virtual','availvirtual','extended')]
            m=Memory();m.length=ctypes.sizeof(m);ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m))
            ctypes.windll.kernel32.GetTickCount64.restype=ctypes.c_ulonglong
            result.update(uptimeSeconds=ctypes.windll.kernel32.GetTickCount64()/1000,memoryTotal=m.total,memoryPercent=m.load)
    result['disks'] = storage()
    return result


def storage():
    """Physical/local filesystems only. Never wake network shares or add APFS siblings twice."""
    mounts = []
    if os.name == 'nt':
        import ctypes
        mask = ctypes.windll.kernel32.GetLogicalDrives()
        for index in range(26):
            path = chr(65+index)+':\\'
            # DRIVE_FIXED excludes remote, removable and optical media.
            if mask & (1 << index) and ctypes.windll.kernel32.GetDriveTypeW(path) == 3:
                mounts.append((path, path, 'Local volume'))
    elif sys.platform == 'darwin':
        # / and /System/Volumes/Data share an APFS container; report the data volume once.
        data = '/System/Volumes/Data'
        mounts.append((data if os.path.exists(data) else '/', 'Macintosh HD', 'APFS container'))
        # Mounted local external disks only; diskutil is bounded and returns plist metadata.
        try:
            import plistlib
            info = plistlib.loads(run(['diskutil', 'list', '-plist', 'external', 'physical'],timeout=4).stdout.encode())
            for disk in info.get('AllDisksAndPartitions', []):
                for part in disk.get('Partitions', []):
                    path = part.get('MountPoint')
                    if path and path.startswith('/Volumes/'):
                        mounts.append((path, part.get('VolumeName') or path, part.get('Content','Local volume')))
        except Exception:
            pass
    elif sys.platform.startswith('linux'):
        seen = set()
        for line in pathlib.Path('/proc/mounts').read_text().splitlines():
            fields = line.split()
            if len(fields)<3: continue
            dev, path, fs = fields[:3]
            path = path.replace('\\040', ' ')
            if not dev.startswith('/dev/') or dev in seen or fs in ('squashfs','iso9660'):
                continue
            if path.startswith(('/snap/', '/var/lib/docker/', '/var/lib/containers/')):
                continue
            seen.add(dev)
            mounts.append((path, path, fs))
    output = []
    for path, name, fs in mounts:
        try:
            total, used, free = shutil.disk_usage(path)
            if total <= 0: continue
            output.append({'id':path, 'name':name, 'mount':path, 'filesystem':fs,
                           'totalBytes':total, 'usedBytes':used, 'freeBytes':free,
                           'sampledAt':time.time()})
        except OSError:
            pass
    return output


def codex(node, root=HOME/'.codex', owner='Codex'):
    path=root/'state_5.sqlite'
    if not path.exists():
        return [], [], 'No Codex session store'
    with dbopen(path) as db:
        cols={r[1] for r in db.execute('pragma table_info(threads)')}
        model='model' if 'model' in cols else 'NULL as model'
        title="coalesce(nullif(name,''),title) as title" if 'name' in cols else 'title'
        # A single bounded metadata query; never walk or load the transcript archive.
        rows=[dict(r) for r in db.execute(f'select id,{title},updated_at,tokens_used,model_provider,{model},cwd,archived from threads where archived=0 order by updated_at desc')]
        total=db.execute('select count(*) from threads where archived=0').fetchone()[0]
    turns={}
    hp=root/'thread_history_1.sqlite'
    if hp.exists():
        with dbopen(hp) as db:
            for row in rows:
                r=db.execute('select status,started_at,completed_at from thread_turns where thread_id=? order by rollout_ordinal desc limit 1',(row['id'],)).fetchone()
                if r: turns[row['id']]=dict(r)
    output=[]
    for r in rows:
        turn=turns.get(r['id'],{});status=turn.get('status','unknown')
        # An unclosed turn is not proof a worker survived a crash: preserve that distinction.
        state=codex_state(status,r['updated_at'])
        output.append({'id':node+':codex:'+r['id'],'agent':owner,'provider':'Codex','device':node,
                       'title':(r['title']or 'Untitled session')[:180],'model':r['model'] or r['model_provider'],
                       'state':state,'updatedAt':stamp(r['updated_at']),'totalTokens':number(r['tokens_used']),
                       'usageKind':'Thread cumulative','source':'Codex local metadata',
                       'detail':'Latest turn: '+status,'workspace':r['cwd']})
    return output,[{'id':node+':codex:'+owner,'name':owner,'provider':'Codex','device':node,'model':rows[0].get('model') if rows else None,
                   'sessions':total,'detail':'All unarchived sessions · local metadata'}],f'{len(output)} of {total} unarchived Codex sessions'


def openclaw(node, root=None):
    root=pathlib.Path(root or HOME/'.openclaw').expanduser();cfg=json.loads((root/'openclaw.json').read_text())
    ac=cfg.get('agents',{});entries=ac.get('entries') or {a['id']:a for a in ac.get('list',[])}
    default=ac.get('defaults',{}).get('model',{});default=default.get('primary') if isinstance(default,dict) else default
    sessions=[];agents=[]
    for aid,a in entries.items():
        name=(a.get('identity') or {}).get('name') or a.get('name') or aid
        model=a.get('model',default);model=model.get('primary') if isinstance(model,dict) else model
        ap=pathlib.Path(a.get('agentDir') or root/'agents'/aid/'agent')/'openclaw-agent.sqlite'
        count=0
        if ap.exists():
            with dbopen(ap) as db:
                count=db.execute('select count(*) from session_nodes where archived_at is null').fetchone()[0]
                rows=db.execute("select session_key,current_session_id,entry_json,status,updated_at,label,display_name from session_nodes where archived_at is null order by case when status in ('running','started','queued') then 0 else 1 end,updated_at desc limit 250").fetchall()
            for row in rows:
                e=json.loads(row['entry_json'] or '{}');status=row['status'] or e.get('status','unknown')
                state={'done':'Idle','completed':'Idle','running':'Running','started':'Running','queued':'Queued','error':'Error','aborted':'Interrupted'}.get(status,status.title())
                actual=e.get('model') or model
                provider=e.get('modelProvider')
                if actual and provider and '/' not in actual: actual=provider+'/'+actual
                sessions.append({'id':node+':openclaw:'+row['session_key'],'agent':name,'provider':'OpenClaw','device':node,
                    'title':(row['label'] or row['display_name'] or row['session_key'])[:180], 'model':actual,'state':state,
                    'updatedAt':stamp(e.get('lastActivityAt') or row['updated_at']), 'inputTokens':number(e.get('inputTokens')),
                    'outputTokens':number(e.get('outputTokens')),'cacheReadTokens':number(e.get('cacheRead')),
                    'cacheWriteTokens':number(e.get('cacheWrite')),'totalTokens':number(e.get('totalTokens')),
                    'contextLimit':number(e.get('contextTokens')),'usageKind':'Latest context / last turn',
                    'source':'OpenClaw session store','detail':'Runtime status: '+status})
        agents.append({'id':node+':openclaw:'+aid,'name':name,'provider':'OpenClaw','device':node,'model':model,'sessions':count,
                       'detail':'Configured agent · model on sessions may differ'})
    return sessions,agents


def hermes(node, root=None, owner='Hermes'):
    root=pathlib.Path(root or HOME/'.hermes').expanduser();sessions=[]
    with dbopen(root/'state.db') as db:
        leases={r[0] for r in db.execute('select conversation_id from session_turn_leases where expires_at > ?',(time.time(),))}
        rows=db.execute('select id,session_key,source,title,model,input_tokens,output_tokens,cache_read_tokens,cache_write_tokens,reasoning_tokens,last_activity_at,started_at,ended_at,tool_call_count,estimated_cost_usd,actual_cost_usd,cost_status from sessions where archived=0 order by last_activity_at desc limit 250').fetchall()
        count=db.execute('select count(*) from sessions where archived=0').fetchone()[0]
        for r in rows:
            active=r['id'] in leases or r['session_key'] in leases
            sessions.append({'id':node+':hermes:'+r['id'],'agent':owner,'provider':'Hermes','device':node,
                'title':(r['title'] or r['source']+' · '+r['id'])[:180],'model':r['model'],'state':'Running' if active else 'Idle',
                'updatedAt':stamp(r['last_activity_at'] or r['started_at']),'inputTokens':number(r['input_tokens']),
                'outputTokens':number(r['output_tokens']),'cacheReadTokens':number(r['cache_read_tokens']),
                'cacheWriteTokens':number(r['cache_write_tokens']),'totalTokens':sum(r[k]or 0 for k in ['input_tokens','output_tokens','cache_read_tokens','cache_write_tokens']),
                'cost':r['actual_cost_usd'] if r['actual_cost_usd'] is not None else r['estimated_cost_usd'],
                'costKind':r['cost_status'] or 'Unknown','toolCalls':r['tool_call_count'],'usageKind':'Session cumulative',
                'source':'Hermes session store + turn lease','detail':'Active turn lease' if active else 'No active turn lease'})
    return sessions,[{'id':node+':hermes','name':owner,'provider':'Hermes','device':node,'model':rows[0]['model'] if rows else None,
                      'sessions':count,'detail':'Hermes dashboard + Telegram · session models shown separately'}]


def clean_url(value):
    try:
        parsed = urllib.parse.urlparse(str(value))
        if parsed.scheme not in ('http', 'https', 'smb') or not parsed.hostname:
            return None
        if parsed.username or parsed.password or parsed.query or parsed.fragment:
            return None
        return parsed.geturl()
    except ValueError:
        return None


def homepage(node, config):
    import yaml
    path = pathlib.Path(config['homepageFile']).expanduser()
    groups = yaml.safe_load(path.read_text(encoding='utf-8')) or []
    items = []
    for group in groups:
        for category, services in group.items():
            if not isinstance(services, list):
                continue
            for service in services:
                for name, props in service.items():
                    if not isinstance(props, dict):
                        continue
                    url = clean_url(props.get('href', ''))
                    if url:
                        items.append({'id': node + ':homepage:' + name, 'name': name,
                                      'group': category, 'url': url, 'device': node,
                                      'container': props.get('container'),
                                      'source': 'Homepage display metadata'})
    return items


def annotate_controls(items, node, allowed):
    containers = {}
    if allowed:
        try:
            for line in run(['docker', 'ps', '-a', '--no-trunc', '--format', '{{json .}}']).stdout.splitlines():
                row = json.loads(line)
                if row.get('Names') in allowed:
                    containers[row['Names']] = row
        except (ValueError, OSError, subprocess.TimeoutExpired):
            pass
    for item in items:
        observed = containers.get(item.get('container'), {})
        if observed.get('ID'):
            item.update(containerId=observed['ID'], containerState=observed.get('State'),
                        containerStatus=observed.get('Status'), controlHost=node,
                        controlAvailable=True, containerSampledAt=time.time())
    return items


def fetch_json(url):
    with urllib.request.urlopen(url,timeout=5) as r: return json.load(r)


def kuma(node, settings):
    base = clean_url(settings.get('kumaUrl', ''))
    if not base: raise ValueError('Configure a credential-free Kuma URL')
    slug = urllib.parse.quote(settings.get('kumaSlug', 'homelab'), safe='')
    config=fetch_json(base.rstrip('/')+'/api/status-page/'+slug)
    data=fetch_json(base.rstrip('/')+'/api/status-page/heartbeat/'+slug);result=[]
    for group in config.get('publicGroupList',[]):
        for m in group.get('monitorList',[]):
            key=str(m['id']);beats=data.get('heartbeatList',{}).get(key,[]);last=beats[-1] if beats else {}
            result.append({'id':node+':'+key,'name':m['name'],'status':last.get('status'),'updatedAt':stamp(last.get('time')),
                           'uptime24h':data.get('uptimeList',{}).get(key+'_24'),'latencyMs':last.get('ping'),
                           'history':[x.get('status') for x in beats[-40:]],'source':'Uptime Kuma · 24 hours'})
    return result


def node_snapshot(settings):
    node = settings['id']
    result = {'device': node, 'sampledAt': time.time(), 'sessions': [], 'agents': [],
              'services': [], 'monitors': [], 'errors': [], 'coverage': []}
    providers = set(settings.get('providers', []))
    try:
        result['resources'] = resources()
    except Exception as error:
        result['errors'].append('Resources: ' + type(error).__name__)
    readers = {
        'codex': lambda: codex(node, pathlib.Path(settings.get('codexHome', str(HOME/'.codex'))).expanduser()),
        'openclaw': lambda: openclaw(node, settings.get('openclawHome')),
        'hermes': lambda: hermes(node, settings.get('hermesHome'), settings.get('hermesName', 'Hermes')),
    }
    for provider, reader in readers.items():
        if provider not in providers:
            continue
        try:
            sessions, agents, *coverage = reader()
            result['sessions'] += sessions
            result['agents'] += agents
            result['coverage'] += coverage or [provider + ' metadata connected']
        except Exception as error:
            result['errors'].append(provider + ': ' + type(error).__name__)
    if 'homepage' in providers:
        try:
            result['services'] += homepage(node, settings)
        except Exception as error:
            result['errors'].append('Homepage: ' + type(error).__name__)
    result['services'] += settings.get('services', [])
    annotate_controls(result['services'], node, settings.get('allowedContainers', []))
    if 'kuma' in providers:
        try:
            result['monitors'] = kuma(node, settings)
        except Exception as error:
            result['errors'].append('Kuma: ' + type(error).__name__)
    return result


def remote(settings, script):
    import base64
    import re
    alias = settings.get('ssh', '')
    interpreter = settings.get('python', 'python3')
    if not re.fullmatch(r'[A-Za-z0-9_][A-Za-z0-9_.@:-]*', alias):
        raise ValueError('Configure a valid SSH host alias')
    if not re.fullmatch(r'[A-Za-z0-9_./:\\-]+', interpreter) or interpreter.startswith('-'):
        raise ValueError('Use an interpreter path without spaces or shell metacharacters')
    encoded = base64.b64encode(json.dumps(settings).encode()).decode('ascii')
    command = ('sudo -n ' if settings.get('sudo', False) else '') + interpreter + ' - --node ' + encoded
    args = ['ssh', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes',
            '-o', 'ConnectTimeout=5', '-o', 'ServerAliveInterval=4',
            '-o', 'ServerAliveCountMax=1', alias, command]
    response = run(args, timeout=22, stdin=script)
    if response.returncode:
        raise RuntimeError('SSH collection unavailable')
    return json.loads(response.stdout)


class NoHealthRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def service_health(item, devices):
    item = dict(item)
    parsed = urllib.parse.urlparse(item['url'])
    host = parsed.hostname
    device = next((d for d in devices if d['id'] == item.get('device')), None)
    item.update(checkedAt=time.time(), status='Link only', latencyMs=None)
    # Imported bookmarks are never permission to probe an arbitrary host.
    configured_hosts = [device.get('dns'), device.get('ip')] if device else []
    if not clean_url(item['url']) or not device or (host not in configured_hosts and item.get('probe') is not True):
        return item
    started = time.perf_counter()
    try:
        if parsed.scheme == 'smb':
            with socket.create_connection((host, parsed.port or 445), timeout=3):
                pass
            item.update(status='Reachable', detail='SMB port reachable')
        else:
            request = urllib.request.Request(item['url'], headers={'User-Agent':'Obelisk read-only health'}, method='HEAD')
            try:
                with urllib.request.build_opener(NoHealthRedirect()).open(request, timeout=4) as response:
                    code = response.status
            except urllib.error.HTTPError as error:
                code = error.code
            if code in (404, 405, 501):
                try:
                    request = urllib.request.Request(item['url'], headers={'User-Agent':'Obelisk read-only health'})
                    with urllib.request.build_opener(NoHealthRedirect()).open(request, timeout=4) as response:
                        code = response.status
                        response.read(512)
                except urllib.error.HTTPError as error:
                    code = error.code
            item.update(httpStatus=code, status='Healthy' if 200 <= code < 400 else 'Auth required' if code in (401,403) else 'Error', detail='HTTP '+str(code))
        item['latencyMs'] = round((time.perf_counter()-started)*1000)
    except Exception:
        item.update(status='Unreachable', detail='Connection unavailable')
    return item


def load_config(path):
    if path and pathlib.Path(path).exists():
        data = json.loads(pathlib.Path(path).read_text(encoding='utf-8-sig'))
        if data.get('schemaVersion') != 1:
            raise ValueError('Unsupported homelab configuration')
        ids = [device.get('id') for device in data.get('devices', [])]
        if any(not isinstance(x, str) or not x for x in ids) or len(set(ids)) != len(ids):
            raise ValueError('Device IDs must be unique non-empty strings')
        return data
    return {'schemaVersion': 1, 'devices': [{'id': 'local', 'name': 'Workstation', 'local': True, 'providers': []}]}


def collect(config):
    started = time.perf_counter()
    result = {'schemaVersion': 1, 'timestamp': time.time(), 'devices': [], 'sessions': [],
              'agents': [], 'services': [], 'monitors': [], 'sources': [], 'errors': []}
    settings = [dict(device) for device in config.get('devices', [])]
    for device in settings:
        device['services'] = [dict(service) for service in config.get('services', []) if service.get('device') == device['id'] and clean_url(service.get('url'))]
        allowed = config.get('controls', {}).get(device['id'], {})
        device['allowedContainers'] = allowed.get('containers', [])
    peers = []
    if config.get('discoverTailscale', False):
        executable = shutil.which('tailscale') or str(pathlib.Path(os.environ.get('ProgramFiles', ''))/'Tailscale/tailscale.exe')
        try:
            tail = json.loads(run([executable, 'status', '--json'], timeout=5).stdout)
            peers = [tail.get('Self', {})] + list(tail.get('Peer', {}).values())
        except Exception as error:
            result['errors'].append('Tailscale: '+type(error).__name__)
    for device in settings:
        peer = next((p for p in peers if device.get('ip') in p.get('TailscaleIPs', []) or (device.get('dns') and device['dns'].rstrip('.') == p.get('DNSName','').rstrip('.'))), {})
        result['devices'].append({'id':device['id'], 'name':device.get('name',device['id']),
            'role':device.get('role',''), 'ip':device.get('ip',''), 'dns':device.get('dns',''),
            'os':peer.get('OS'), 'online':bool(device.get('local') or peer.get('Online')),
            'lastSeen':stamp(peer.get('LastSeen')), 'source':'Configured host',
            'telemetryStatus':'Not configured', 'sampledAt':time.time()})
    script = pathlib.Path(__file__).read_text(encoding='utf-8')
    with futures.ThreadPoolExecutor(max_workers=5) as pool:
        tasks = {pool.submit(node_snapshot, d) if d.get('local') else pool.submit(remote, d, script): d['id'] for d in settings if d.get('local') or d.get('ssh')}
        for future in futures.as_completed(tasks):
            node = tasks[future]
            device = next(d for d in result['devices'] if d['id'] == node)
            try:
                data = future.result()
                device.update(data.get('resources', {}))
                device.update(online=True, telemetryStatus='Partial' if data.get('errors') else 'Connected')
                for key in ('sessions','agents','services','monitors'):
                    result[key] += data.get(key, [])
                result['sources'].append({'device':node,'status':'Connected','sampledAt':data['sampledAt'],'detail':' · '.join(data.get('coverage',[])),'errors':data.get('errors',[])})
            except Exception as error:
                device['telemetryStatus']='Unavailable'
                result['sources'].append({'device':node,'status':'Unavailable','sampledAt':time.time(),'detail':type(error).__name__,'errors':[]})
    # Preserve configured bookmark routes even when a host is unavailable.
    seen = {s['id'] for s in result['services']}
    result['services'] += [dict(s) for s in config.get('services',[]) if s.get('id') not in seen and clean_url(s.get('url'))]
    result['services'] = [s for s in result['services'] if clean_url(s.get('url'))]
    with futures.ThreadPoolExecutor(max_workers=6) as pool:
        result['services'] = list(pool.map(lambda s: service_health(s,result['devices']),result['services']))
    result['sessions'].sort(key=lambda s:(s['state'] not in ('Running','Open turn','Queued'),-s.get('updatedAt',0)))
    result['collectionMs'] = round((time.perf_counter()-started)*1000)
    return result


if __name__ == '__main__':
    try:
        if '--node' in sys.argv:
            import base64
            data = node_snapshot(json.loads(base64.b64decode(sys.argv[sys.argv.index('--node')+1])))
        else:
            path = sys.argv[sys.argv.index('--config')+1] if '--config' in sys.argv else None
            data = collect(load_config(path))
        print(json.dumps(data, ensure_ascii=True, separators=(',',':')))
    except Exception as error:
        print(json.dumps({'error':type(error).__name__}), file=sys.stderr)
        sys.exit(1)
