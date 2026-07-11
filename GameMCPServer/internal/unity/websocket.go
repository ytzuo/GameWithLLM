package unity

import (
	"bufio"
	"crypto/sha1"
	"encoding/base64"
	"encoding/binary"
	"fmt"
	"io"
	"net"
	"net/http"
	"strings"
)

// isWebSocketUpgrade 判断 HTTP 请求是否为 WebSocket 升级请求。
func isWebSocketUpgrade(r *http.Request) bool {
	return strings.EqualFold(r.Header.Get("Upgrade"), "websocket") &&
		strings.Contains(strings.ToLower(r.Header.Get("Connection")), "upgrade")
}

// upgradeWebSocket 手动完成 WebSocket 握手并返回底层连接。
func upgradeWebSocket(w http.ResponseWriter, r *http.Request) (net.Conn, error) {
	if !isWebSocketUpgrade(r) {
		http.Error(w, "websocket upgrade required", http.StatusUpgradeRequired)
		return nil, fmt.Errorf("websocket upgrade required")
	}

	key := r.Header.Get("Sec-WebSocket-Key")
	if key == "" {
		http.Error(w, "missing Sec-WebSocket-Key", http.StatusBadRequest)
		return nil, fmt.Errorf("missing Sec-WebSocket-Key")
	}

	hijacker, ok := w.(http.Hijacker)
	if !ok {
		http.Error(w, "hijacking not supported", http.StatusInternalServerError)
		return nil, fmt.Errorf("hijacking not supported")
	}

	conn, rw, err := hijacker.Hijack()
	if err != nil {
		return nil, err
	}

	accept := websocketAccept(key)
	if _, err := fmt.Fprintf(rw, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: %s\r\n\r\n", accept); err != nil {
		_ = conn.Close()
		return nil, err
	}
	if err := rw.Flush(); err != nil {
		_ = conn.Close()
		return nil, err
	}

	if rw.Reader.Buffered() > 0 {
		// 保留 net/http 在 Hijack 过程中已经读到的剩余字节。
		conn = &bufferedConn{Conn: conn, reader: rw.Reader}
	}
	return conn, nil
}

// websocketAccept 计算 WebSocket 握手需要的 Sec-WebSocket-Accept。
func websocketAccept(key string) string {
	h := sha1.New()
	_, _ = h.Write([]byte(key))
	_, _ = h.Write([]byte("258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))
	return base64.StdEncoding.EncodeToString(h.Sum(nil))
}

type bufferedConn struct {
	net.Conn
	reader *bufio.Reader
}

// Read 优先读取 Hijack 时缓冲区里遗留的数据，再读底层连接。
func (c *bufferedConn) Read(p []byte) (int, error) {
	if c.reader != nil && c.reader.Buffered() > 0 {
		return c.reader.Read(p)
	}
	return c.Conn.Read(p)
}

// readTextFrame 读取并解码一个 WebSocket 文本帧。
func readTextFrame(r io.Reader) ([]byte, error) {
	header := make([]byte, 2)
	if _, err := io.ReadFull(r, header); err != nil {
		return nil, err
	}

	opcode := header[0] & 0x0f
	if opcode == 0x8 {
		return nil, io.EOF
	}
	if opcode != 0x1 {
		return nil, fmt.Errorf("unsupported websocket opcode: %d", opcode)
	}

	masked := (header[1] & 0x80) != 0
	length := uint64(header[1] & 0x7f)
	switch length {
	case 126:
		ext := make([]byte, 2)
		if _, err := io.ReadFull(r, ext); err != nil {
			return nil, err
		}
		length = uint64(binary.BigEndian.Uint16(ext))
	case 127:
		ext := make([]byte, 8)
		if _, err := io.ReadFull(r, ext); err != nil {
			return nil, err
		}
		length = binary.BigEndian.Uint64(ext)
	}

	var mask []byte
	if masked {
		mask = make([]byte, 4)
		if _, err := io.ReadFull(r, mask); err != nil {
			return nil, err
		}
	}

	if length > 1<<20 {
		return nil, fmt.Errorf("websocket frame too large: %d", length)
	}
	payload := make([]byte, length)
	if _, err := io.ReadFull(r, payload); err != nil {
		return nil, err
	}

	if masked {
		for i := range payload {
			payload[i] ^= mask[i%4]
		}
	}
	return payload, nil
}

// writeTextFrame 将 payload 写成一个 WebSocket 文本帧。
func writeTextFrame(w io.Writer, payload []byte) error {
	// 按 RFC 6455，服务端发给客户端的帧不需要 mask。
	header := []byte{0x81}
	switch {
	case len(payload) < 126:
		header = append(header, byte(len(payload)))
	case len(payload) <= 0xffff:
		header = append(header, 126, byte(len(payload)>>8), byte(len(payload)))
	default:
		header = append(header, 127, 0, 0, 0, 0, byte(len(payload)>>24), byte(len(payload)>>16), byte(len(payload)>>8), byte(len(payload)))
	}

	if _, err := w.Write(header); err != nil {
		return err
	}
	_, err := w.Write(payload)
	return err
}
