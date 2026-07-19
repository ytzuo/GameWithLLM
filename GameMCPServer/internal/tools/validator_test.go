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
			"properties":{"target":{"type":"string","enum":["gate","warehouse"]}},
			"required":["target"]
		}`),
	}

	require.NoError(t, ValidateArguments(definition, json.RawMessage(`{"target":"gate"}`)))
	assert.ErrorContains(t, ValidateArguments(definition, json.RawMessage(`{}`)), "required")
	assert.ErrorContains(t, ValidateArguments(definition, json.RawMessage(`{"target":"other"}`)), "allowed")
	assert.ErrorContains(t, ValidateArguments(definition, json.RawMessage(`[]`)), "object")
}
