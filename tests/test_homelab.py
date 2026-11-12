import importlib.util
import json
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT/'windows/JarvisHud/Homelab'/f'{name}.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module
collector, control = load('collector'), load('control')

class CollectionTests(unittest.TestCase):
    def test_fresh_install_only_collects_local_resources(self):
        config = collector.load_config(None)
        self.assertFalse(config.get('discoverTailscale', False))
        with patch.object(collector, 'resources', return_value={}), patch.object(collector, 'codex') as codex, patch.object(collector, 'openclaw') as claw, patch.object(collector, 'hermes') as hermes, patch.object(collector, 'remote') as remote:
            snapshot = collector.collect(config)
        self.assertEqual(len(snapshot['devices']), 1)
        self.assertEqual(snapshot['sessions'], [])
        for reader in (codex, claw, hermes, remote): reader.assert_not_called()

    def test_configuration_rejects_duplicate_hosts(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'config.json'
            path.write_text(json.dumps({'schemaVersion':1, 'devices':[{'id':'a'}, {'id':'a'}]}))
            with self.assertRaises(ValueError): collector.load_config(path)

    def test_urls_never_export_credentials_or_query_tokens(self):
        for url in ('https://user:password@example.test', 'https://example.test/?token=example', 'file:///etc/passwd', 'https://example.test/#private'):
            self.assertFalse(collector.clean_url(url))
        self.assertEqual(collector.clean_url('https://example.test/status'), 'https://example.test/status')

    def test_external_bookmarks_are_not_probed(self):
        with patch.object(collector.urllib.request, 'build_opener') as opener:
            result = collector.service_health({'url':'https://unconfigured.example.test', 'device':'server'}, [{'id':'server', 'dns':'server.example.test'}])
        opener.assert_not_called()
        self.assertEqual(result['status'], 'Link only')

    def test_health_probe_does_not_follow_redirects(self):
        self.assertIsNone(collector.NoHealthRedirect().redirect_request(None,None,302,'',{},'https://unconfigured.example.test'))

    def test_ssh_arguments_cannot_be_shell_fragments(self):
        for settings in ({'ssh':'host; command'}, {'ssh':'-oProxyCommand=bad'}, {'ssh':'server','python':'python3; command'}):
            with patch.object(collector,'run') as run, self.assertRaises(ValueError): collector.remote(settings,'')
            run.assert_not_called()

    def test_old_turn_is_not_reported_as_running(self):
        self.assertEqual(collector.codex_state('running', 100, now=1000), 'Unclosed turn')
        self.assertEqual(collector.codex_state('running', 950, now=1000), 'Open turn')

class ControlTests(unittest.TestCase):
    def setUp(self):
        control.DEVICE='server';control.POLICY={'containers':['homepage'],'ssh':'lab-server'}
        self.request={'device':'server','container':'homepage','action':'set','containerId':'a'*64,'sampledAt':time.time(),'running':False,'expectedRunning':True}

    def test_disabled_unknown_and_stale_controls_fail_closed(self):
        for update in ({'device':'other'},{'container':'database'},{'sampledAt':time.time()-121},{'containerId':'short'},{'running':'false'}):
            with self.assertRaises(ValueError): control.validate(self.request|update)

    def test_replaced_or_changed_container_is_never_mutated(self):
        for observed in ({'containerId':'b'*64,'state':'running','running':True},{'containerId':'a'*64,'state':'exited','running':False}):
            with patch.object(control,'inspect_container',return_value=observed),patch.object(control,'run') as run,self.assertRaises(ValueError):
                control.apply_request(self.request)
            run.assert_not_called()

    def test_action_uses_verified_id_and_confirms_result(self):
        before={'containerId':'a'*64,'state':'running','running':True}
        after={'containerId':'a'*64,'state':'exited','running':False}
        with patch.object(control,'inspect_container',side_effect=[before,after]),patch.object(control,'run') as run:
            run.return_value.returncode=0
            result=control.apply_request(self.request)
        self.assertTrue(result['ok'] and result['changed'])
        self.assertEqual(run.call_args.args[0],['docker','container','stop','--timeout','15','a'*64])

if __name__=='__main__': unittest.main()
