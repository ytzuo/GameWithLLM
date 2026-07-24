package config

import (
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
)

func TestLoad_AgentHostAndLLMConfiguration(t *testing.T) {
	t.Setenv("AGENT_HOST_ADDR", "127.0.0.1:19090")
	t.Setenv("AGENT_HOST_BASE_URL", "http://127.0.0.1:19090")
	t.Setenv("LLM_API_URL", "http://llm.test/v1")
	t.Setenv("LLM_API_KEY", "test-key")
	t.Setenv("LLM_MODEL", "model-test")
	t.Setenv("LLM_REQUEST_TIMEOUT_SECONDS", "12")
	t.Setenv("LLM_MAX_TOOL_ROUNDS", "3")
	t.Setenv("LLM_MAX_RETRIES", "1")
	t.Setenv("LLM_MAX_CONTEXT_CHARS", "12345")

	cfg := Load()
	assert.Equal(t, "127.0.0.1:19090", cfg.ServerAddr)
	assert.Equal(t, "http://127.0.0.1:19090", cfg.BaseURL)
	assert.Equal(t, "http://llm.test/v1", cfg.LLMAPIURL)
	assert.Equal(t, "test-key", cfg.LLMAPIKey)
	assert.Equal(t, "model-test", cfg.LLMModel)
	assert.Equal(t, 12*time.Second, cfg.LLMRequestTimeout)
	assert.Equal(t, 3, cfg.LLMMaxToolRounds)
	assert.Equal(t, 1, cfg.LLMMaxRetries)
	assert.Equal(t, 12345, cfg.LLMMaxContextChars)
}
