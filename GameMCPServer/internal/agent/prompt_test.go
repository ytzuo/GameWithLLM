package agent

import (
	"strings"
	"testing"
)

func TestBuildSystemPromptIncludesIdentityAndCoreRules(t *testing.T) {
	prompt := BuildSystemPrompt("Ryan_001")
	required := []string{
		"Ryan_001",
		"只输出纯文本",
		"禁止使用 Markdown",
		"禁止使用 emoji",
		"不能声称动作已经完成",
		"不得假装能够执行",
	}
	for _, value := range required {
		if !strings.Contains(prompt, value) {
			t.Errorf("system prompt does not include %q", value)
		}
	}
}
