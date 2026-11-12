// Obelisk's local speech service. The UI owns its lifetime through stdin.
package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"time"
	"unicode"
)

type engine struct {
	root, token string
	client      *http.Client
	mu          sync.Mutex
	child       *exec.Cmd
	childURL    string
	childToken  string
	job         io.Closer
	ready       bool
	lastUsed    time.Time
	active      chan struct{}
	voices      map[string]string
	last        map[string]any
}

func language(text, voice string) string {
	if voice == "jarvis-ru" {
		return "ru"
	}
	if voice == "jarvis-en" {
		return "en"
	}
	ru, en := 0, 0
	for _, r := range text {
		if unicode.In(r, unicode.Cyrillic) {
			ru++
		} else if unicode.In(r, unicode.Latin) {
			en++
		}
	}
	if ru > en {
		return "ru"
	}
	return "en"
}
func jsonOut(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(value)
}
func fail(w http.ResponseWriter, status int, message string) {
	jsonOut(w, status, map[string]string{"error": message})
}
func loadToken(path string) (string, error) {
	if b, e := os.ReadFile(path); e == nil && len(bytes.TrimSpace(b)) >= 32 {
		return strings.TrimSpace(string(b)), nil
	}
	b := make([]byte, 32)
	if _, e := rand.Read(b); e != nil {
		return "", e
	}
	v := hex.EncodeToString(b)
	f, e := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if e != nil {
		return "", e
	}
	defer f.Close()
	_, e = f.WriteString(v)
	return v, e
}
func (e *engine) stopLocked() {
	e.ready = false
	if e.child != nil {
		e.child.Process.Kill()
		e.child.Wait()
		e.child = nil
	}
	if e.job != nil {
		e.job.Close()
		e.job = nil
	}
}
func (e *engine) warm(ctx context.Context) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	e.lastUsed = time.Now()
	if e.ready {
		return nil
	}
	e.stopLocked()
	socket, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return err
	}
	port := socket.Addr().(*net.TCPAddr).Port
	socket.Close()
	e.childURL = fmt.Sprintf("http://127.0.0.1:%d", port)
	childToken, err := loadToken(filepath.Join(e.root, "data", "worker.token"))
	if err != nil {
		return err
	}
	e.childToken = childToken
	cmd := exec.Command(filepath.Join(e.root, "runtime", "llama-server.exe"),
		"-m", filepath.Join(e.root, "models", "Qwen3-TTS-12Hz-1.7B-Base-Q8_0.gguf"),
		"--mmproj", filepath.Join(e.root, "models", "mmproj-Qwen3-TTS-12Hz-1.7B-Base-bf16.gguf"),
		"-ngl", "99", "--host", "127.0.0.1", "--port", fmt.Sprint(port), "-c", "2048", "-np", "1", "-t", "6",
		"--no-webui", "--no-warmup", "--api-key-file", filepath.Join(e.root, "data", "worker.token"), "-lv", "1")
	hide(cmd)
	cmd.Dir = e.root
	// Native logs never contain the request body; truncate on each cold start.
	f, err := os.OpenFile(filepath.Join(e.root, "data", "worker.log"), os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0600)
	if err != nil {
		return err
	}
	defer f.Close()
	cmd.Stdout = f
	cmd.Stderr = f
	if err = cmd.Start(); err != nil {
		return err
	}
	e.child = cmd
	e.job, err = ownProcess(cmd.Process.Pid)
	if err != nil {
		e.stopLocked()
		return fmt.Errorf("cannot own audio worker: %w", err)
	}
	deadline := time.NewTimer(45 * time.Second)
	defer deadline.Stop()
	tick := time.NewTicker(100 * time.Millisecond)
	defer tick.Stop()
	for {
		select {
		case <-ctx.Done():
			e.stopLocked()
			return ctx.Err()
		case <-deadline.C:
			e.stopLocked()
			return errors.New("local speech model did not become ready")
		case <-tick.C:
			pingCtx, cancel := context.WithTimeout(ctx, time.Second)
			req, _ := http.NewRequestWithContext(pingCtx, "GET", e.childURL+"/health", nil)
			req.Header.Set("Authorization", "Bearer "+childToken)
			r, er := e.client.Do(req)
			if er == nil {
				r.Body.Close()
				if r.StatusCode == 200 {
					e.ready = true
					cancel()
					return nil
				}
			}
			cancel()
		}
	}
}
func (e *engine) handle(w http.ResponseWriter, r *http.Request) {
	// Native clients only. A web origin is never allowed to drive local inference.
	if r.Header.Get("Origin") != "" || subtle.ConstantTimeCompare([]byte(r.Header.Get("Authorization")), []byte("Bearer "+e.token)) != 1 {
		fail(w, 401, "Unauthorized")
		return
	}
	switch r.Method + " " + r.URL.Path {
	case "GET /health":
		e.mu.Lock()
		defer e.mu.Unlock()
		jsonOut(w, 200, map[string]any{"service": "obelisk-audio", "version": "0.9.0", "model": "qwen3-tts-1.7b-q8", "device": "local-cuda", "ready": e.ready, "busy": len(e.active) > 0, "voices": []string{"jarvis-en", "jarvis-ru"}, "last": e.last})
	case "POST /warmup":
		// Consume the small command body before slow work. HTTP/1 clients otherwise
		// risk a reset while the server closes a connection with unread request data.
		if _, err := io.Copy(io.Discard, http.MaxBytesReader(w, r.Body, 1024)); err != nil {
			fail(w, 400, "Invalid warmup request")
			return
		}
		if err := e.warm(r.Context()); err != nil {
			log.Printf("warmup ended: %v", err)
			fail(w, 503, err.Error())
			return
		}
		jsonOut(w, 200, map[string]bool{"ready": true})
	case "POST /release":
		select {
		case e.active <- struct{}{}:
			defer func() { <-e.active }()
		default:
			fail(w, 409, "Speech is active")
			return
		}
		e.mu.Lock()
		e.stopLocked()
		e.mu.Unlock()
		jsonOut(w, 200, map[string]bool{"ready": false})
	case "POST /v1/audio/speech":
		e.speech(w, r)
	default:
		fail(w, 404, "Unknown audio operation")
	}
}
func (e *engine) speech(w http.ResponseWriter, r *http.Request) {
	var input struct {
		Input          string `json:"input"`
		Voice          string `json:"voice"`
		ResponseFormat string `json:"response_format"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 32768)).Decode(&input); err != nil {
		fail(w, 400, "Invalid speech request")
		return
	}
	if len([]rune(strings.TrimSpace(input.Input))) == 0 || len([]rune(input.Input)) > 400 {
		fail(w, 400, "Speech phrase must contain 1–400 characters")
		return
	}
	if input.Voice != "" && input.Voice != "jarvis-auto" && input.Voice != "jarvis-en" && input.Voice != "jarvis-ru" {
		fail(w, 400, "Unknown Jarvis voice")
		return
	}
	if input.ResponseFormat != "" && input.ResponseFormat != "pcm" && input.ResponseFormat != "wav" {
		fail(w, 400, "Use pcm or wav")
		return
	}
	select {
	case e.active <- struct{}{}:
		defer func() { <-e.active }()
	default:
		fail(w, 429, "Speech engine is busy")
		return
	}
	start := time.Now()
	ctx, cancel := context.WithTimeout(r.Context(), 65*time.Second)
	defer cancel()
	if err := e.warm(ctx); err != nil {
		fail(w, 503, err.Error())
		return
	}
	lang := language(input.Input, input.Voice)
	format := input.ResponseFormat
	if format == "" {
		format = "pcm"
	}
	payload, _ := json.Marshal(map[string]any{"input": input.Input, "lang": lang, "speaker_ref_b64": e.voices[lang], "stream": true, "response_format": format, "n_predict": 450, "seed": 42})
	e.mu.Lock()
	url, key := e.childURL, e.childToken
	e.mu.Unlock()
	req, _ := http.NewRequestWithContext(ctx, "POST", url+"/tts", bytes.NewReader(payload))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", "Bearer "+key)
	res, err := e.client.Do(req)
	if err != nil {
		e.mu.Lock()
		e.stopLocked()
		e.mu.Unlock()
		fail(w, 503, "Local voice generation stopped")
		return
	}
	defer res.Body.Close()
	if res.StatusCode != 200 {
		fail(w, 502, "Local voice engine rejected the phrase")
		return
	}
	w.Header().Set("Content-Type", res.Header.Get("Content-Type"))
	w.Header().Set("X-Sample-Rate", "24000")
	w.Header().Set("X-Voice", "jarvis-"+lang)
	w.Header().Set("Cache-Control", "no-store")
	var first int64
	var count int64
	buffer := make([]byte, 8192)
	flusher, _ := w.(http.Flusher)
	for {
		n, readErr := res.Body.Read(buffer)
		if n > 0 {
			if count == 0 {
				first = time.Since(start).Milliseconds()
			}
			count += int64(n)
			if _, err = w.Write(buffer[:n]); err != nil {
				cancel()
				break
			}
			if flusher != nil {
				flusher.Flush()
			}
		}
		if readErr != nil {
			err = readErr
			break
		}
	}
	cancelled := ctx.Err() != nil || err != io.EOF
	e.mu.Lock()
	e.lastUsed = time.Now()
	e.last = map[string]any{"language": lang, "firstPcmMs": first, "totalMs": time.Since(start).Milliseconds(), "bytes": count, "cancelled": cancelled, "utc": time.Now().UTC()}
	// Cancellation is definitive: discard the native slot so no abandoned inference can hold a new turn.
	if cancelled {
		e.stopLocked()
	}
	e.mu.Unlock()
}
func main() {
	rootFlag := flag.String("root", "", "engine directory")
	port := flag.Int("port", 3920, "loopback port")
	owned := flag.Bool("owned", false, "exit when the owning app closes stdin")
	flag.Parse()
	exe, err := os.Executable()
	if err != nil {
		log.Fatal(err)
	}
	root := *rootFlag
	if root == "" {
		root = filepath.Dir(exe)
	}
	root, err = filepath.Abs(root)
	if err != nil {
		log.Fatal(err)
	}
	if err = os.MkdirAll(filepath.Join(root, "data"), 0700); err != nil {
		log.Fatal(err)
	}
	token, err := loadToken(filepath.Join(root, "data", "engine.token"))
	if err != nil {
		log.Fatal(err)
	}
	e := &engine{root: root, token: token, active: make(chan struct{}, 1), voices: map[string]string{}, client: &http.Client{Transport: &http.Transport{Proxy: nil, MaxIdleConnsPerHost: 2, ResponseHeaderTimeout: 50 * time.Second}}}
	for _, lang := range []string{"en", "ru"} {
		b, err := os.ReadFile(filepath.Join(root, "voices", "jarvis-"+lang+".wav"))
		if err != nil {
			log.Fatal(err)
		}
		e.voices[lang] = base64.StdEncoding.EncodeToString(b)
	}
	listener, err := net.Listen("tcp4", fmt.Sprintf("127.0.0.1:%d", *port))
	if err != nil {
		log.Fatal("Audio endpoint already in use")
	}
	server := &http.Server{Handler: http.HandlerFunc(e.handle), ReadHeaderTimeout: 5 * time.Second, IdleTimeout: 30 * time.Second, MaxHeaderBytes: 8192}
	if *owned {
		go func() {
			_, err := io.Copy(io.Discard, os.Stdin)
			log.Printf("owner input closed: %v", err)
			e.mu.Lock()
			e.stopLocked()
			e.mu.Unlock()
			server.Close()
		}()
	}
	go func() {
		for range time.Tick(30 * time.Second) {
			e.mu.Lock()
			if len(e.active) == 0 && e.ready && time.Since(e.lastUsed) > 10*time.Minute {
				e.stopLocked()
			}
			e.mu.Unlock()
		}
	}()
	fmt.Println("Obelisk audio ready on loopback")
	if err = server.Serve(listener); err != nil && err != http.ErrServerClosed {
		log.Print(err)
	}
	e.mu.Lock()
	e.stopLocked()
	e.mu.Unlock()
}
