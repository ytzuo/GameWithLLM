package handler

import (
	"context"
	"io"

	"github.com/cloudwego/hertz/pkg/app"
	"github.com/cloudwego/hertz/pkg/protocol/consts"
	mcpserver "github.com/mark3labs/mcp-go/server"
)

// handleHealth 健康检查
func handleHealth(ctx context.Context, c *app.RequestContext) {
	c.JSON(consts.StatusOK, map[string]interface{}{
		"status":  "ok",
		"service": "GameMCPServer",
	})
}

// handleRoot 根路径
func handleRoot(ctx context.Context, c *app.RequestContext) {
	c.String(consts.StatusOK, "Game MCP Server is running!")
}

// handleSSE 处理 SSE 端点请求。
func handleSSE(ctx context.Context, c *app.RequestContext, sseServer *mcpserver.SSEServer) {
	req, err := convertToHTTPRequest(c)
	if err != nil {
		c.AbortWithStatusJSON(consts.StatusInternalServerError, map[string]string{
			"error": err.Error(),
		})
		return
	}

	pr, pw := io.Pipe()

	c.Header("Content-Type", "text/event-stream")
	c.Header("Cache-Control", "no-cache")
	c.Header("Connection", "keep-alive")
	c.Header("Access-Control-Allow-Origin", "*")
	c.SetBodyStream(pr, -1)

	go func() {
		defer pw.Close()
		rw := newStreamResponseWriter(pw)
		sseServer.SSEHandler().ServeHTTP(rw, req)
	}()
}

// handleMessage 处理 MCP 消息端点请求。
func handleMessage(ctx context.Context, c *app.RequestContext, sseServer *mcpserver.SSEServer) {
	req, err := convertToHTTPRequest(c)
	if err != nil {
		c.AbortWithStatusJSON(consts.StatusInternalServerError, map[string]string{
			"error": err.Error(),
		})
		return
	}

	rw := newResponseRecorder()
	sseServer.MessageHandler().ServeHTTP(rw, req)

	c.SetStatusCode(rw.statusCode)
	for key, values := range rw.header {
		for _, value := range values {
			c.Response.Header.Add(key, value)
		}
	}
	c.Write(rw.body.Bytes())
}
