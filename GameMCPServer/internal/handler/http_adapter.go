package handler

import (
	"fmt"
	"io"
	"net/http"

	"github.com/cloudwego/hertz/pkg/app"
)

// convertToHTTPRequest 将 Hertz RequestContext 转换为标准 http.Request。
func convertToHTTPRequest(c *app.RequestContext) (*http.Request, error) {
	method := string(c.Request.Method())
	uri := string(c.Request.RequestURI())

	var body []byte
	if c.Request.Body() != nil {
		body = c.Request.Body()
	}

	req, err := http.NewRequest(method, uri, nil)
	if err != nil {
		return nil, err
	}

	c.Request.Header.VisitAll(func(key, value []byte) {
		req.Header.Add(string(key), string(value))
	})

	if len(body) > 0 {
		req.Body = &bodyReader{body: body}
		req.ContentLength = int64(len(body))
	}

	return req, nil
}

// bodyReader 实现 io.ReadCloser，用于向标准 http.Request 提供 Hertz 请求体。
type bodyReader struct {
	body   []byte
	offset int
}

func (r *bodyReader) Read(p []byte) (int, error) {
	if r.offset >= len(r.body) {
		return 0, fmt.Errorf("EOF")
	}
	n := copy(p, r.body[r.offset:])
	r.offset += n
	return n, nil
}

func (r *bodyReader) Close() error {
	return nil
}

// responseRecorder 实现 http.ResponseWriter，用于转接非流式响应。
type responseRecorder struct {
	statusCode int
	header     http.Header
	body       *bytesBuffer
}

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

// streamResponseWriter 将 mcp-go SSE handler 的输出转发到 io.PipeWriter。
type streamResponseWriter struct {
	header     http.Header
	pw         *io.PipeWriter
	statusCode int
}

func newStreamResponseWriter(pw *io.PipeWriter) *streamResponseWriter {
	return &streamResponseWriter{
		header:     make(http.Header),
		pw:         pw,
		statusCode: http.StatusOK,
	}
}

func (w *streamResponseWriter) Header() http.Header {
	return w.header
}

func (w *streamResponseWriter) WriteHeader(code int) {
	w.statusCode = code
}

func (w *streamResponseWriter) Write(p []byte) (int, error) {
	return w.pw.Write(p)
}

func (w *streamResponseWriter) Flush() {}

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
