package tools

import (
	"encoding/json"
	"fmt"
)

// ValidateArguments 实现当前工具契约需要的最小 JSON Schema 校验子集。
func ValidateArguments(definition Definition, arguments json.RawMessage) error {
	var values map[string]any
	if err := json.Unmarshal(arguments, &values); err != nil || values == nil {
		return fmt.Errorf("arguments must be a JSON object")
	}

	var schema struct {
		Type       string   `json:"type"`
		Required   []string `json:"required"`
		Properties map[string]struct {
			Type string `json:"type"`
			Enum []any  `json:"enum"`
		} `json:"properties"`
	}
	if err := json.Unmarshal(definition.InputSchema, &schema); err != nil {
		return fmt.Errorf("invalid schema for tool %q: %w", definition.Name, err)
	}
	if schema.Type != "" && schema.Type != "object" {
		return fmt.Errorf("unsupported root schema type %q", schema.Type)
	}
	for _, required := range schema.Required {
		if value, ok := values[required]; !ok || value == nil {
			return fmt.Errorf("required argument %q is missing", required)
		}
	}
	for name, property := range schema.Properties {
		value, ok := values[name]
		if !ok {
			continue
		}
		if property.Type == "string" {
			if _, ok := value.(string); !ok {
				return fmt.Errorf("argument %q must be a string", name)
			}
		}
		if len(property.Enum) > 0 && !containsJSONValue(property.Enum, value) {
			return fmt.Errorf("argument %q is not one of the allowed values", name)
		}
	}
	return nil
}

func containsJSONValue(values []any, target any) bool {
	targetJSON, _ := json.Marshal(target)
	for _, value := range values {
		valueJSON, _ := json.Marshal(value)
		if string(valueJSON) == string(targetJSON) {
			return true
		}
	}
	return false
}
