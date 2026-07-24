package agent

import "unicode/utf8"

const defaultMaxContextChars = 32000

func trimConversationMessages(messages []Message, maxChars int) []Message {
	if maxChars <= 0 || len(messages) <= 1 {
		return append([]Message(nil), messages...)
	}

	prefixEnd := 0
	for prefixEnd < len(messages) && messages[prefixEnd].Role == "system" {
		prefixEnd++
	}

	turns := make([][]Message, 0)
	for _, message := range messages[prefixEnd:] {
		if message.Role == "user" || len(turns) == 0 {
			turns = append(turns, []Message{message})
			continue
		}
		turns[len(turns)-1] = append(turns[len(turns)-1], message)
	}
	if len(turns) == 0 {
		return append([]Message(nil), messages...)
	}

	used := messagesContextChars(messages[:prefixEnd])
	selectedStart := len(turns)
	for index := len(turns) - 1; index >= 0; index-- {
		turnChars := messagesContextChars(turns[index])
		if selectedStart != len(turns) && used+turnChars > maxChars {
			break
		}
		selectedStart = index
		used += turnChars
	}

	result := make([]Message, 0, prefixEnd+len(messages))
	result = append(result, messages[:prefixEnd]...)
	for _, turn := range turns[selectedStart:] {
		result = append(result, turn...)
	}
	return result
}

func messagesContextChars(messages []Message) int {
	total := 0
	for _, message := range messages {
		total += utf8.RuneCountInString(message.Role)
		total += utf8.RuneCountInString(message.Content)
		total += utf8.RuneCountInString(message.ToolCallID)
		for _, call := range message.ToolCalls {
			total += utf8.RuneCountInString(call.ID)
			total += utf8.RuneCountInString(call.Name)
			total += utf8.RuneCount(call.Arguments)
		}
	}
	return total
}
