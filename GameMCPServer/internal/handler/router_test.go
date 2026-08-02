package handler

import (
	"net/http"
	"net/http/httptest"
	"testing"

	"GameMCPServer/internal/config"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func TestRoutesExposeA2AAndRemoveV2WebSocket(t *testing.T) {
	cfg := config.Load()
	cfg.A2ABearerToken = "token"
	cfg.RuntimeGatewayToken = "runtime-token"
	cfg.GatewayServiceToken = "service-token"
	mux := http.NewServeMux()
	_, err := RegisterRoutesWithConfig(mux, cfg)
	require.NoError(t, err)

	card := httptest.NewRecorder()
	mux.ServeHTTP(card, httptest.NewRequest(http.MethodGet, "/.well-known/agent-card.json", nil))
	assert.Equal(t, http.StatusOK, card.Code)

	legacy := httptest.NewRecorder()
	mux.ServeHTTP(legacy, httptest.NewRequest(http.MethodGet, "/unity/ws", nil))
	assert.Equal(t, http.StatusNotFound, legacy.Code)

	a2a := httptest.NewRecorder()
	mux.ServeHTTP(a2a, httptest.NewRequest(http.MethodPost, "/a2a", nil))
	assert.Equal(t, http.StatusUnauthorized, a2a.Code)
}
