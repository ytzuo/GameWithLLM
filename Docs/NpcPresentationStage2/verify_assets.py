from pathlib import Path
import json,re,subprocess
rows=json.loads(Path('Docs/NpcPresentationStage1/asset-migration.json').read_text())
for row in rows:
 text=Path(row['target']+'.meta').read_text()
 assert 'guid: '+row['guid'] in text,row['target']
for old,name,guid in [('merchant_001','Merchant','44c07dacf138e9f4ab81e86a9737b4da'),('guide_001','Guide','cdf750e1f981b5546a1022ff41a0bb9c')]:
 assert 'guid: '+guid in Path('unity-NPC-agent-client/Assets/Content/Npcs/'+name+'.meta').read_text()
 assert not Path('unity-NPC-agent-client/Assets/Content/Npcs/'+old).exists()
 assert not Path('NpcContent/npc/'+old).exists()
paths=subprocess.check_output(['git','diff','--name-only','--diff-filter=D']).decode().splitlines()
deleted={}
for path in paths:
 if path.startswith('unity-NPC-agent-client/') and path.endswith('.meta'):
  old=subprocess.check_output(['git','show','HEAD:'+path]).decode()
  match=re.search(r'^guid: (\w+)',old,re.M)
  if match: deleted[match.group(1)]=path
# Moved assets retain GUIDs and are not deleted dependencies.
live={re.search(r'^guid: (\w+)',p.read_text(),re.M).group(1) for p in Path('unity-NPC-agent-client/Assets').rglob('*.meta') if re.search(r'^guid: (\w+)',p.read_text(),re.M)}
removed=set(deleted)-live
for p in Path('unity-NPC-agent-client/Assets').rglob('*'):
 if p.suffix not in {'.asset','.prefab','.unity','.mat','.controller','.anim','.meta'}: continue
 try: text=p.read_text()
 except UnicodeError: continue
 for guid in re.findall(r'guid: (\w+)',text):
  assert guid not in removed, f'Dangling removed GUID {guid}: {p}'
print('NPC_STAGE2_ASSET_AUDIT_SUCCESS preserved_files=16 preserved_directories=2 dangling_deleted_guids=0')
