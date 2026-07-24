package tools

import (
	"encoding/json"
	"fmt"
)

// Policy 限制单条玩家消息允许的工具轮数，并校验模型只能调用当前 NPC 已声明的能力。
type Policy struct {
	MaxToolRounds int
}

// NewPolicy 创建工具策略；非正数轮数回退为默认值 4。
func NewPolicy(maxToolRounds int) Policy {
	if maxToolRounds <= 0 {
		maxToolRounds = 4
	}
	return Policy{MaxToolRounds: maxToolRounds}
}

// Authorize 验证工具存在于当前能力快照，并按其运行时 Schema 校验参数。
func (p Policy) Authorize(definitions []Definition, name string, arguments json.RawMessage) error {
	definition, ok := Find(definitions, name)
	if !ok {
		return fmt.Errorf("tool %q is not available for this NPC", name)
	}
	return ValidateArguments(definition, arguments)
}
