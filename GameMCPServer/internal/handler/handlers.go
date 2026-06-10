package handler

import (
	"context"
	"net/http"
	"net/url"

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

// handleMCP 处理 MCP Streamable HTTP 单端点请求。
func handleMCP(ctx context.Context, c *app.RequestContext, mcpHTTPServer *mcpserver.StreamableHTTPServer) {
	reqURL, err := url.ParseRequestURI(string(c.Request.RequestURI()))
	if err != nil {
		c.AbortWithStatusJSON(consts.StatusInternalServerError, map[string]string{
			"error": err.Error(),
		})
		return
	}

	req := &mcpserver.HTTPRequest{
		Method:  string(c.Request.Method()),
		URL:     reqURL,
		Header:  copyRequestHeaders(c),
		Body:    c.Request.Body(),
		Context: ctx,
	}

	if req.Method == http.MethodGet || req.Method == http.MethodPost {
		mcpHTTPServer.Handle(newHertzStreamResponseWriter(c), req)
		return
	}

	rw := newResponseRecorder()
	mcpHTTPServer.Handle(rw, req)

	c.SetStatusCode(rw.statusCode)
	for key, values := range rw.header {
		for _, value := range values {
			c.Response.Header.Add(key, value)
		}
	}
	c.Write(rw.body.Bytes())
}
