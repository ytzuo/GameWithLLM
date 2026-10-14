"""Deploy and exercise the exact full candidate's Windows IL2CPP Player.
Uses a deterministic local model fixture and emits NPC evidence only after all
candidate resources, network/game checks and the Player exit marker pass.
"""
import functools
import argparse
import hashlib
import shutil
import json
import os
from pathlib import Path
import subprocess
import threading
import time
import urllib.request
import uuid
from http.server import SimpleHTTPRequestHandler, BaseHTTPRequestHandler, ThreadingHTTPServer
from importlib.util import spec_from_file_location, module_from_spec

REPO = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser()
parser.add_argument('candidate', type=Path)
parser.add_argument('--output', type=Path, default=REPO / 'Artifacts/NpcPresentationStage4')
parser.add_argument('--restore-save-id')
args = parser.parse_args()
CANDIDATE = args.candidate.resolve(strict=True)
MANIFEST_PATH = CANDIDATE / 'candidate-manifest.json'
MANIFEST = json.loads(MANIFEST_PATH.read_text(encoding='utf-8-sig'))
if MANIFEST['releaseKind'] != 'full':
    raise ValueError('NPC baseline smoke requires a full candidate with its Player.')
MANIFEST_HASH = hashlib.sha256(MANIFEST_PATH.read_bytes()).hexdigest()
OUT = args.output.resolve()
OUT.mkdir(parents=True, exist_ok=True)
DEPLOY = OUT / 'deployment' / MANIFEST_HASH
DEPLOY.mkdir(parents=True, exist_ok=True)
(CANDIDATE / 'npc-runtime-smoke.passed.json').unlink(missing_ok=True)
for entry in MANIFEST['files']:
    source = (REPO / entry['path']).resolve(strict=True)
    if not source.is_relative_to(REPO) or source.stat().st_size != entry['length'] or hashlib.sha256(source.read_bytes()).hexdigest() != entry['sha256']:
        raise ValueError('Candidate artifact changed: ' + entry['path'])
    prefix = 'unity-NPC-agent-client/ServerData/production/'
    relative = entry['path'][len(prefix):] if entry['path'].startswith(prefix) else None
    if entry['path'].startswith('NpcContent/'):
        relative = entry['path'][len('NpcContent/'):]
    if relative:
        target = (DEPLOY / relative).resolve()
        if not target.is_relative_to(DEPLOY): raise ValueError('Invalid deployment path')
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
spec = spec_from_file_location('serve_content', REPO / 'Scripts/Serve-Content.py')
serve_content = module_from_spec(spec); spec.loader.exec_module(serve_content)
EXPECTED_NPCS = {n['name'] for n in json.loads((DEPLOY / 'npc/index.json').read_text(encoding='utf-8-sig'))['npcs']}
EXPECTED_DISPLAY_NAMES = {n['displayName'] for n in json.loads((DEPLOY / 'npc/index.json').read_text(encoding='utf-8-sig'))['npcs']}
SEEN_DISPLAY_NAMES = set()
if args.restore_save_id: (OUT / 'previous-save-id').write_text(args.restore_save_id)
TOKEN = uuid.uuid4().hex
INSTANCE = 'npc-stage4'
ENV = dict(AGENT_SERVICE_ADDR='127.0.0.1:19080', AGENT_SERVICE_BASE_URL='http://127.0.0.1:19080',
           A2A_AGENT_URL='http://127.0.0.1:19080/a2a', RUNTIME_GATEWAY_WS_URL='ws://127.0.0.1:19080/runtime/ws',
           A2A_BEARER_TOKEN=TOKEN, RUNTIME_GATEWAY_TOKEN=TOKEN, MCP_GATEWAY_SERVICE_TOKEN=TOKEN,
           UNITY_INSTANCE_ID='npc-stage4', PLAYER_ID='npc-stage4-player', UNITY_SCENE_ID='warehouse',
           LLM_API_URL='http://127.0.0.1:19081/v1/chat/completions', LLM_API_KEY='local-test-only',
           LLM_MODEL='local-test', LLM_MAX_RETRIES='0', LLM_REQUEST_TIMEOUT_SECONDS='30',
           NPC_CONTENT_BASE_URL='http://127.0.0.1:8081',
           NPC_SMOKE_OUTPUT_PATH=str(OUT),
           CONVERSATION_SAVE_DIR=str(OUT / 'conversations'))
GO_ONLY_KEYS = {'AGENT_SERVICE_ADDR', 'MCP_GATEWAY_SERVICE_TOKEN', 'CONVERSATION_SAVE_DIR', 'NPC_PROFILE_PATH', 'SYSTEM_PROMPT_PATH'}
UNITY_ENV = {key: value for key, value in ENV.items() if not key.startswith('LLM_') and key not in GO_ONLY_KEYS}
PLAYER_ENV = {key: value for key, value in os.environ.items() if not key.startswith('LLM_') and key not in GO_ONLY_KEYS}
PLAYER_ENV.update(UNITY_ENV)
for name in ['runtime-env.json', 'save-world', 'restore-world', 'world-saved', 'world-restored', 'stop', 'ready', 'scene-error', 'state.json', 'world-contract.json', 'scene-report.json', 'player-checks.json', 'previous-world-restored']:
    (OUT / name).unlink(missing_ok=True)
(OUT / 'runtime-env.json').write_text(json.dumps(UNITY_ENV), encoding='utf-8')

class QuietFiles(serve_content.ContentHandler):
    blocked = False
    def log_message(self, *args): pass
    def do_GET(self):
        if self.blocked:
            self.send_error(503)
            return
        super().do_GET()

class Model(BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        system = '\n'.join(m.get('content', '') for m in request['messages'] if m['role'] == 'system')
        SEEN_DISPLAY_NAMES.update(name for name in EXPECTED_DISPLAY_NAMES if name in system)
        last = request['messages'][-1]
        tool = last.get('role') == 'user' and last.get('content') == 'cancel-move'
        call = dict(id='smoke-move', type='function', function=dict(name='game_npc_move', arguments='{"targetId":"landmark:gate"}'))
        if request.get('stream'):
            self.send_response(200); self.send_header('Content-Type', 'text/event-stream'); self.end_headers()
            delta = dict(tool_calls=[dict(index=0, **call)]) if tool else dict(content='stage-four-ok')
            for chunk in [dict(choices=[dict(index=0, delta=delta, finish_reason=None)]),
                          dict(choices=[dict(index=0, delta={}, finish_reason='tool_calls' if tool else 'stop')])]:
                self.wfile.write(('data: ' + json.dumps(chunk) + '\n\n').encode())
            self.wfile.write(b'data: [DONE]\n\n'); self.wfile.flush()
        else:
            message = dict(role='assistant', tool_calls=[call]) if tool else dict(role='assistant', content='stage-four-ok')
            data = json.dumps(dict(choices=[dict(message=message, finish_reason='tool_calls' if tool else 'stop')])).encode()
            self.send_response(200); self.send_header('Content-Length', str(len(data))); self.end_headers(); self.wfile.write(data)

def post(path, body):
    request = urllib.request.Request('http://127.0.0.1:19080' + path, data=json.dumps(body).encode(),
                                    headers={'Authorization': 'Bearer ' + TOKEN, 'Content-Type': 'application/json'})
    return json.loads(urllib.request.urlopen(request, timeout=60).read())

def rpc(path, method, params):
    result = post(path, dict(jsonrpc='2.0', id=uuid.uuid4().hex, method=method, params=params))
    if 'error' in result: raise RuntimeError('RPC failed: ' + str(result['error']))
    return result['result']

def wait_for(check, timeout=60):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if (OUT / 'scene-error').exists(): raise RuntimeError('Player Console error; inspect player.log')
        try:
            value = check()
            if value: return value
        except (OSError, RuntimeError, ValueError): pass
        time.sleep(0.25)
    raise TimeoutError('Smoke condition did not complete')

def tool(name, **args):
    value = rpc('/mcp/runtimes/' + INSTANCE, 'tools/call', dict(name=name, arguments=dict(entityId=args.pop('entityId', 'Merchant'), **args)))
    if value.get('isError'): raise RuntimeError('Unity tool failed: ' + str(value['structuredContent'].get('errorCode')))
    structured = value['structuredContent']
    return structured.get('data', structured)

def message(text, method='message/send', context=None):
    msg = dict(messageId=uuid.uuid4().hex, role='user', parts=[dict(kind='text', text=text)],
               metadata={'https://gamewithllm.dev/extensions/game-context/v1': dict(instanceId=INSTANCE, playerId='npc-stage4-player', agentId='Merchant', sceneId='warehouse')})
    if context: msg['contextId'] = context
    return dict(jsonrpc='2.0', id=uuid.uuid4().hex, method=method, params=dict(message=msg))

def stream(body, on_event=lambda e: None):
    request = urllib.request.Request('http://127.0.0.1:19080/a2a', data=json.dumps(body).encode(), headers={'Authorization': 'Bearer ' + TOKEN, 'Content-Type': 'application/json', 'Accept': 'text/event-stream'})
    events = []
    with urllib.request.urlopen(request, timeout=60) as response:
        for line in response:
            if line.startswith(b'data: '):
                event = json.loads(line[6:]); events.append(event); on_event(event)
    return events

def main():
    global INSTANCE
    subprocess.run(['go', 'build', '-o', str(OUT / 'agent-service.exe'), './cmd/server'], cwd=REPO / 'GameMCPServer', check=True)
    files = ThreadingHTTPServer(('127.0.0.1', 8081), functools.partial(QuietFiles, directory=str(DEPLOY)))
    model = ThreadingHTTPServer(('127.0.0.1', 19081), Model)
    for server in [files, model]: threading.Thread(target=server.serve_forever, daemon=True).start()
    log = (OUT / 'go-scene.log').open('w', encoding='utf-8')
    def start(): return subprocess.Popen([str(OUT / 'agent-service.exe')], cwd=REPO / 'GameMCPServer', env=dict(os.environ, **ENV), stdout=log, stderr=log, creationflags=subprocess.CREATE_NO_WINDOW)
    go = start(); checks = []; cached_failure = None
    player_log_path = OUT / 'player.log'
    player_path = REPO / 'unity-NPC-agent-client/Builds/ContentReleaseProduction/GameWithLLM.exe'
    player = subprocess.Popen([str(player_path), '-batchmode', '-nographics', '-logFile', str(player_log_path), '-gameWithLlmNpcRuntimeSmoke'] + (['-npcSmokeRestorePrevious'] if args.restore_save_id else []),
                              cwd=player_path.parent, env=PLAYER_ENV, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                              creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        print('SMOKE_SERVICES_READY', flush=True)
        with urllib.request.urlopen('http://127.0.0.1:8081/npc/index.json') as response:
            assert response.headers['Cache-Control'] == 'no-cache, must-revalidate' and response.headers['ETag']
        def player_ready():
            if player.poll() is not None: raise AssertionError('Player exited before ready; inspect player.log')
            return (OUT / 'ready').exists()
        wait_for(player_ready, 300)
        INSTANCE = (OUT / 'ready').read_text()
        tools = wait_for(lambda: rpc('/mcp/runtimes/' + INSTANCE, 'tools/list', {})['tools'])
        assert any(t['name'] == 'game_npc_move' for t in tools)
        wait_for(lambda: tool('game_npc_get_state'))
        checks.append('runtime-registration')
        npc_contexts = {}
        if args.restore_save_id:
            assert (OUT / 'previous-world-restored').read_text() == args.restore_save_id
            previous = json.loads((OUT / 'previous-world-contract.json').read_text())
            for entity in previous['entities']:
                if entity['entityId'] not in EXPECTED_NPCS: continue
                current = tool('game_npc_get_state', entityId=entity['entityId'])['position']
                assert all(abs(current[k] - entity['position'][k]) < 0.1 for k in current)
            restored_previous = post('/game-saves/' + args.restore_save_id + '/agent-context:restore',
                dict(instanceId=INSTANCE, playerId='npc-stage4-player', npcIds=['Ryan_001', 'Alice_001'] + sorted(EXPECTED_NPCS)))
            assert restored_previous['result']['ok']
            for restored_context in restored_previous['result']['contexts']:
                if restored_context['npcId'] not in EXPECTED_NPCS: continue
                npc_contexts[restored_context['npcId']] = restored_context['sessionId']
                query = message('restored current npc', context=restored_context['sessionId'])
                query['params']['message']['metadata']['https://gamewithllm.dev/extensions/game-context/v1']['agentId'] = restored_context['npcId']
                assert 'stage-four-ok' in json.dumps(post('/a2a', query))
            assert EXPECTED_DISPLAY_NAMES <= SEEN_DISPLAY_NAMES
            checks.append('previous-presentation-world-and-context-restore')
        for npc_id in sorted(EXPECTED_NPCS):
            query = message('current npc hello', context=npc_contexts.get(npc_id))
            query['params']['message']['metadata']['https://gamewithllm.dev/extensions/game-context/v1']['agentId'] = npc_id
            response = post('/a2a', query)
            assert 'stage-four-ok' in json.dumps(response)
            npc_contexts[npc_id] = response['result']['contextId']
            query = message('current npc stream', 'message/stream', response['result']['contextId'])
            query['params']['message']['metadata']['https://gamewithllm.dev/extensions/game-context/v1']['agentId'] = npc_id
            assert 'stage-four-ok' in json.dumps(stream(query))
            assert tool('game_inventory_get_self', entityId=npc_id) is not None
            for target in ['landmark:warehouse', 'landmark:gate']:
                tool('game_npc_move', entityId=npc_id, targetId=target)
        checks.append('all-catalog-npcs-dialogue-stream-inventory-and-move')
        assert EXPECTED_DISPLAY_NAMES <= SEEN_DISPLAY_NAMES
        first = post('/a2a', message('hello', context=npc_contexts['Merchant'])); assert 'error' not in first and 'stage-four-ok' in json.dumps(first), {'rpcError': first.get('error'), 'status': first.get('result', {}).get('status')}
        context = first['result']['contextId']
        events = stream(message('stream', 'message/stream', context)); assert 'stage-four-ok' in json.dumps(events)
        checks.append('ordinary-and-streaming-dialogue')
        guide = message('guide hello', context=npc_contexts['Guide']); guide['params']['message']['metadata']['https://gamewithllm.dev/extensions/game-context/v1']['agentId'] = 'Guide'
        guide_reply = post('/a2a', guide)
        assert 'stage-four-ok' in json.dumps(guide_reply)
        guide = message('guide stream','message/stream',guide_reply['result']['contextId']); guide['params']['message']['metadata']['https://gamewithllm.dev/extensions/game-context/v1']['agentId'] = 'Guide'
        assert 'stage-four-ok' in json.dumps(stream(guide))
        checks.append('both-current-npc-bindings-and-dialogue')
        for target in ['landmark:warehouse', 'landmark:gate']:
            moved = tool('game_npc_move', targetId=target)
            assert not tool('game_npc_get_state')['movement']['isMoving']
        checks.append('move-warehouse-and-gate')
        # Move back so the cancellation target is distant, then cancel while moving.
        tool('game_npc_move', targetId='landmark:warehouse')
        task_ids = []; cancel_events = []; errors = []
        def consume(event):
            result = event.get('result', {})
            if result.get('id', '').startswith('task-'): task_ids.append(result['id'])
            if result.get('taskId'): task_ids.append(result['taskId'])
        def run_stream():
            try: cancel_events.extend(stream(message('cancel-move', 'message/stream', context), consume))
            except Exception as error: errors.append(type(error).__name__)
        thread = threading.Thread(target=run_stream); thread.start()
        wait_for(lambda: task_ids and json.loads((OUT / 'state.json').read_text())['movement']['isMoving'])
        wait_for(lambda: json.loads((OUT / 'state.json').read_text())['animation']['moving'])
        rpc('/a2a', 'tasks/cancel', dict(id=task_ids[0])); thread.join(30)
        assert not thread.is_alive() and not errors
        wait_for(lambda: not tool('game_npc_get_state')['movement']['isMoving'])
        wait_for(lambda: not json.loads((OUT / 'state.json').read_text())['animation']['moving'])
        assert 'cancelled' in json.dumps(cancel_events); checks.append('cancel-task-and-movement')
        inventory = tool('game_inventory_get_self'); assert inventory is not None; checks.append('inventory')
        (OUT / 'save-world').write_text('save')
        wait_for(lambda: (OUT / 'world-saved').exists())
        save_id = (OUT / 'world-saved').read_text()
        world_contract = json.loads((OUT / 'world-contract.json').read_text())
        assert world_contract['version'] == 3
        assert {item['entityId'] for item in world_contract['npcContents']} == EXPECTED_NPCS
        assert all(set(item) == {'entityId'} for item in world_contract['npcContents'])
        saved_state = world_contract['merchantPosition']
        payload = dict(instanceId=INSTANCE, playerId='npc-stage4-player', operationId=str(uuid.uuid4()), mode='create')
        assert post('/game-saves/' + save_id + '/agent-context:prepare', payload)['result']['ok']
        assert post('/game-saves/' + save_id + '/agent-context:commit', payload)['result']['ok']
        if 'Keeper' in EXPECTED_NPCS:
            path = DEPLOY / 'npc/Keeper/npc.json'
            original = path.read_bytes()
            before_inventory = tool('game_inventory_get_self', entityId='Keeper')
            for failure in ['deleted-name', 'renamed-identity']:
                if failure == 'deleted-name': path.unlink()
                else:
                    changed = json.loads(original)
                    changed['name'] = 'KeeperRenamed'
                    changed['profile']['npcId'] = 'KeeperRenamed'
                    path.write_text(json.dumps(changed), encoding='utf-8')
                try:
                    failed_restore = post('/game-saves/' + save_id + '/agent-context:restore',
                        dict(instanceId=INSTANCE, playerId='npc-stage4-player', npcIds=['Ryan_001', 'Alice_001'] + sorted(EXPECTED_NPCS)))
                    assert not failed_restore['result']['ok'] and failed_restore['result']['errorCode']
                    query = message('existing keeper context', context=npc_contexts['Keeper'])
                    query['params']['message']['metadata']['https://gamewithllm.dev/extensions/game-context/v1']['agentId'] = 'Keeper'
                    assert 'stage-four-ok' in json.dumps(post('/a2a', query))
                    assert tool('game_inventory_get_self', entityId='Keeper') == before_inventory
                finally:
                    path.write_bytes(original)
            checks.append('deleted-or-renamed-npc-restore-rejected-context-retained')
        tool('game_npc_move', targetId='landmark:gate')
        (OUT / 'restore-world').write_text('restore'); wait_for(lambda: (OUT / 'world-restored').exists())
        def position_restored():
            current = tool('game_npc_get_state')['position']
            return all(abs(current[k] - saved_state[k]) < 0.1 for k in saved_state)
        wait_for(position_restored)
        assert tool('game_inventory_get_self') == inventory
        restored = post('/game-saves/' + save_id + '/agent-context:restore', dict(instanceId=INSTANCE, playerId='npc-stage4-player', npcIds=['Ryan_001', 'Alice_001'] + sorted(EXPECTED_NPCS)))
        assert restored['result']['ok']; checks.append('world-inventory-and-context-restore')
        snapshots = list((OUT/'conversations').rglob('*.json'))
        for path in snapshots:
            raw = json.loads(path.read_text(encoding='utf-8-sig'))
            if 'snapshotVersion' in raw:
                assert raw['snapshotVersion'] == 2
                for item in raw['contexts']:
                    if 'npcContent' in item: assert set(item['npcContent']) == {'entityId'}
        checks.append('snapshot-format-two-identity-only')
        go.terminate(); go.wait(10); go = start()
        tools_after = wait_for(lambda: rpc('/mcp/runtimes/' + INSTANCE, 'tools/list', {})['tools'])
        assert sorted(t['name'] for t in tools_after) == sorted(t['name'] for t in tools)
        assert 'error' not in post('/a2a', message('after restart')); checks.append('go-restart-reconnect-manifest')
        checks.append('console-and-missing-script')
        (OUT / 'scene-report.json').write_text(json.dumps(dict(passed=True, model='local deterministic fixture', checks=checks,
            candidateManifestSha256=MANIFEST_HASH, npcNames=sorted(EXPECTED_NPCS),
            toolSchemaSha256=hashlib.sha256(json.dumps(tools, sort_keys=True).encode()).hexdigest(), currentProfilesObserved=True), indent=2))
    finally:
        (OUT / 'stop').write_text('stop')
        try: player.wait(30)
        except subprocess.TimeoutExpired:
            player.terminate(); player.wait(10)
        if player.returncode == 0 and (OUT / 'scene-report.json').exists():
            for offline in [False, True]:
                QuietFiles.blocked = offline
                restart_log = OUT / ('offline-player.log' if offline else 'cache-hit-player.log')
                cached = subprocess.Popen([str(player_path), '-batchmode', '-nographics', '-logFile', str(restart_log),
                                           '-gameWithLlmNpcRuntimeSmoke', '-npcSmokeCachedStartup'],
                                          cwd=player_path.parent, env=PLAYER_ENV, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                          creationflags=subprocess.CREATE_NO_WINDOW)
                try: cached.wait(120)
                except subprocess.TimeoutExpired:
                    cached.terminate(); cached.wait(10)
                if cached.returncode != 0 or 'NPC_CACHED_STARTUP_SUCCESS' not in restart_log.read_text(encoding='utf-8', errors='replace'):
                    cached_failure = 'Cached startup failed; inspect ' + restart_log.name
                    break
            QuietFiles.blocked = False
        go.terminate(); go.wait(10); log.close()
        files.shutdown(); model.shutdown()
    if cached_failure: raise RuntimeError(cached_failure)
    if player.returncode != 0 or 'NPC_RUNTIME_PLAYER_SUCCESS' not in player_log_path.read_text(encoding='utf-8', errors='replace'):
        raise RuntimeError('Player smoke failed; inspect player.log')
    player_checks = json.loads((OUT / 'player-checks.json').read_text())['checks']
    scenarios = set(player_checks) | {'save-restore', 'manifest-reregister'}
    evidence = dict(schemaVersion=1, releaseId=MANIFEST['releaseId'], candidateManifestSha256=MANIFEST_HASH,
                    successMarker='NPC_RUNTIME_SMOKE_SUCCESS', playerLogSha256=hashlib.sha256(player_log_path.read_bytes()).hexdigest(),
                    environment='local deployed production candidate / Windows IL2CPP / deterministic model',
                    scenarios=[dict(name=name, passed=True) for name in sorted(scenarios)])
    evidence['additionalChecks'] = ['online-cache-hit-player-restart', 'offline-cached-player-restart', 'http-revalidation-headers']
    (CANDIDATE / 'npc-runtime-smoke.passed.json').write_text(json.dumps(evidence, indent=2), encoding='utf-8')
    print('NPC_STAGE4_PLAYER_SUCCESS', flush=True)

if __name__ == '__main__': main()
