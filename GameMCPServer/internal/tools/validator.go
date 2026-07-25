package tools

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"regexp"
	"unicode/utf8"
)

type schemaNode struct {
	Type                 string                `json:"type"`
	Properties           map[string]schemaNode `json:"properties"`
	Required             []string              `json:"required"`
	AdditionalProperties *bool                 `json:"additionalProperties"`
	Items                *schemaNode           `json:"items"`
	Enum                 []any                 `json:"enum"`
	Minimum              *float64              `json:"minimum"`
	Maximum              *float64              `json:"maximum"`
	MinLength            *int                  `json:"minLength"`
	MaxLength            *int                  `json:"maxLength"`
	Pattern              string                `json:"pattern"`
	MinItems             *int                  `json:"minItems"`
	MaxItems             *int                  `json:"maxItems"`
	UniqueItems          bool                  `json:"uniqueItems"`
}

// ValidateArguments validates the JSON Schema subset emitted by Unity's
// type-driven ToolContract. Unity remains the only source of tool schemas.
func ValidateArguments(definition Definition, arguments json.RawMessage) error {
	var schema schemaNode
	if err := decodeJSON(definition.InputSchema, &schema); err != nil {
		return fmt.Errorf("invalid schema for tool %q: %w", definition.Name, err)
	}
	if schema.Type != "object" {
		return fmt.Errorf("invalid schema for tool %q: root type must be object", definition.Name)
	}

	var value any
	if err := decodeJSON(arguments, &value); err != nil {
		return fmt.Errorf("arguments must be a JSON object: %w", err)
	}
	if _, ok := value.(map[string]any); !ok {
		return fmt.Errorf("arguments must be a JSON object")
	}
	if err := validateSchemaValue(schema, value, "$"); err != nil {
		return err
	}
	return nil
}

func decodeJSON(raw json.RawMessage, target any) error {
	decoder := json.NewDecoder(bytes.NewReader(raw))
	decoder.UseNumber()
	if err := decoder.Decode(target); err != nil {
		return err
	}
	if decoder.More() {
		return fmt.Errorf("multiple JSON values are not allowed")
	}
	var trailing any
	if err := decoder.Decode(&trailing); err == nil {
		return fmt.Errorf("multiple JSON values are not allowed")
	} else if err != io.EOF {
		return err
	}
	return nil
}

func validateSchemaValue(schema schemaNode, value any, path string) error {
	if !matchesSchemaType(schema.Type, value) {
		return fmt.Errorf("%s must be %s", path, describeSchemaType(schema.Type))
	}
	if len(schema.Enum) > 0 && !containsJSONValue(schema.Enum, value) {
		return fmt.Errorf("%s is not one of the allowed values", path)
	}

	switch schema.Type {
	case "object":
		return validateObject(schema, value.(map[string]any), path)
	case "array":
		return validateArray(schema, value.([]any), path)
	case "string":
		return validateString(schema, value.(string), path)
	case "integer", "number":
		return validateNumber(schema, value.(json.Number), path)
	case "boolean":
		return nil
	default:
		return fmt.Errorf("%s uses unsupported schema type %q", path, schema.Type)
	}
}

func validateObject(schema schemaNode, value map[string]any, path string) error {
	for _, name := range schema.Required {
		property, ok := value[name]
		if !ok || property == nil {
			return fmt.Errorf("required argument %q is missing", name)
		}
	}
	for name, property := range value {
		propertySchema, known := schema.Properties[name]
		if !known {
			if schema.AdditionalProperties != nil && !*schema.AdditionalProperties {
				return fmt.Errorf("argument %q is not allowed", name)
			}
			continue
		}
		if err := validateSchemaValue(propertySchema, property, path+"."+name); err != nil {
			return err
		}
	}
	return nil
}

func validateArray(schema schemaNode, value []any, path string) error {
	if schema.MinItems != nil && len(value) < *schema.MinItems {
		return fmt.Errorf("%s must contain at least %d items", path, *schema.MinItems)
	}
	if schema.MaxItems != nil && len(value) > *schema.MaxItems {
		return fmt.Errorf("%s must contain at most %d items", path, *schema.MaxItems)
	}
	if schema.UniqueItems {
		for i := range value {
			for j := i + 1; j < len(value); j++ {
				if jsonValuesEqual(value[i], value[j]) {
					return fmt.Errorf("%s must not contain duplicate items", path)
				}
			}
		}
	}
	if schema.Items != nil {
		for index, item := range value {
			if err := validateSchemaValue(*schema.Items, item, fmt.Sprintf("%s[%d]", path, index)); err != nil {
				return err
			}
		}
	}
	return nil
}

func validateString(schema schemaNode, value, path string) error {
	length := utf8.RuneCountInString(value)
	if schema.MinLength != nil && length < *schema.MinLength {
		return fmt.Errorf("%s length must be at least %d", path, *schema.MinLength)
	}
	if schema.MaxLength != nil && length > *schema.MaxLength {
		return fmt.Errorf("%s length must be at most %d", path, *schema.MaxLength)
	}
	if schema.Pattern != "" {
		pattern, err := regexp.Compile(schema.Pattern)
		if err != nil {
			return fmt.Errorf("%s uses invalid schema pattern: %w", path, err)
		}
		if !pattern.MatchString(value) {
			return fmt.Errorf("%s does not match the required pattern", path)
		}
	}
	return nil
}

func validateNumber(schema schemaNode, value json.Number, path string) error {
	number, err := value.Float64()
	if err != nil || math.IsNaN(number) || math.IsInf(number, 0) {
		return fmt.Errorf("%s must be a finite number", path)
	}
	if schema.Type == "integer" && math.Trunc(number) != number {
		return fmt.Errorf("%s must be an integer", path)
	}
	if schema.Minimum != nil && number < *schema.Minimum {
		return fmt.Errorf("%s must be at least %v", path, *schema.Minimum)
	}
	if schema.Maximum != nil && number > *schema.Maximum {
		return fmt.Errorf("%s must be at most %v", path, *schema.Maximum)
	}
	return nil
}

func matchesSchemaType(schemaType string, value any) bool {
	switch schemaType {
	case "object":
		_, ok := value.(map[string]any)
		return ok
	case "array":
		_, ok := value.([]any)
		return ok
	case "string":
		_, ok := value.(string)
		return ok
	case "boolean":
		_, ok := value.(bool)
		return ok
	case "integer":
		number, ok := value.(json.Number)
		if !ok {
			return false
		}
		parsed, err := number.Float64()
		return err == nil && math.Trunc(parsed) == parsed
	case "number":
		_, ok := value.(json.Number)
		return ok
	default:
		return false
	}
}

func describeSchemaType(schemaType string) string {
	switch schemaType {
	case "object":
		return "a JSON object"
	case "array":
		return "an array"
	case "string":
		return "a string"
	case "boolean":
		return "a boolean"
	case "integer":
		return "an integer"
	case "number":
		return "a number"
	default:
		return fmt.Sprintf("schema type %q", schemaType)
	}
}

func containsJSONValue(values []any, target any) bool {
	for _, value := range values {
		if jsonValuesEqual(value, target) {
			return true
		}
	}
	return false
}

func jsonValuesEqual(left, right any) bool {
	leftJSON, leftErr := json.Marshal(left)
	rightJSON, rightErr := json.Marshal(right)
	return leftErr == nil && rightErr == nil && bytes.Equal(leftJSON, rightJSON)
}
