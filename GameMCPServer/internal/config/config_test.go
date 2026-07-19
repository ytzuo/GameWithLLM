package config

import (
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
)

func TestLoad_LLMConfiguration(t *testing.T) {
	t.Setenv("LLM_API_URL", "http://llm.test/v1")
	t.Setenv("LLM_API_KEY", "preferred-key")
	t.Setenv("OPENAI_API_KEY", "legacy-key")
	t.Setenv("LLM_MODEL", "model-test")
	t.Setenv("LLM_REQUEST_TIMEOUT_SECONDS", "12")
	t.Setenv("LLM_MAX_TOOL_ROUNDS", "3")

	cfg := Load()
	assert.Equal(t, "http://llm.test/v1", cfg.LLMAPIURL)
	assert.Equal(t, "preferred-key", cfg.LLMAPIKey)
	assert.Equal(t, "model-test", cfg.LLMModel)
	assert.Equal(t, 12*time.Second, cfg.LLMRequestTimeout)
	assert.Equal(t, 3, cfg.LLMMaxToolRounds)
}

func TestLoad_LegacyOpenAIKeyFallback(t *testing.T) {
	t.Setenv("LLM_API_KEY", "")
	t.Setenv("OPENAI_API_KEY", "legacy-key")

	cfg := Load()
	assert.Equal(t, "legacy-key", cfg.LLMAPIKey)
}
