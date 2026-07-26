package agent

import (
	"strings"
	"testing"

	"github.com/stretchr/testify/assert"
)

func TestBuildSystemPromptIncludesIdentityAndCoreRules(t *testing.T) {
	prompt := BuildSystemPrompt(testNPCProfile("Ryan_001"))
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
func TestBuildSystemPromptUsesDistinctProfilesAndSharedResponsibilities(t *testing.T) {
	ryan := testNPCProfile("Ryan_001")
	ryan.DisplayName = "Ryan"
	ryan.Personality = []string{"沉稳"}
	ryan.Responsibilities = []string{"执行移动任务", "管理游戏物品"}
	alice := testNPCProfile("Alice_001")
	alice.DisplayName = "Alice"
	alice.Personality = []string{"友善"}
	alice.Responsibilities = append([]string(nil), ryan.Responsibilities...)

	ryanPrompt := BuildSystemPrompt(ryan)
	alicePrompt := BuildSystemPrompt(alice)
	assert.NotEqual(t, ryanPrompt, alicePrompt)
	assert.Contains(t, ryanPrompt, "- 执行移动任务")
	assert.Contains(t, alicePrompt, "- 管理游戏物品")
	assert.Contains(t, ryanPrompt, "你的实际行为能力严格限于当前提供的能力")
}
