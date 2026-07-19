package tools

import "encoding/json"

// Definition 是 Agent 可见的工具契约，实际能力仍由 Unity 注册。
type Definition struct {
	Name        string          `json:"name"`
	Description string          `json:"description,omitempty"`
	InputSchema json.RawMessage `json:"inputSchema"`
}

func Find(definitions []Definition, name string) (Definition, bool) {
	for _, definition := range definitions {
		if definition.Name == name {
			return definition, true
		}
	}
	return Definition{}, false
}
