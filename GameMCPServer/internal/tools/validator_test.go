package tools

import (
	"encoding/json"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestValidateArguments(t *testing.T) {
	definition := Definition{
		Name: "move",
		InputSchema: json.RawMessage(`{
			"type":"object",
			"properties":{
				"targetId":{"type":"string","minLength":1,"pattern":"\\S"},
				"approachDistance":{"type":"number","minimum":0,"maximum":10},
				"categories":{
					"type":"array",
					"items":{"type":"string","enum":["npc","player","landmark"]},
					"uniqueItems":true
				},
				"quantity":{"type":"integer","minimum":1}
			},
			"required":["targetId"],
			"additionalProperties":false
		}`),
	}

	tests := []struct {
		name      string
		arguments string
		errorText string
	}{
		{"valid", `{"targetId":"landmark:gate","approachDistance":1.5,"categories":["landmark"],"quantity":2}`, ""},
		{"valid optional defaults", `{"targetId":"landmark:gate"}`, ""},
		{"missing required", `{}`, "required"},
		{"unknown property", `{"targetId":"gate","extra":true}`, "not allowed"},
		{"wrong root", `[]`, "object"},
		{"wrong string type", `{"targetId":7}`, "string"},
		{"blank string", `{"targetId":" "}`, "pattern"},
		{"below minimum", `{"targetId":"gate","approachDistance":-1}`, "at least"},
		{"above maximum", `{"targetId":"gate","approachDistance":11}`, "at most"},
		{"wrong array type", `{"targetId":"gate","categories":"npc"}`, "array"},
		{"wrong item type", `{"targetId":"gate","categories":[1]}`, "string"},
		{"enum", `{"targetId":"gate","categories":["vehicle"]}`, "allowed"},
		{"duplicate array item", `{"targetId":"gate","categories":["npc","npc"]}`, "duplicate"},
		{"integer", `{"targetId":"gate","quantity":1.5}`, "integer"},
		{"integer minimum", `{"targetId":"gate","quantity":0}`, "at least"},
		{"null", `{"targetId":null}`, "required"},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			err := ValidateArguments(definition, json.RawMessage(test.arguments))
			if test.errorText == "" {
				require.NoError(t, err)
				return
			}
			assert.ErrorContains(t, err, test.errorText)
		})
	}
}

func TestValidateArgumentsRejectsInvalidSchema(t *testing.T) {
	tests := []struct {
		name   string
		schema string
	}{
		{"malformed", `{`},
		{"non object root", `{"type":"array","items":{"type":"string"}}`},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			err := ValidateArguments(
				Definition{Name: "invalid", InputSchema: json.RawMessage(test.schema)},
				json.RawMessage(`{}`))
			assert.ErrorContains(t, err, "invalid schema")
		})
	}
}
