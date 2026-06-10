package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/gorilla/websocket"

	"GameMCPServer/internal/unity"
)

func main() {
	serverURL := flag.String("server", "ws://127.0.0.1:8080/unity/ws", "GameMCPServer Unity WebSocket endpoint")
	delay := flag.Duration("delay", 100*time.Millisecond, "simulated command execution delay")
	failCommand := flag.String("fail-command", "", "tool name that should return a simulated failure")
	missingNPC := flag.String("missing-npc", "", "npc_id that should return npc_not_found")
	clientID := flag.String("client-id", "mock-unity", "mock Unity client id")
	flag.Parse()

	conn, _, err := websocket.DefaultDialer.Dial(*serverURL, nil)
	if err != nil {
		log.Fatalf("connect %s: %v", *serverURL, err)
	}
	defer conn.Close()

	if err := conn.WriteJSON(unity.HelloMessage{
		Type:         unity.MessageTypeHello,
		ClientID:     *clientID,
		Capabilities: []string{"get_npc_status", "get_npc_position", "move_to", "say"},
	}); err != nil {
		log.Fatalf("send hello: %v", err)
	}
	log.Printf("mockUnity connected: %s", *serverURL)

	done := make(chan struct{})
	go func() {
		defer close(done)
		for {
			_, payload, err := conn.ReadMessage()
			if err != nil {
				log.Printf("read: %v", err)
				return
			}
			if err := handleMessage(conn, payload, *delay, *failCommand, *missingNPC); err != nil {
				log.Printf("handle message: %v", err)
			}
		}
	}()

	stop := make(chan os.Signal, 1)
	signal.Notify(stop, os.Interrupt, syscall.SIGTERM)

	select {
	case <-stop:
		log.Println("mockUnity stopping")
	case <-done:
	}
}

func handleMessage(conn *websocket.Conn, payload []byte, delay time.Duration, failCommand, missingNPC string) error {
	var env struct {
		Type string `json:"type"`
	}
	if err := json.Unmarshal(payload, &env); err != nil {
		return err
	}

	switch env.Type {
	case unity.MessageTypeCommand:
		var command unity.Command
		if err := json.Unmarshal(payload, &command); err != nil {
			return err
		}
		log.Printf("command: id=%s tool=%s npc=%s args=%v", command.CommandID, command.ToolName, command.NPCID, command.Arguments)
		time.Sleep(delay)
		return conn.WriteJSON(simulate(command, failCommand, missingNPC))
	case unity.MessageTypePong:
		return nil
	default:
		return fmt.Errorf("unknown message type %q", env.Type)
	}
}

func simulate(command unity.Command, failCommand, missingNPC string) unity.Result {
	result := unity.Result{
		Type:      unity.MessageTypeResult,
		CommandID: command.CommandID,
		ToolName:  command.ToolName,
		OK:        true,
		Data:      map[string]any{},
	}

	if missingNPC != "" && command.NPCID == missingNPC {
		result.OK = false
		result.ErrorCode = "npc_not_found"
		result.Message = fmt.Sprintf("NPC %s 不存在", command.NPCID)
		return result
	}

	if failCommand != "" && command.ToolName == failCommand {
		result.OK = false
		result.ErrorCode = "execution_failed"
		result.Message = fmt.Sprintf("模拟执行失败: %s", command.ToolName)
		return result
	}

	switch command.ToolName {
	case "get_npc_status":
		result.Message = fmt.Sprintf("[Unity 反馈]: NPC %s 状态: 正常, 生命值: 100, 能量: 80", command.NPCID)
		result.Data = map[string]any{"state": "normal", "hp": 100, "energy": 80}
	case "get_npc_position":
		result.Message = fmt.Sprintf("[Unity 反馈]: NPC %s 位置: (100.5, 0.0, 200.3)", command.NPCID)
		result.Data = map[string]any{"x": 100.5, "y": 0.0, "z": 200.3}
	case "move_to":
		target, _ := command.Arguments["target"].(string)
		result.Message = fmt.Sprintf("[Unity 反馈]: NPC %s 已开始移动到 %s", command.NPCID, target)
	case "say":
		content, _ := command.Arguments["content"].(string)
		result.Message = fmt.Sprintf("[Unity 反馈]: NPC %s 说: %s", command.NPCID, content)
	default:
		result.OK = false
		result.ErrorCode = "unknown_command"
		result.Message = fmt.Sprintf("未知 Unity 命令: %s", command.ToolName)
	}

	return result
}
