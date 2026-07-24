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

// NewUnityRegistry 创建空的在线实例、NPC 路由和能力注册表。
func NewUnityRegistry() *UnityRegistry {
	return &UnityRegistry{
		instances:         make(map[string]*registeredUnityInstance),
		npcToInstance:     make(map[string]string),
		sessionToInstance: make(map[*jsonRPCSession]string),
	}
}

// Register 原子替换实例的连接和完整能力快照，并返回是否取代了旧连接。
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

// UnregisterSession 仅清理由该连接实际拥有的实例，避免旧连接误删新注册。
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

// UpdateNPC 更新所属实例的 NPC 路由；调用连接必须拥有该实例。
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

// UpdateTools 校验并替换所属实例的完整工具能力快照。
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

// ResolveNPC 返回 NPC 当前所属实例和负责发送命令的连接 Session。
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

// CapabilitiesForNPC 返回 NPC 所属实例及按名称稳定排序的工具定义副本。
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

// HasTool 判断指定 Unity 实例当前是否声明了该工具。
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

// IsRegistered 判断连接是否已经完成 unity.register。
func (r *UnityRegistry) IsRegistered(session *jsonRPCSession) bool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return r.sessionToInstance[session] != ""
}

// ListTools 汇总所有在线实例的工具，并按名称去重和稳定排序。
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

// Counts 返回当前在线实例数和可路由 NPC 数。
func (r *UnityRegistry) Counts() (instances, npcs int) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return len(r.instances), len(r.npcToInstance)
}
