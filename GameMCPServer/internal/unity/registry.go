package unity

import (
	"fmt"
	"sort"
	"sync"
)

type registeredUnityInstance struct {
	instanceID string
	session    *jsonRPCSession
	npcs       map[string]struct{}
	tools      map[string]ToolDefinition
}

// UnityRegistry 保存在线 Unity 实例、NPC 路由和运行时工具能力。
type UnityRegistry struct {
	mu                sync.RWMutex
	instances         map[string]*registeredUnityInstance
	npcToInstance     map[string]string
	sessionToInstance map[*jsonRPCSession]string
}

func NewUnityRegistry() *UnityRegistry {
	return &UnityRegistry{
		instances:         make(map[string]*registeredUnityInstance),
		npcToInstance:     make(map[string]string),
		sessionToInstance: make(map[*jsonRPCSession]string),
	}
}

func (r *UnityRegistry) Register(session *jsonRPCSession, registration UnityRegistration) (bool, error) {
	if session == nil {
		return false, fmt.Errorf("session is required")
	}
	if err := registration.Validate(); err != nil {
		return false, err
	}

	r.mu.Lock()
	defer r.mu.Unlock()

	if previousInstanceID := r.sessionToInstance[session]; previousInstanceID != "" {
		r.unregisterSessionLocked(session, previousInstanceID)
	}

	previous := r.instances[registration.InstanceID]
	replaced := previous != nil && previous.session != session
	if previous != nil {
		r.unregisterSessionLocked(previous.session, registration.InstanceID)
	}

	instance := &registeredUnityInstance{
		instanceID: registration.InstanceID,
		session:    session,
		npcs:       make(map[string]struct{}, len(registration.NPCs)),
		tools:      make(map[string]ToolDefinition, len(registration.Tools)),
	}
	for _, npcID := range registration.NPCs {
		instance.npcs[npcID] = struct{}{}
		r.npcToInstance[npcID] = registration.InstanceID
	}
	for _, tool := range registration.Tools {
		instance.tools[tool.Name] = tool
	}

	r.instances[registration.InstanceID] = instance
	r.sessionToInstance[session] = registration.InstanceID
	return replaced, nil
}

func (r *UnityRegistry) UnregisterSession(session *jsonRPCSession) {
	if session == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	instanceID := r.sessionToInstance[session]
	if instanceID == "" {
		return
	}
	r.unregisterSessionLocked(session, instanceID)
}

func (r *UnityRegistry) unregisterSessionLocked(session *jsonRPCSession, instanceID string) {
	instance := r.instances[instanceID]
	delete(r.sessionToInstance, session)
	if instance == nil || instance.session != session {
		return
	}
	for npcID := range instance.npcs {
		if r.npcToInstance[npcID] == instanceID {
			delete(r.npcToInstance, npcID)
		}
	}
	delete(r.instances, instanceID)
}

func (r *UnityRegistry) UpdateNPC(session *jsonRPCSession, change UnityNPCChangedParams) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	instance, err := r.ownedInstanceLocked(session, change.InstanceID)
	if err != nil {
		return err
	}
	if change.NPCID == "" {
		return fmt.Errorf("npcId is required")
	}
	if change.Online {
		instance.npcs[change.NPCID] = struct{}{}
		r.npcToInstance[change.NPCID] = change.InstanceID
		return nil
	}
	delete(instance.npcs, change.NPCID)
	if r.npcToInstance[change.NPCID] == change.InstanceID {
		delete(r.npcToInstance, change.NPCID)
	}
	return nil
}

func (r *UnityRegistry) UpdateTools(session *jsonRPCSession, change UnityToolsChangedParams) error {
	for _, tool := range change.Tools {
		if err := tool.Validate(); err != nil {
			return err
		}
	}

	r.mu.Lock()
	defer r.mu.Unlock()
	instance, err := r.ownedInstanceLocked(session, change.InstanceID)
	if err != nil {
		return err
	}
	instance.tools = make(map[string]ToolDefinition, len(change.Tools))
	for _, tool := range change.Tools {
		instance.tools[tool.Name] = tool
	}
	return nil
}

func (r *UnityRegistry) ownedInstanceLocked(session *jsonRPCSession, instanceID string) (*registeredUnityInstance, error) {
	if instanceID == "" {
		return nil, fmt.Errorf("instanceId is required")
	}
	instance := r.instances[instanceID]
	if instance == nil || instance.session != session {
		return nil, fmt.Errorf("session does not own Unity instance %q", instanceID)
	}
	return instance, nil
}

func (r *UnityRegistry) ResolveNPC(npcID string) (string, *jsonRPCSession, bool) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	instanceID := r.npcToInstance[npcID]
	instance := r.instances[instanceID]
	if instance == nil {
		return "", nil, false
	}
	return instanceID, instance.session, true
}

func (r *UnityRegistry) CapabilitiesForNPC(npcID string) (string, []ToolDefinition, bool) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	instanceID := r.npcToInstance[npcID]
	instance := r.instances[instanceID]
	if instance == nil {
		return "", nil, false
	}
	names := make([]string, 0, len(instance.tools))
	for name := range instance.tools {
		names = append(names, name)
	}
	sort.Strings(names)
	definitions := make([]ToolDefinition, 0, len(names))
	for _, name := range names {
		definitions = append(definitions, instance.tools[name])
	}
	return instanceID, definitions, true
}
func (r *UnityRegistry) HasTool(instanceID, toolName string) bool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	instance := r.instances[instanceID]
	if instance == nil {
		return false
	}
	_, ok := instance.tools[toolName]
	return ok
}

func (r *UnityRegistry) IsRegistered(session *jsonRPCSession) bool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return r.sessionToInstance[session] != ""
}

func (r *UnityRegistry) ListTools() []ToolDefinition {
	r.mu.RLock()
	defer r.mu.RUnlock()
	byName := make(map[string]ToolDefinition)
	for _, instance := range r.instances {
		for name, tool := range instance.tools {
			byName[name] = tool
		}
	}
	names := make([]string, 0, len(byName))
	for name := range byName {
		names = append(names, name)
	}
	sort.Strings(names)
	result := make([]ToolDefinition, 0, len(names))
	for _, name := range names {
		result = append(result, byName[name])
	}
	return result
}

func (r *UnityRegistry) Counts() (instances, npcs int) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return len(r.instances), len(r.npcToInstance)
}
