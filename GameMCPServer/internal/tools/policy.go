package tools

import (
	"encoding/json"
	"fmt"
)

type Policy struct {
	MaxToolRounds int
}

func NewPolicy(maxToolRounds int) Policy {
	if maxToolRounds <= 0 {
		maxToolRounds = 4
	}
	return Policy{MaxToolRounds: maxToolRounds}
}

func (p Policy) Authorize(definitions []Definition, name string, arguments json.RawMessage) error {
	definition, ok := Find(definitions, name)
	if !ok {
		return fmt.Errorf("tool %q is not available for this NPC", name)
	}
	return ValidateArguments(definition, arguments)
}
