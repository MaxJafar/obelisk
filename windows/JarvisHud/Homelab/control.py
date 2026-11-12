"""Explicit service actions for Obelisk. User-configured hosts, explicit application allowlists and checked container identity.
The JSON request is stdin data. No request value is evaluated by a shell.
"""
import base64
import json
import pathlib
import re
import subprocess
import sys
import time

POLICY = {}
DEVICE = ''


def run(args, timeout=8, stdin=None):
    return subprocess.run(args, input=stdin, capture_output=True, text=True,
                          encoding='utf-8', errors='replace', timeout=timeout,
                          creationflags=0x08000000 if sys.platform == 'win32' else 0)


def validate(request):
    if not isinstance(request, dict) or request.get('device') != DEVICE or not DEVICE:
        raise ValueError('This host has no configured service control.')
    if request.get('container') not in POLICY.get('containers', []):
        raise ValueError('This application is not configured for service control.')
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,127}', request['container']):
        raise ValueError('Invalid container name.')
    if request.get('action') not in ('inspect', 'set'):
        raise ValueError('Unsupported action.')
    if not re.fullmatch(r'[0-9a-f]{64}', str(request.get('containerId', ''))):
        raise ValueError('Refresh the container identity first.')
    if request['action'] == 'set':
        if type(request.get('running')) is not bool or type(request.get('expectedRunning')) is not bool:
            raise ValueError('A running state is required.')
        stamp = request.get('sampledAt')
        if not isinstance(stamp, (float, int)) or not 0 <= time.time()-stamp < 120:
            raise ValueError('The service state is stale. Refresh before changing it.')


def inspect_container(name):
    # Only state and identity: never export environment, mounts, labels or credentials.
    result = run(['docker', 'container', 'inspect', '--format',
                  '{{.Id}}|{{.State.Status}}', name])
    if result.returncode:
        raise RuntimeError('The container is unavailable. Refresh the map.')
    parts = result.stdout.strip().split('|')
    if len(parts) != 2 or not re.fullmatch('[0-9a-f]{64}', parts[0]):
        raise RuntimeError('Docker returned an unrecognized container state.')
    return {'containerId': parts[0], 'state': parts[1], 'running': parts[1] == 'running'}


def apply_request(request):
    validate(request)
    observed = inspect_container(request['container'])
    if observed['containerId'] != request['containerId']:
        raise ValueError('The container was replaced. Refresh before controlling it.')
    if request['action'] == 'inspect':
        return {'ok': True, 'changed': False, **observed, 'message': 'State verified over SSH.'}
    if observed['state'] not in ('running', 'exited', 'created'):
        raise ValueError('The container is transitioning or paused. Refresh its state.')
    if observed['running'] != request['expectedRunning']:
        raise ValueError('The service changed since the last snapshot. Refresh its state.')
    if observed['running'] == request['running']:
        return {'ok': True, 'changed': False, **observed, 'message': 'Already in the requested state.'}
    args = ['docker', 'container', 'start', observed['containerId']] if request['running'] else [
        'docker', 'container', 'stop', '--timeout', '15', observed['containerId']]
    result = run(args, timeout=28)
    if result.returncode:
        raise RuntimeError('Docker did not complete the action. Refresh to verify the state.')
    after = inspect_container(request['container'])
    if after['containerId'] != request['containerId'] or after['running'] != request['running']:
        raise RuntimeError('The requested state was not confirmed. Refresh the map.')
    return {'ok': True, 'changed': True, **after,
            'message': 'Running state verified.' if after['running'] else 'Stopped state verified.'}


def dispatch(request, config):
    global POLICY, DEVICE
    DEVICE = request.get('device', '')
    POLICY = config.get('controls', {}).get(DEVICE, {})
    validate(request)
    alias = POLICY.get('ssh', '')
    interpreter = POLICY.get('python', 'python3')
    if not re.fullmatch(r'[A-Za-z0-9_][A-Za-z0-9_.@:-]*', alias):
        raise ValueError('Configure a valid SSH host alias for service control.')
    if not re.fullmatch(r'[A-Za-z0-9_./:\\-]+', interpreter) or interpreter.startswith('-'):
        raise ValueError('Configure a safe Python interpreter path.')
    payload = base64.b64encode(json.dumps({'request':request, 'policy':POLICY}).encode()).decode('ascii')
    source = pathlib.Path(__file__).read_text(encoding='utf-8-sig')
    source += "\npayload=json.loads(base64.b64decode('"+payload+"'))\nPOLICY=payload['policy']\nDEVICE=payload['request']['device']"
    source += "\ntry:\n print(json.dumps(apply_request(payload['request'])))"
    source += "\nexcept Exception as e:\n print(json.dumps({'ok':False,'message':str(e) if isinstance(e,(ValueError,RuntimeError)) else 'Action status unknown. Refresh to verify.'}))\n"
    command = ('sudo -n ' if POLICY.get('sudo', False) else '') + interpreter + ' - --remote'
    args = ['ssh','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o','ConnectTimeout=5',
            '-o','ServerAliveInterval=4','-o','ServerAliveCountMax=1',alias,command]
    result = run(args, timeout=40, stdin=source)
    if result.returncode:
        raise RuntimeError('SSH control was unavailable. Refresh to verify the service.')
    return json.loads(result.stdout)


if __name__ == '__main__' and '--remote' not in sys.argv:
    try:
        path = pathlib.Path(sys.argv[sys.argv.index('--config')+1])
        config = json.loads(path.read_text(encoding='utf-8-sig'))
        print(json.dumps(dispatch(json.load(sys.stdin), config)))
    except Exception as error:
        print(json.dumps({'ok':False,'message':str(error) if isinstance(error,(ValueError,RuntimeError))
                          else 'Action status unknown. Refresh to verify.'}))
