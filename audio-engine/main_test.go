package main

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestVoiceRouting(t *testing.T) {
	for _, c := range []struct{ text, voice, want string }{{"All systems ready", "jarvis-auto", "en"}, {"Все системы готовы", "jarvis-auto", "ru"}, {"GPU и CPU работают нормально", "jarvis-auto", "ru"}, {"Hello", "jarvis-ru", "ru"}, {"Привет", "jarvis-en", "en"}} {
		if got := language(c.text, c.voice); got != c.want {
			t.Fatalf("%q: %s", c.text, got)
		}
	}
}
func TestUnauthorizedAndOrigins(t *testing.T) {
	e := engine{token: "secret"}
	for _, origin := range []string{"", "https://example.com"} {
		r := httptest.NewRequest("POST", "/warmup", nil)
		if origin != "" {
			r.Header.Set("Authorization", "Bearer secret")
			r.Header.Set("Origin", origin)
		}
		w := httptest.NewRecorder()
		e.handle(w, r)
		if w.Code != 401 {
			t.Fatal(w.Code)
		}
	}
}
func TestRejectsUnboundedAndUnknownVoice(t *testing.T) {
	e := engine{token: "secret"}
	for _, body := range []string{`{"input":"x","voice":"arbitrary"}`, `{"input":""}`, `{"input":"` + strings.Repeat("a", 401) + `"}`, `{"input":"hello","response_format":"mp3"}`} {
		r := httptest.NewRequest(http.MethodPost, "/v1/audio/speech", strings.NewReader(body))
		r.Header.Set("Authorization", "Bearer secret")
		w := httptest.NewRecorder()
		e.handle(w, r)
		if w.Code != 400 {
			t.Fatal(w.Code)
		}
	}
}
