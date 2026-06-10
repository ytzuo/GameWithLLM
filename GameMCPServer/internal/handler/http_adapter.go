package handler

import (
	"net/http"
	"sync"

	"github.com/cloudwego/hertz/pkg/app"
	"github.com/cloudwego/hertz/pkg/protocol/http1/resp"
)

// responseRecorder 实现 http.ResponseWriter，用于捕获 mcp-go 的非流式响应（如 DELETE 请求）。
// 由于 DELETE 等短请求不需要 SSE 流式输出，使用此结构暂存完整响应后再回写给 Hertz。
type responseRecorder struct {
	statusCode int
	header     http.Header
	body       *bytesBuffer
}

// newResponseRecorder 创建一个新的响应记录器，用于接收 mcp-go 的非流式响应。
func newResponseRecorder() *responseRecorder {
	return &responseRecorder{
		statusCode: http.StatusOK,
		header:     make(http.Header),
		body:       &bytesBuffer{},
	}
}

func (rr *responseRecorder) Header() http.Header {
	return rr.header
}

func (rr *responseRecorder) Write(p []byte) (int, error) {
	return rr.body.Write(p)
}

func (rr *responseRecorder) WriteHeader(code int) {
	rr.statusCode = code
}

func (rr *responseRecorder) Flush() {}

func (rr *responseRecorder) CanStream() bool {
	return false
}

// hertzStreamResponseWriter 将 mcp-go 的 Streamable HTTP 输出直接写入 Hertz chunked response。
// 用于 SSE 流式场景：mcp-go 会多次调用 Write 输出事件流，这里通过 HijackWriter 实现 chunked transfer encoding。
type hertzStreamResponseWriter struct {
	c           *app.RequestContext
	header      http.Header
	statusCode  int
	wroteHeader bool
	mu          sync.Mutex
}

// newHertzStreamResponseWriter 创建流式响应写入器。
// 如果尚未劫持响应写入器，会设置 chunked body writer 以支持 SSE 流式传输。
func newHertzStreamResponseWriter(c *app.RequestContext) *hertzStreamResponseWriter {
	if c.Response.GetHijackWriter() == nil {
		c.Response.HijackWriter(resp.NewChunkedBodyWriter(&c.Response, c.GetWriter()))
	}
	return &hertzStreamResponseWriter{
		c:          c,
		header:     make(http.Header),
		statusCode: http.StatusOK,
	}
}

func (w *hertzStreamResponseWriter) Header() http.Header {
	return w.header
}

func (w *hertzStreamResponseWriter) WriteHeader(code int) {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.writeHeaderLocked(code)
}

func (w *hertzStreamResponseWriter) Write(p []byte) (int, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.writeHeaderLocked(http.StatusOK)
	return w.c.Write(p)
}

func (w *hertzStreamResponseWriter) Flush() {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.writeHeaderLocked(http.StatusOK)
	_ = w.c.Flush()
}

func (w *hertzStreamResponseWriter) CanStream() bool {
	return true
}

// writeHeaderLocked 在首次写入时发送状态码和响应头到 Hertz。
// 对 HijackWriter 写入空数据是为了触发 chunked transfer 的初始 flush，确保客户端立即收到响应头。
func (w *hertzStreamResponseWriter) writeHeaderLocked(code int) {
	if w.wroteHeader {
		return
	}
	w.wroteHeader = true
	w.statusCode = code
	w.c.SetStatusCode(code)
	for key, values := range w.header {
		for _, value := range values {
			w.c.Response.Header.Add(key, value)
		}
	}
	if hw := w.c.Response.GetHijackWriter(); hw != nil {
		_, _ = hw.Write(nil)
	}
}

// copyRequestHeaders 将 Hertz 请求头复制为标准 http.Header，供 mcp-go 使用。
func copyRequestHeaders(c *app.RequestContext) http.Header {
	header := make(http.Header)
	c.Request.Header.VisitAll(func(key, value []byte) {
		header.Add(string(key), string(value))
	})
	return header
}

// bytesBuffer 是一个简单的字节缓冲区，用于 responseRecorder 暂存响应体。
type bytesBuffer struct {
	data []byte
}

func (b *bytesBuffer) Write(p []byte) (int, error) {
	b.data = append(b.data, p...)
	return len(p), nil
}

func (b *bytesBuffer) Bytes() []byte {
	return b.data
}
