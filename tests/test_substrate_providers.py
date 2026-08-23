"""
Vendor adapter tests — the real SDKs, against a local wire stub (§10.2).

Everything else in the suite talks to a scripted provider that implements
LLMProvider directly. That proves the agents, but it never executes
AnthropicProvider.complete() or OpenAICompatibleProvider.complete() — the
code that actually has to be right when a key arrives.

So these run the genuine vendor SDK against an HTTP server on localhost
that speaks the vendor's wire protocol. No key, no network, no cost, and
the adapter cannot tell the difference.

This is not paranoia. Writing it caught a live bug: the adapter passed
`temperature=` to the Anthropic Messages API, which the 1.x SDK removed,
so the first real call would have died with a TypeError. That is exactly
the class of failure a "substrate agnostic" layer exists to absorb, and
exactly the class it cannot absorb if nothing ever runs the adapter.

Skipped when the SDK isn't installed. Install requirements-dev.txt to run
them — worth doing in CI, since an SDK upgrade that changes shape should
fail here rather than in production.
"""
from __future__ import annotations

import json
import socketserver
import threading
from http.server import BaseHTTPRequestHandler
from typing import Any, Dict, Optional

import pytest

from substrates.base import CompletionError
from substrates.registry import resolve_substrate


# ---------------------------------------------------------------------------
# A local server that speaks a vendor's wire protocol
# ---------------------------------------------------------------------------

class _Stub:
    """Captures the request the SDK actually put on the wire, and replies
    with whatever the test asked for."""

    def __init__(self, responder, status: int = 200):
        self.responder = responder
        self.status = status
        self.captured: Dict[str, Any] = {}
        stub = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                length = int(self.headers.get("content-length", 0))
                stub.captured["path"] = self.path
                # Lowercased: HTTP header names are case-insensitive and
                # SDKs disagree about capitalisation.
                stub.captured["headers"] = {k.lower(): v
                                            for k, v in self.headers.items()}
                stub.captured["body"] = json.loads(self.rfile.read(length) or b"{}")

                payload = json.dumps(stub.responder(stub.captured["body"])).encode()
                self.send_response(stub.status)
                self.send_header("content-type", "application/json")
                self.send_header("content-length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, *args):
                pass

        self._server = socketserver.TCPServer(("127.0.0.1", 0), Handler)
        self.port = self._server.server_address[1]
        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True)

    def __enter__(self):
        self._thread.start()
        return self

    def __exit__(self, *exc):
        self._server.shutdown()
        self._server.server_close()

    @property
    def url(self) -> str:
        return f"http://127.0.0.1:{self.port}"

    @property
    def body(self) -> Dict[str, Any]:
        return self.captured.get("body", {})


def _anthropic_reply(tail: str = '"recommendation": "Keep it brief.", "proceed": true}',
                     usage: Optional[Dict[str, int]] = None):
    def responder(request):
        return {
            "id": "msg_stub", "type": "message", "role": "assistant",
            "model": request.get("model", "?"),
            "content": [{"type": "text", "text": tail}],
            "stop_reason": "end_turn",
            "usage": usage or {"input_tokens": 210, "output_tokens": 28},
        }
    return responder


def _openai_reply(text: str = '{"recommendation": "Keep it brief.", "proceed": true}'):
    def responder(request):
        return {
            "id": "chatcmpl-stub", "object": "chat.completion",
            "created": 0, "model": request.get("model", "?"),
            "choices": [{"index": 0, "finish_reason": "stop",
                         "message": {"role": "assistant", "content": text}}],
            "usage": {"prompt_tokens": 195, "completion_tokens": 22,
                      "total_tokens": 217},
        }
    return responder


def _substrate(provider: str, base_url: str, key_env: Optional[str],
               model: str = "some-model-id"):
    return resolve_substrate({"substrates": {"probe": {
        "provider": provider, "model": model, "api_key_env": key_env,
        "base_url": base_url, "max_tokens": 512,
    }}}, "probe")


# ---------------------------------------------------------------------------
# Anthropic
# ---------------------------------------------------------------------------

class TestAnthropicAdapter:
    @pytest.fixture(autouse=True)
    def _sdk(self, monkeypatch):
        pytest.importorskip("anthropic", reason="pip install -r requirements-dev.txt")
        monkeypatch.setenv("ECI_TEST_KEY", "sk-ant-stub")

    def test_it_puts_a_well_formed_request_on_the_wire(self):
        with _Stub(_anthropic_reply()) as stub:
            substrate = _substrate("anthropic", stub.url, "ECI_TEST_KEY")
            substrate.complete(system="You are ANALYTICS.", user="TASK: Evaluate",
                               temperature=0.2, prefill="{")

        assert stub.captured["headers"]["x-api-key"] == "sk-ant-stub"
        assert stub.body["model"] == "some-model-id"
        assert stub.body["max_tokens"] == 512
        assert stub.body["system"] == "You are ANALYTICS."
        assert stub.body["messages"][0] == {"role": "user", "content": "TASK: Evaluate"}

    def test_prefill_is_sent_as_an_assistant_turn(self):
        """How JSON-only output gets forced without spending prompt tokens
        pleading for it."""
        with _Stub(_anthropic_reply()) as stub:
            _substrate("anthropic", stub.url, "ECI_TEST_KEY").complete(
                system="s", user="u", prefill="{")
        assert stub.body["messages"][-1] == {"role": "assistant", "content": "{"}

    def test_the_prefill_is_rejoined_to_the_response(self):
        """The vendor returns only the tail. Callers must see a whole
        document, or every parse fails on a stray leading brace."""
        with _Stub(_anthropic_reply()) as stub:
            response = _substrate("anthropic", stub.url, "ECI_TEST_KEY").complete(
                system="s", user="u", prefill="{")
        assert response.text.startswith('{"recommendation"')
        assert json.loads(response.text)["proceed"] is True

    def test_usage_and_model_are_reported(self):
        with _Stub(_anthropic_reply(usage={"input_tokens": 11, "output_tokens": 22})) as stub:
            response = _substrate("anthropic", stub.url, "ECI_TEST_KEY").complete(
                system="s", user="u")
        assert response.usage == {"input_tokens": 11, "output_tokens": 22}
        assert response.model == "some-model-id"
        assert response.provider == "anthropic"

    def test_unsupported_parameters_are_dropped_and_reported(self, capsys):
        """The bug this file was written to catch. The 1.x Messages API
        has no `temperature`; passing it raises TypeError on the first
        live call. The adapter must drop it and say so, not crash and not
        silently pretend the manifest's value took effect."""
        with _Stub(_anthropic_reply()) as stub:
            substrate = _substrate("anthropic", stub.url, "ECI_TEST_KEY")
            substrate.complete(system="s", user="u", temperature=0.2)

        provider = substrate.provider
        if "temperature" in provider.unsupported_params:
            assert "temperature" not in stub.body
            assert "does not accept 'temperature'" in capsys.readouterr().err
        else:
            # An SDK generation that still takes it must actually send it.
            assert stub.body["temperature"] == 0.2

    def test_a_server_error_becomes_a_completion_error(self):
        """Agents catch CompletionError and degrade. A raw vendor
        exception escaping the layer would take the pipeline down."""
        with _Stub(_anthropic_reply(), status=500) as stub:
            with pytest.raises(CompletionError):
                _substrate("anthropic", stub.url, "ECI_TEST_KEY").complete(
                    system="s", user="u")

    def test_the_whole_analytics_contract_survives_the_round_trip(self):
        """Adapter plus contract, end to end: what a real Haiku call will
        do, minus the model."""
        from agents.analytics import contract
        from agents.analytics.contract import Task

        with _Stub(_anthropic_reply()) as stub:
            response = _substrate("anthropic", stub.url, "ECI_TEST_KEY").complete(
                system="s", user="u", prefill="{")

        recommendation = contract.parse(response.text, Task.EVALUATE)
        assert recommendation.proceed is True
        assert recommendation.recommendation == "Keep it brief."


# ---------------------------------------------------------------------------
# OpenAI-compatible
# ---------------------------------------------------------------------------

class TestOpenAICompatibleAdapter:
    @pytest.fixture(autouse=True)
    def _sdk(self, monkeypatch):
        pytest.importorskip("openai", reason="pip install -r requirements-dev.txt")
        monkeypatch.setenv("ECI_TEST_KEY", "sk-openai-stub")

    def test_it_puts_a_well_formed_request_on_the_wire(self):
        with _Stub(_openai_reply()) as stub:
            _substrate("openai", stub.url + "/v1", "ECI_TEST_KEY").complete(
                system="You are ANALYTICS.", user="TASK: Evaluate", temperature=0.2)

        assert stub.body["model"] == "some-model-id"
        roles = [m["role"] for m in stub.body["messages"]]
        assert roles[:2] == ["system", "user"]

    def test_a_keyless_local_endpoint_works(self):
        """Ollama, LM Studio, vLLM — no credential, base_url required."""
        with _Stub(_openai_reply()) as stub:
            response = _substrate("ollama", stub.url + "/v1", None).complete(
                system="s", user="u")
        assert json.loads(response.text)["proceed"] is True

    def test_usage_is_normalised_to_the_neutral_shape(self):
        """Different vendors, one vocabulary — prompt_tokens becomes
        input_tokens so nothing above this layer learns a dialect."""
        with _Stub(_openai_reply()) as stub:
            response = _substrate("openai", stub.url + "/v1", "ECI_TEST_KEY").complete(
                system="s", user="u")
        assert response.usage == {"input_tokens": 195, "output_tokens": 22}

    def test_a_server_error_becomes_a_completion_error(self):
        with _Stub(_openai_reply(), status=500) as stub:
            with pytest.raises(CompletionError):
                _substrate("openai", stub.url + "/v1", "ECI_TEST_KEY").complete(
                    system="s", user="u")

    def test_max_tokens_rename_is_retried_and_then_remembered(self, capsys):
        """The reasoning-family bug: the model 400s on `max_tokens` and
        names `max_completion_tokens` in the error. inspect.signature()
        can't catch this — the SDK's create() still lists max_tokens, it's
        the specific model that rejects it — so the adapter has to react
        to the vendor's own error text, retry once, and remember the
        model's preference so it isn't paid for on every subsequent call."""
        calls = []

        def responder(request):
            calls.append(request)
            if len(calls) == 1:
                stub.status = 400
                assert "max_tokens" in request
                assert "max_completion_tokens" not in request
                return {"error": {
                    "message": "Unsupported parameter: 'max_tokens' is not "
                               "supported with this model. Use "
                               "'max_completion_tokens' instead.",
                    "type": "invalid_request_error", "param": "max_tokens",
                    "code": "unsupported_parameter",
                }}
            stub.status = 200
            assert "max_completion_tokens" in request
            assert "max_tokens" not in request
            return {
                "id": "chatcmpl-stub", "object": "chat.completion", "created": 0,
                "model": request.get("model", "?"),
                "choices": [{"index": 0, "finish_reason": "stop",
                             "message": {"role": "assistant", "content": "ok"}}],
                "usage": {"prompt_tokens": 5, "completion_tokens": 1, "total_tokens": 6},
            }

        with _Stub(responder) as stub:
            substrate = _substrate("openai", stub.url + "/v1", "ECI_TEST_KEY",
                                   model="gpt-5.4-nano")

            # First call: fails on max_tokens, retries once, succeeds.
            response = substrate.complete(system="s", user="u")
            assert response.text == "ok"
            assert len(calls) == 2
            assert "rejected 'max_tokens'" in capsys.readouterr().err

            # Second call, same provider instance: goes straight to
            # max_completion_tokens — no wasted failing round trip.
            response2 = substrate.complete(system="s", user="u")
            assert response2.text == "ok"
            assert len(calls) == 3
            assert "max_completion_tokens" in calls[-1]

    def test_a_genuine_bad_request_is_not_mistaken_for_the_rename(self):
        """A 400 that has nothing to do with max_tokens must still surface
        as an ordinary CompletionError, not trigger the retry path."""
        with _Stub(lambda request: {"error": {"message": "invalid model id"}},
                   status=400) as stub:
            with pytest.raises(CompletionError):
                _substrate("openai", stub.url + "/v1", "ECI_TEST_KEY").complete(
                    system="s", user="u")

    def test_a_pinned_token_param_skips_the_probe_entirely(self):
        """Once a model's requirement is confirmed (e.g. by a --live
        preflight run), the manifest can pin it via
        `options: {token_param: max_completion_tokens}` — first call
        succeeds directly, no failing round trip, no retry at all."""
        calls = []

        def responder(request):
            calls.append(request)
            assert "max_completion_tokens" in request
            assert "max_tokens" not in request
            return {
                "id": "chatcmpl-stub", "object": "chat.completion", "created": 0,
                "model": request.get("model", "?"),
                "choices": [{"index": 0, "finish_reason": "stop",
                             "message": {"role": "assistant", "content": "ok"}}],
                "usage": {"prompt_tokens": 5, "completion_tokens": 1, "total_tokens": 6},
            }

        with _Stub(responder) as stub:
            substrate = resolve_substrate({"substrates": {"probe": {
                "provider": "openai", "model": "gpt-5.4-nano",
                "api_key_env": "ECI_TEST_KEY", "base_url": stub.url + "/v1",
                "max_tokens": 512,
                "options": {"token_param": "max_completion_tokens"},
            }}}, "probe")
            response = substrate.complete(system="s", user="u")

        assert response.text == "ok"
        assert len(calls) == 1
        assert stub.status == 200          # never touched the 400 path


# ---------------------------------------------------------------------------
# Two vendors, one agent
# ---------------------------------------------------------------------------

def test_the_same_agent_code_runs_on_either_vendor(monkeypatch):
    """§10.2's actual claim, demonstrated rather than asserted about: the
    identical Analytics call path, two different wire protocols, one
    manifest edit apart."""
    pytest.importorskip("anthropic")
    pytest.importorskip("openai")
    monkeypatch.setenv("ECI_TEST_KEY", "stub")

    from agents.analytics import contract
    from agents.analytics.contract import Task

    results = []
    for provider, responder, suffix in (
        ("anthropic", _anthropic_reply(), ""),
        ("openai", _openai_reply(), "/v1"),
    ):
        with _Stub(responder) as stub:
            response = _substrate(provider, stub.url + suffix, "ECI_TEST_KEY").complete(
                system="s", user="u", prefill="{")
        results.append(contract.parse(response.text, Task.EVALUATE))

    assert results[0].recommendation == results[1].recommendation
    assert results[0].proceed == results[1].proceed
