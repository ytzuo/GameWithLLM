// Package config loads local development configuration from environment variables
// and monorepo-level dotenv files.
package config

import (
	"bufio"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

const (
	defaultServerAddr         = ":8080"
	defaultBaseURL            = "http://127.0.0.1:8080"
	defaultUnityJSONRPCWSURL  = "ws://127.0.0.1:8080/unity/ws"
	defaultToolTimeoutSeconds = 10
	defaultLLMAPIURL          = "https://api.openai.com/v1/chat/completions"
	defaultLLMModel           = "gpt-4o-mini"
	defaultLLMTimeoutSeconds  = 60
	defaultLLMMaxRetries      = 2
	defaultLLMMaxToolRounds   = 4
	defaultLLMMaxContextChars = 32000
)

// Config contains runtime settings shared by local development tools.
type Config struct {
	ServerAddr              string
	BaseURL                 string
	UnityJSONRPCWSURL       string
	UnityToolTimeout        time.Duration
	UnityToolTimeoutSecond  int
	LLMAPIURL               string
	LLMAPIKey               string
	LLMModel                string
	LLMRequestTimeout       time.Duration
	LLMRequestTimeoutSecond int
	LLMMaxRetries           int
	LLMMaxToolRounds        int
	LLMMaxContextChars      int
}

// Load reads .env.local/.env while allowing real process environment variables
// to take precedence.
func Load() Config {
	values := loadDotEnvFiles()
	timeoutSeconds := intValue("UNITY_TOOL_TIMEOUT_SECONDS", values, defaultToolTimeoutSeconds)
	llmTimeoutSeconds := intValue("LLM_REQUEST_TIMEOUT_SECONDS", values, defaultLLMTimeoutSeconds)

	return Config{
		ServerAddr:              stringValue("AGENT_HOST_ADDR", values, defaultServerAddr),
		BaseURL:                 stringValue("AGENT_HOST_BASE_URL", values, defaultBaseURL),
		UnityJSONRPCWSURL:       stringValue("UNITY_JSONRPC_WS_URL", values, defaultUnityJSONRPCWSURL),
		UnityToolTimeout:        time.Duration(timeoutSeconds) * time.Second,
		UnityToolTimeoutSecond:  timeoutSeconds,
		LLMAPIURL:               stringValue("LLM_API_URL", values, defaultLLMAPIURL),
		LLMAPIKey:               stringValue("LLM_API_KEY", values, ""),
		LLMModel:                stringValue("LLM_MODEL", values, defaultLLMModel),
		LLMRequestTimeout:       time.Duration(llmTimeoutSeconds) * time.Second,
		LLMRequestTimeoutSecond: llmTimeoutSeconds,
		LLMMaxRetries:           nonNegativeIntValue("LLM_MAX_RETRIES", values, defaultLLMMaxRetries),
		LLMMaxToolRounds:        intValue("LLM_MAX_TOOL_ROUNDS", values, defaultLLMMaxToolRounds),
		LLMMaxContextChars:      intValue("LLM_MAX_CONTEXT_CHARS", values, defaultLLMMaxContextChars),
	}
}

func stringValue(key string, values map[string]string, fallback string) string {
	if value := strings.TrimSpace(os.Getenv(key)); value != "" {
		return value
	}
	if value := strings.TrimSpace(values[key]); value != "" {
		return value
	}
	return fallback
}

func intValue(key string, values map[string]string, fallback int) int {
	value := stringValue(key, values, "")
	if value == "" {
		return fallback
	}
	parsed, err := strconv.Atoi(value)
	if err != nil || parsed <= 0 {
		return fallback
	}
	return parsed
}

func nonNegativeIntValue(key string, values map[string]string, fallback int) int {
	value := stringValue(key, values, "")
	if value == "" {
		return fallback
	}
	parsed, err := strconv.Atoi(value)
	if err != nil || parsed < 0 {
		return fallback
	}
	return parsed
}

func loadDotEnvFiles() map[string]string {
	values := map[string]string{}
	root, ok := findRepoRoot()
	if !ok {
		return values
	}

	for _, name := range []string{".env", ".env.local"} {
		path := filepath.Join(root, name)
		fileValues, err := readDotEnv(path)
		if err != nil {
			continue
		}
		for key, value := range fileValues {
			values[key] = value
		}
	}
	return values
}

func findRepoRoot() (string, bool) {
	wd, err := os.Getwd()
	if err != nil {
		return "", false
	}

	for {
		if exists(filepath.Join(wd, "GameMCPServer")) && exists(filepath.Join(wd, "unity-NPC-agent-client")) {
			return wd, true
		}

		parent := filepath.Dir(wd)
		if parent == wd {
			return "", false
		}
		wd = parent
	}
}

func exists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

func readDotEnv(path string) (map[string]string, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()

	values := map[string]string{}
	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		key, value, ok := strings.Cut(line, "=")
		if !ok {
			return nil, fmt.Errorf("invalid dotenv line in %s: %s", path, line)
		}
		key = strings.TrimSpace(key)
		value = strings.TrimSpace(value)
		value = strings.Trim(value, `"'`)
		if key != "" {
			values[key] = value
		}
	}
	return values, scanner.Err()
}
