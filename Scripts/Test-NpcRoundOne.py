"""Local integration coordinator. Start this, then launch NpcRoundOneSceneSmoke
with Artifacts/NpcRound1/runtime-env.json applied to the Editor environment.
Uses a deterministic local model fixture; never calls an external LLM.
"""
import functools
import json
import os
from pathlib import Path
import subprocess
import threading
import time
import urllib.request
import uuid
from http.server import SimpleHTTPRequestHandler, BaseHTTPRequestHandler, ThreadingHTTPServer

REPO = Path(__file__).resolve().parent.parent
OUT = REPO / 'Artifacts/NpcRound1'
OUT.mkdir(parents=True, exist_ok=True)
TOKEN = uuid.uuid4().hex
INSTANCE = 'npc-round1'
ENV = dict(AGENT_SERVICE_ADDR='127.0.0.1:19080', AGENT_SERVICE_BASE_URL='http://127.0.0.1:19080',
           A2A_AGENT_URL='http://127.0.0.1:19080/a2a', RUNTIME_GATEWAY_WS_URL='ws://127.0.0.1:19080/runtime/ws',
           A2A_BEARER_TOKEN=TOKEN, RUNTIME_GATEWAY_TOKEN=TOKEN, MCP_GATEWAY_SERVICE_TOKEN=TOKEN,
           UNITY_INSTANCE_ID='npc-round1', PLAYER_ID='npc-round1-player', UNITY_SCENE_ID='warehouse-demo',
           LLM_API_URL='http://127.0.0.1:19081/v1/chat/completions', LLM_API_KEY='local-test-only',
           LLM_MODEL='local-test', LLM_MAX_RETRIES='0', LLM_REQUEST_TIMEOUT_SECONDS='30',
           NPC_CONTENT_BASE_URL='http://127.0.0.1:8081',
           CONVERSATION_SAVE_DIR=str(OUT / 'conversations'))
for name in ['runtime-env.json', 'save-world', 'restore-world', 'world-saved', 'world-restored', 'stop', 'ready', 'scene-error', 'state.json']:
    (OUT / name).unlink(missing_ok=True)
(OUT / 'runtime-env.json').write_text(json.dumps(ENV), encoding='utf-8')

class QuietFiles(SimpleHTTPRequestHandler):
    def log_message(self, *args): pass

class Model(BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        last = request['messages'][-1]
        tool = last.get('role') == 'user' and last.get('content') == 'cancel-move'
        call = dict(id='smoke-move', type='function', function=dict(name='game_npc_move', arguments='{"targetId":"landmark:gate"}'))
        if request.get('stream'):
            self.send_response(200); self.send_header('Content-Type', 'text/event-stream'); self.end_headers()
            delta = dict(tool_calls=[dict(index=0, **call)]) if tool else dict(content='round-one-ok')
            for chunk in [dict(choices=[dict(index=0, delta=delta, finish_reason=None)]),
                          dict(choices=[dict(index=0, delta={}, finish_reason='tool_calls' if tool else 'stop')])]:
                self.wfile.write(('data: ' + json.dumps(chunk) + '\n\n').encode())
            self.wfile.write(b'data: [DONE]\n\n'); self.wfile.flush()
        else:
            message = dict(role='assistant', tool_calls=[call]) if tool else dict(role='assistant', content='round-one-ok')
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
        if (OUT / 'scene-error').exists(): raise RuntimeError('Editor Console error; inspect npc-round1-scene.log')
        try:
            value = check()
            if value: return value
        except (OSError, RuntimeError, ValueError): pass
        time.sleep(0.25)
    raise TimeoutError('Smoke condition did not complete')

def tool(name, **args):
    value = rpc('/mcp/runtimes/' + INSTANCE, 'tools/call', dict(name=name, arguments=dict(entityId='Ryan_001', **args)))
    if value.get('isError'): raise RuntimeError('Unity tool failed: ' + str(value['structuredContent'].get('errorCode')))
    structured = value['structuredContent']
    return structured.get('data', structured)

def message(text, method='message/send', context=None):
    msg = dict(messageId=uuid.uuid4().hex, role='user', parts=[dict(kind='text', text=text)],
               metadata={'https://gamewithllm.dev/extensions/game-context/v1': dict(instanceId=INSTANCE, playerId='npc-round1-player', agentId='Ryan_001', sceneId='warehouse-demo')})
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
    files = ThreadingHTTPServer(('127.0.0.1', 8081), functools.partial(QuietFiles, directory=str(REPO / 'unity-NPC-agent-client')))
    model = ThreadingHTTPServer(('127.0.0.1', 19081), Model)
    for server in [files, model]: threading.Thread(target=server.serve_forever, daemon=True).start()
    log = (OUT / 'go-scene.log').open('w', encoding='utf-8')
    def start(): return subprocess.Popen([str(OUT / 'agent-service.exe')], cwd=REPO / 'GameMCPServer', env=dict(os.environ, **ENV), stdout=log, stderr=log, creationflags=subprocess.CREATE_NO_WINDOW)
    go = start(); checks = []
    try:
        print('SMOKE_SERVICES_READY', flush=True)
        wait_for(lambda: (OUT / 'ready').exists(), 120)
        INSTANCE = (OUT / 'ready').read_text()
        tools = wait_for(lambda: rpc('/mcp/runtimes/' + INSTANCE, 'tools/list', {})['tools'])
        assert any(t['name'] == 'game_npc_move' for t in tools); checks.append('runtime-registration')
        first = post('/a2a', message('hello')); assert 'error' not in first and 'round-one-ok' in json.dumps(first)
        context = first['result']['contextId']
        events = stream(message('stream', 'message/stream', context)); assert 'round-one-ok' in json.dumps(events)
        checks.append('ordinary-and-streaming-dialogue')
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
        rpc('/a2a', 'tasks/cancel', dict(id=task_ids[0])); thread.join(30)
        assert not thread.is_alive() and not errors
        wait_for(lambda: not tool('game_npc_get_state')['movement']['isMoving'])
        assert 'cancelled' in json.dumps(cancel_events); checks.append('cancel-task-and-movement')
        inventory = tool('game_inventory_get_self'); assert inventory is not None; checks.append('inventory')
        (OUT / 'save-world').write_text('save')
        wait_for(lambda: (OUT / 'world-saved').exists())
        save_id = (OUT / 'world-saved').read_text()
        saved_state = tool('game_npc_get_state')['position']
        payload = dict(instanceId=INSTANCE, playerId='npc-round1-player', operationId=str(uuid.uuid4()), mode='create')
        assert post('/game-saves/' + save_id + '/agent-context:prepare', payload)['result']['ok']
        assert post('/game-saves/' + save_id + '/agent-context:commit', payload)['result']['ok']
        tool('game_npc_move', targetId='landmark:gate')
        (OUT / 'restore-world').write_text('restore'); wait_for(lambda: (OUT / 'world-restored').exists())
        restored_state = tool('game_npc_get_state')['position']
        assert all(abs(restored_state[k] - saved_state[k]) < 0.1 for k in saved_state)
        assert tool('game_inventory_get_self') == inventory
        restored = post('/game-saves/' + save_id + '/agent-context:restore', dict(instanceId=INSTANCE, playerId='npc-round1-player', npcIds=['Ryan_001', 'Alice_001']))
        assert restored['result']['ok']; checks.append('world-inventory-and-context-restore')
        go.terminate(); go.wait(10); go = start()
        tools_after = wait_for(lambda: rpc('/mcp/runtimes/' + INSTANCE, 'tools/list', {})['tools'])
        assert sorted(t['name'] for t in tools_after) == sorted(t['name'] for t in tools)
        assert 'error' not in post('/a2a', message('after restart')); checks.append('go-restart-reconnect-manifest')
        checks.append('console-and-missing-script')
        (OUT / 'scene-report.json').write_text(json.dumps(dict(passed=True, model='local deterministic fixture', checks=checks), indent=2))
        print('NPC_ROUND_ONE_SCENE_SUCCESS', flush=True)
    finally:
        (OUT / 'stop').write_text('stop')
        go.terminate(); go.wait(10); log.close()
        files.shutdown(); model.shutdown()

if __name__ == '__main__': main()
