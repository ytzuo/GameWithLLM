package unity

import (
	"context"
	"io"
	"testing"
	"time"
)

type fakeRead struct {
	msg jsonRPCMessage
	err error
}

type fakeJSONRPCConnection struct {
	reads  chan fakeRead
	writes chan jsonRPCMessage
}

func newFakeJSONRPCConnection() *fakeJSONRPCConnection {
	return &fakeJSONRPCConnection{
		reads:  make(chan fakeRead, 16),
		writes: make(chan jsonRPCMessage, 16),
	}
}

func (c *fakeJSONRPCConnection) Read(ctx context.Context, msg *jsonRPCMessage) error {
	select {
	case result := <-c.reads:
		if result.err != nil {
			return result.err
		}
		*msg = result.msg
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (c *fakeJSONRPCConnection) Write(ctx context.Context, msg jsonRPCMessage) error {
	select {
	case c.writes <- msg:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

func newTestSession(timeout time.Duration) (*jsonRPCSession, *fakeJSONRPCConnection) {
	ctx, cancel := context.WithCancel(context.Background())
	conn := newFakeJSONRPCConnection()
	registry := NewUnityRegistry()
	return newJSONRPCSession(ctx, cancel, conn, registry), conn
}

func mustReceiveMessage(t *testing.T, messages <-chan jsonRPCMessage) jsonRPCMessage {
	t.Helper()
	select {
	case msg := <-messages:
		return msg
	case <-time.After(time.Second):
		t.Fatal("timed out waiting for JSON-RPC message")
		return jsonRPCMessage{}
	}
}

func waitForDone(t *testing.T, done <-chan struct{}) {
	t.Helper()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("timed out waiting for goroutine")
	}
}

func stopReadLoop(conn *fakeJSONRPCConnection) {
	conn.reads <- fakeRead{err: io.EOF}
}
