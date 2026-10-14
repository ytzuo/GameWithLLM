"""Validate stage-one handoff data only; never invoke production parsers or mutate files."""
import json
import re
import subprocess
from pathlib import Path

BASE = Path(__file__).resolve().parent
ROOT = BASE.parent.parent
FIXTURES = BASE / "Fixtures"
NAME = re.compile(r"^[A-Za-z][A-Za-z0-9_]{0,63}$")
FORBIDDEN = {
    "schemaVersion", "contentVersion", "playerBuildId", "minPlayerVersion",
    "maxPlayerVersion", "manifestSha256", "catalogContentVersion",
}
MARKERS = re.compile(r"NpcContent|NPCContent|npcContent|RemoteNpc|npc/|merchant_001|guide_001")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def load(path):
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)


def exact(value, keys):
    require(isinstance(value, dict) and set(value) == set(keys), f"Unexpected keys: {value.keys() if isinstance(value, dict) else type(value)}")


def no_versions(value):
    if isinstance(value, dict):
        require(not FORBIDDEN.intersection(value), "NPC version field in target fixture")
        for child in value.values():
            no_versions(child)
    elif isinstance(value, list):
        for child in value:
            no_versions(child)


def main():
    for path in BASE.rglob("*.json"):
        load(path)
    for path in FIXTURES.glob("*.json"):
        no_versions(load(path))

    catalog = load(FIXTURES / "index.json")
    exact(catalog, ("texts", "npcs"))
    names = [item["name"] for item in catalog["npcs"]]
    require(names == ["Merchant", "Guide"], "Unexpected sample identities")
    require(len({name.casefold() for name in names}) == len(names), "Name collision")
    for item in catalog["npcs"]:
        exact(item, ("name", "displayName", "description", "avatarPath", "manifestPath"))
        name = item["name"]
        require(NAME.fullmatch(name), "Non-ASCII technical name")
        require(item["avatarPath"] == f"npc/{name}/avatar.png", "Avatar path mismatch")
        require(item["manifestPath"] == f"npc/{name}/npc.json", "Manifest path mismatch")
        manifest = load(FIXTURES / f"{name}.npc.json")
        exact(manifest, ("name", "visual", "animationDriver", "profile", "systemPrompt"))
        require(manifest["name"] == name and manifest["profile"]["npcId"] == name, "Profile identity mismatch")
        require(manifest["profile"]["displayName"] == item["displayName"], "Display name mismatch")
        exact(manifest["visual"], ("prefabAddress",))
        require(manifest["visual"]["prefabAddress"] == f"npc/{name}/visual", "Address mismatch")
        require(manifest["animationDriver"] == {"kind": "builtin", "driverId": "standard-locomotion"}, "Unexpected sample driver")
        exact(manifest["systemPrompt"], ("locale", "template"))
        for key in ("displayName", "npcId", "identity", "personality", "speakingStyle", "responsibilities", "worldKnowledge", "forbiddenTopics"):
            require("{{" + key + "}}" in manifest["systemPrompt"]["template"], "Missing prompt placeholder")

    binding = load(FIXTURES / "runtime-binding.json")
    expected_bindings = [{"entityId": name} for name in names]
    require(binding == {"npcContents": expected_bindings}, "Runtime binding mismatch")
    world = load(FIXTURES / "world-save.json")
    require(world["version"] == 3 and world["npcContents"] == expected_bindings, "Save format mismatch")
    require({x["entityId"] for x in world["entities"]} == {"player:local-player-1", *names}, "Save entities mismatch")
    require({x["containerId"] for x in world["inventories"]} == {"player:local-player-1.inventory", *(f"npc:{n}.inventory" for n in names)}, "Inventory identity mismatch")
    snapshot = load(FIXTURES / "conversation-snapshot.json")
    require(snapshot["snapshotVersion"] == 2, "Snapshot format mismatch")
    require([x["npcContent"] for x in snapshot["contexts"]] == expected_bindings, "Snapshot binding mismatch")
    for context in snapshot["contexts"]:
        require(context["npcId"] == context["npcContent"]["entityId"] and context["historyMessages"] == [], "Snapshot identity/history mismatch")
    records = load(FIXTURES / "installed-current.json")
    require([x["name"] for x in records] == names, "Installed names mismatch")
    for record in records:
        exact(record, ("name", "displayName", "description", "avatarPath", "manifestJson"))
        manifest = json.loads(record["manifestJson"], object_pairs_hook=unique_object)
        no_versions(manifest)
        require(manifest == load(FIXTURES / f"{record['name']}.npc.json"), "Installed raw manifest mismatch")

    assets = load(BASE / "asset-migration.json")
    require(len(assets) == 16 and len({x["guid"] for x in assets}) == 16, "Asset count/GUID mismatch")
    for asset in assets:
        require((ROOT / asset["source"]).is_file(), "Missing source asset")
        require(f"guid: {asset['guid']}" in (ROOT / asset["sourceMeta"]).read_text(encoding="utf-8"), "Source GUID changed")
        require(not (ROOT / asset["target"]).exists(), "Target already migrated: stage one baseline changed")
        for reference in asset["references"]:
            require(asset["guid"] in (ROOT / reference).read_text(encoding="utf-8"), "Stale GUID reference")
    addresses = load(BASE / "address-migration.json")
    require(len(addresses) == 26, "Address count mismatch")
    require({x["newAddress"] for x in addresses if x["action"] == "replace"} == {f"npc/{n}/visual" for n in names}, "Replacement addresses mismatch")
    for entry in addresses:
        require(entry["oldAddress"] in (ROOT / entry["group"]).read_text(encoding="utf-8"), "Stale address baseline")
    for path in load(BASE / "cleanup-candidates.json"):
        require((ROOT / path).is_file(), "Stale cleanup candidate")

    tracked = subprocess.check_output(["git", "ls-files"], cwd=ROOT, text=True).splitlines()
    inventory = []
    for path in tracked:
        candidate = ROOT / path
        if not path.startswith(("GameMCPServer/", "unity-NPC-agent-client/Assets/", "unity-NPC-agent-client/Packages/", "Scripts/", "NpcContent/")) or candidate.suffix not in {".go", ".cs", ".ps1", ".json", ".asset", ".prefab", ".unity", ".asmdef", ".uxml", ".uss"}:
            continue
        matches = []
        for number, line in enumerate(candidate.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
            if MARKERS.search(line):
                matches.append({"line": number, "tokens": sorted(set(MARKERS.findall(line)))})
        if matches:
            inventory.append({"path": path, "matches": matches})
    require(inventory == load(BASE / "dependency-inventory.json"), "Dependency inventory drift")
    cases = load(BASE / "acceptance-cases.json")
    require({c["name"] for c in cases["productionSmoke"]} == {"dynamic-download", "animation-driver", "save-restore", "resource-integrity", "cache-repair", "catalog-address-missing", "scene-cancel", "manifest-reregister"}, "Smoke scenario mismatch")
    print(f"STAGE1_HANDOFF_VALID: {len(names)} NPCs, {len(assets)} GUID-preserving moves, {len(addresses)} addresses, {len(inventory)} dependency files")


if __name__ == "__main__":
    main()
