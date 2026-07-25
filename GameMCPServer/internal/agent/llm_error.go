package agent

import (
	"errors"
	"fmt"
	"time"
)

// LLMRequestError 保留模型供应商错误的状态码、可重试性和建议等待时间。
type LLMRequestError struct {
	StatusCode int
	Message    string
	Temporary  bool
	RetryAfter time.Duration
	Cause      error
}

// Error 返回适合日志和 JSON-RPC error message 的摘要，不包含请求密钥。
func (e *LLMRequestError) Error() string {
	if e.StatusCode > 0 {
		return fmt.Sprintf("LLM returned HTTP %d: %s", e.StatusCode, e.Message)
	}
	if e.Cause != nil {
		return fmt.Sprintf("%s: %v", e.Message, e.Cause)
	}
	return e.Message
}

// Unwrap 暴露底层网络错误，支持 errors.Is 和 errors.As。
func (e *LLMRequestError) Unwrap() error {
	return e.Cause
}

// IsTemporaryLLMError 判断错误是否允许在尚未输出可见文本时自动重试。
func IsTemporaryLLMError(err error) bool {
	var requestError *LLMRequestError
	return errors.As(err, &requestError) && requestError.Temporary
}

// IsLLMRequestError 判断错误是否来自模型 HTTP 或网络请求层。
func IsLLMRequestError(err error) bool {
	var requestError *LLMRequestError
	return errors.As(err, &requestError)
}
