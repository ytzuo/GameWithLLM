package gateway

import (
	"bytes"
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"

	"GameMCPServer/internal/mcp"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestRegistryConnectionGenerationAndIsolation(t *testing.T) {
	registry := NewRegistry()
	first := newRuntimeSession(context.Background(), nil, Manifest{
		InstanceID: "runtime-a",
		Tools:      []mcp.Tool{{Name: "tool-a"}},
	})
	second := newRuntimeSession(context.Background(), nil, Manifest{InstanceID: "runtime-a"})
	other := newRuntimeSession(context.Background(), nil, Manifest{InstanceID: "runtime-b"})
	assert.Equal(t, uint64(1), registry.register(first))
	assert.Equal(t, uint64(2), registry.register(second))
	assert.True(t, first.closed.Load())
	assert.Equal(t, uint64(1), registry.register(other))
	resolved, ok := registry.ResolveClient("runtime-a")
	require.True(t, ok)
	assert.Same(t, second, resolved)
	resolved, ok = registry.ResolveClient("runtime-b")
	require.True(t, ok)
	assert.Same(t, other, resolved)
}

func TestVirtualMCPRequiresServiceIdentity(t *testing.T) {
	server := NewServer(NewRegistry(), "runtime-token", "service-token")
	body := bytes.NewBufferString(`{"jsonrpc":"2.0","id":"1","method":"initialize"}`)
	request := httptest.NewRequest(http.MethodPost, "/mcp/runtimes/runtime-a", body)
	response := httptest.NewRecorder()
	server.HandleVirtualMCP(response, request)
	assert.Equal(t, http.StatusUnauthorized, response.Code)

	request = httptest.NewRequest(http.MethodPost, "/mcp/runtimes/runtime-a", bytes.NewBufferString(`{"jsonrpc":"2.0","id":"1","method":"initialize"}`))
	request.Header.Set("Authorization", "Bearer service-token")
	response = httptest.NewRecorder()
	server.HandleVirtualMCP(response, request)
	assert.Contains(t, response.Body.String(), "runtime unavailable")
}

func TestVirtualMCPRejectsBrowserOrigin(t *testing.T) {
	server := NewServer(NewRegistry(), "runtime-token", "service-token")
	request := httptest.NewRequest(http.MethodPost, "/mcp/runtimes/runtime-a", bytes.NewBuffer(nil))
	request.Header.Set("Authorization", "Bearer service-token")
	request.Header.Set("Origin", "https://untrusted.example")
	response := httptest.NewRecorder()

	server.HandleVirtualMCP(response, request)

	assert.Equal(t, http.StatusForbidden, response.Code)
}

func TestValidateManifestRejectsInvalidSchemasAndDuplicates(t *testing.T) {
	assert.Error(t, validateManifest(Manifest{InstanceID: "runtime-a", Entities: []string{"npc-1", "npc-1"}}))
	assert.Error(t, validateManifest(Manifest{InstanceID: "runtime-a", Tools: []mcp.Tool{{Name: "move", InputSchema: json.RawMessage([]byte{'[', ']'})}}}))
	assert.NoError(t, validateManifest(Manifest{InstanceID: "runtime-a", Entities: []string{"npc-1"}, Tools: []mcp.Tool{{Name: "move", InputSchema: json.RawMessage([]byte{'{', '}'})}}}))
}
