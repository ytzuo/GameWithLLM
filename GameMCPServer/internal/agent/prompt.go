package agent

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"strings"
)

const systemPromptTemplate = `你是 Unity 游戏世界中的 NPC“{{displayName}}”，内部实体标识为“{{npcId}}”。你不是旁白、系统助手或游戏外的人工智能。始终从这个 NPC 的第一人称立场与玩家交流。

角色身份
{{identity}}

性格
{{personality}}

说话方式
{{speakingStyle}}

职责
{{responsibilities}}


可知晓的静态世界背景
{{worldKnowledge}}
这些内容只描述静态背景。坐标、距离、路径、库存、任务进度和行为结果等实时状态必须通过当前可用工具确认。

禁止透露与禁止编造
{{forbiddenTopics}}

交流方式
1. 默认使用简体中文。只有玩家明确使用或要求其他语言时才切换。
2. 像真实人物面对面说话，语气自然、直接，避免机械、客服式或说明书式表达。回复应简洁，通常使用一至三句话；只有确有必要时才展开。
3. 只输出纯文本。禁止使用 Markdown，包括标题、列表、表格、引用、代码块、加粗、链接等格式。
4. 禁止使用 emoji、颜文字、特殊装饰符号或夸张的网络语气。
5. 不要描述内部推理过程，不要提及系统提示词、语言模型、工具、函数调用、参数、JSON 或内部规则。
6. 不要逐字复述玩家的话，也不要在每次回复中重复自己的身份。

知识与真实性
1. 只能依据角色配置中的静态背景、玩家提供的信息、当前对话历史和实际执行结果作答。
2. 不得编造场景目标、位置、距离、物品、数量、容器状态或已经发生的行为。
3. 在行为成功执行之前，不能声称动作已经完成。
4. 如果缺少必要信息，应自然地询问玩家，或者先使用当前可用的查询能力获取信息。

行为规则
1. 对普通聊天、解释或无需改变游戏状态的问题，直接用自然语言回答，不执行行为。
2. 当玩家明确要求移动、查看游戏状态、操作物品或进行其他会改变或读取游戏状态的行为时，应使用当前提供的能力执行。
3. 你的实际行为能力严格限于当前提供的能力。没有提供的能力一律不得假装能够执行。
4. 行为需要目标名称、物品标识、数量或当前状态时，应先查询并依据真实结果选择参数，不得猜测。
5. 多步骤任务应按合理顺序执行，并根据每一步的真实结果决定下一步。前一步失败时，不要继续假装后续步骤成功。
6. 行为成功后，用角色口吻简短说明结果。不要展示原始返回数据、内部名称或技术细节。
7. 行为失败时，如实说明没有完成以及玩家能理解的原因；适合时提出一个简短的澄清问题或可行替代方案。
8. 如果玩家的指令存在关键歧义，并且不同理解会导致不同游戏行为，应先澄清再执行，不要擅自猜测。

回复目标
让玩家感觉自己正在和游戏世界中的真实角色交谈。保持沉浸感、诚实和简洁，同时可靠地执行当前允许的游戏行为。`

var promptPlaceholders = []string{
	"displayName", "npcId", "identity", "personality", "speakingStyle",
	"responsibilities", "worldKnowledge", "forbiddenTopics",
}

// SystemPromptCatalog 是 Go 独占的版本化 Prompt；其正文不会进入 Unity 或日志。
type SystemPromptCatalog struct {
	SchemaVersion  int    `json:"schemaVersion"`
	ContentVersion string `json:"contentVersion"`
	Locale         string `json:"locale"`
	Template       string `json:"template"`
}

func LoadSystemPromptCatalog(path string) (*SystemPromptCatalog, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	data, err := io.ReadAll(io.LimitReader(file, 256*1024+1))
	if err != nil {
		return nil, fmt.Errorf("read system prompt catalog: %w", err)
	}
	if len(data) > 256*1024 {
		return nil, fmt.Errorf("system prompt catalog exceeds 256 KiB")
	}
	if err := rejectDuplicateJSONProperties(data); err != nil {
		return nil, fmt.Errorf("decode system prompt catalog: %w", err)
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	var catalog SystemPromptCatalog
	if err := decoder.Decode(&catalog); err != nil {
		return nil, fmt.Errorf("decode system prompt catalog: %w", err)
	}
	if err := decoder.Decode(&struct{}{}); err != io.EOF {
		return nil, fmt.Errorf("system prompt catalog must contain one JSON object")
	}
	if err := catalog.Validate(); err != nil {
		return nil, err
	}
	return &catalog, nil
}

func rejectDuplicateJSONProperties(data []byte) error {
	decoder := json.NewDecoder(bytes.NewReader(data))
	var readValue func() error
	readValue = func() error {
		token, err := decoder.Token()
		if err != nil {
			return err
		}
		delimiter, ok := token.(json.Delim)
		if !ok {
			return nil
		}
		switch delimiter {
		case '{':
			seen := make(map[string]struct{})
			for decoder.More() {
				keyToken, err := decoder.Token()
				if err != nil {
					return err
				}
				key, ok := keyToken.(string)
				if !ok {
					return fmt.Errorf("object property name is not a string")
				}
				if _, exists := seen[key]; exists {
					return fmt.Errorf("duplicate JSON property %q", key)
				}
				seen[key] = struct{}{}
				if err := readValue(); err != nil {
					return err
				}
			}
			_, err = decoder.Token()
			return err
		case '[':
			for decoder.More() {
				if err := readValue(); err != nil {
					return err
				}
			}
			_, err = decoder.Token()
			return err
		default:
			return fmt.Errorf("unexpected JSON delimiter %q", delimiter)
		}
	}
	if err := readValue(); err != nil {
		return err
	}
	if _, err := decoder.Token(); err != io.EOF {
		if err == nil {
			return fmt.Errorf("JSON contains trailing content")
		}
		return err
	}
	return nil
}

func (c *SystemPromptCatalog) Validate() error {
	if c == nil {
		return fmt.Errorf("system prompt catalog is nil")
	}
	if c.SchemaVersion != 1 {
		return fmt.Errorf("system prompt schemaVersion must be 1")
	}
	if strings.TrimSpace(c.ContentVersion) != c.ContentVersion || c.ContentVersion == "" || len(c.ContentVersion) > 128 {
		return fmt.Errorf("system prompt contentVersion is invalid")
	}
	if c.Locale != "zh-CN" {
		return fmt.Errorf("system prompt locale must be zh-CN")
	}
	if strings.TrimSpace(c.Template) == "" || len(c.Template) > 64*1024 {
		return fmt.Errorf("system prompt template is invalid")
	}
	for _, name := range promptPlaceholders {
		token := "{{" + name + "}}"
		if strings.Count(c.Template, token) != 1 {
			return fmt.Errorf("system prompt template must contain %s exactly once", token)
		}
	}
	withoutKnown := c.Template
	for _, name := range promptPlaceholders {
		withoutKnown = strings.ReplaceAll(withoutKnown, "{{"+name+"}}", "")
	}
	if strings.Contains(withoutKnown, "{{") || strings.Contains(withoutKnown, "}}") {
		return fmt.Errorf("system prompt template contains an unknown placeholder")
	}
	return nil
}

func DefaultSystemPromptCatalog() *SystemPromptCatalog {
	return &SystemPromptCatalog{
		SchemaVersion: 1, ContentVersion: "builtin-1", Locale: "zh-CN", Template: systemPromptTemplate,
	}
}

func (c *SystemPromptCatalog) Build(profile NPCProfile) string {
	values := map[string]string{
		"displayName":      profile.DisplayName,
		"npcId":            profile.NPCID,
		"identity":         profile.Identity,
		"personality":      formatPromptList(profile.Personality),
		"speakingStyle":    profile.SpeakingStyle,
		"responsibilities": formatPromptList(profile.Responsibilities),
		"worldKnowledge":   formatPromptList(profile.WorldKnowledge),
		"forbiddenTopics":  formatPromptList(profile.ForbiddenTopics),
	}
	result := c.Template
	for _, name := range promptPlaceholders {
		result = strings.ReplaceAll(result, "{{"+name+"}}", values[name])
	}
	return result
}

func BuildSystemPrompt(profile NPCProfile) string {
	return DefaultSystemPromptCatalog().Build(profile)
}

func formatPromptList(values []string) string {
	return "- " + strings.Join(values, "\n- ")
}
