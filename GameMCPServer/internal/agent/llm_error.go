package agent

import (
	"errors"
	"fmt"
	"time"
)

type LLMRequestError struct {
	StatusCode int
	Message    string
	Temporary  bool
	RetryAfter time.Duration
	Cause      error
}

func (e *LLMRequestError) Error() string {
	if e.StatusCode > 0 {
		return fmt.Sprintf("LLM returned HTTP %d: %s", e.StatusCode, e.Message)
	}
	if e.Cause != nil {
		return fmt.Sprintf("%s: %v", e.Message, e.Cause)
	}
	return e.Message
}

func (e *LLMRequestError) Unwrap() error {
	return e.Cause
}

func IsTemporaryLLMError(err error) bool {
	var requestError *LLMRequestError
	return errors.As(err, &requestError) && requestError.Temporary
}

func IsLLMRequestError(err error) bool {
	var requestError *LLMRequestError
	return errors.As(err, &requestError)
}
