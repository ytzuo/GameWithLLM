package handler

import (
	"encoding/json"
	"net/http"
)

// handleHealth 返回服务健康状态。
func handleHealth(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{
		"status":  "ok",
		"service": "GameMCPServer",
	})
}
