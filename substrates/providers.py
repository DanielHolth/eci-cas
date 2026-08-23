"""
Vendor adapters (§10.2).

Three ship in the box:

  AnthropicProvider        native Anthropic Messages API
  OpenAICompatibleProvider any /v1/chat/completions endpoint — OpenAI,
                            Groq, Together, Mistral, OpenRouter, vLLM,
                            Ollama, LM Studio, an in-house gateway
  EchoProvider             offline, deterministic, zero-cost stub

Every vendor SDK import is LAZY, inside the method that needs it. That is
load-bearing, not tidiness: Recovery must be able to parse a manifest,
provision the ecosystem and health-check it with no vendor SDK installed
at all (§9's "zero LLM API dependency during deployment"). A provider
that is declared but unusable surfaces as a CredentialsError from the
offline validate_credentials() check, which stops bootstrap cleanly
(§9.1 step 6) rather than exploding on the first real event.

Adding a fourth vendor is a subclass plus a register_provider() call.
Nothing above this layer changes.
"""
from __future__ import annotations

import inspect
import os
import re
import sys
from typing import Any, Callable, Dict, List, Optional, Set, Tuple

from substrates.base import (
    CompletionError,
    CompletionRequest,
    CompletionResponse,
    CredentialsError,
    FailureKind,
    LLMProvider,
)

#: HTTP status -> FailureKind. Vendor-neutral: both adapters read the
#: status code off the exception rather than matching class names, so a
#: vendor renaming an exception doesn't silently reclassify a failure.
#:
#: 400 is deliberately TERMINAL. It is normally a malformed request, but
#: it is also how an exhausted credit balance arrives, and both mean "the
#: next identical call fails identically". Retrying either is waste.
_STATUS_KINDS = {
    400: FailureKind.TERMINAL,    # malformed request, or credit exhausted
    401: FailureKind.TERMINAL,    # bad or revoked key
    403: FailureKind.TERMINAL,    # permission denied
    404: FailureKind.TERMINAL,    # unknown model
    413: FailureKind.TERMINAL,    # request too large — resending won't shrink it
    422: FailureKind.TERMINAL,
    408: FailureKind.TRANSIENT,
    409: FailureKind.TRANSIENT,
    429: FailureKind.TRANSIENT,   # rate limited
    500: FailureKind.TRANSIENT,
    502: FailureKind.TRANSIENT,
    503: FailureKind.TRANSIENT,   # service unavailable
    504: FailureKind.TRANSIENT,   # deadline exceeded
    529: FailureKind.TRANSIENT,   # overloaded
}

#: Exception class-name fragments for failures that carry no status code
#: (connection dropped, socket timeout). Matched case-insensitively.
_TRANSIENT_NAME_HINTS = ("timeout", "connection", "overloaded", "ratelimit",
                         "unavailable", "apiconnection")

#: OpenAI's newer reasoning-family models (gpt-5.x, o-series, and whatever
#: comes after) reject `max_tokens` at the API layer and ask for
#: `max_completion_tokens` instead. This is a MODEL-level restriction, not
#: an SDK one — `_split_supported`'s inspect.signature() check can't catch
#: it, because the installed SDK's create() still lists `max_tokens` as a
#: parameter and forwards it; it's the API that 400s on the specific model.
#: Matched on the vendor's own error text (a 400 whose message names both
#: parameters) rather than a model-name allowlist, so a future model with
#: the same quirk is caught without a code change here.
_MAX_TOKENS_RENAME_HINT = re.compile(
    r"unsupported parameter.{0,40}'?max_tokens'?.{0,80}max_completion_tokens",
    re.IGNORECASE | re.DOTALL,
)


def _is_max_tokens_rename_error(exc: Exception) -> bool:
    haystack = str(exc)
    body = getattr(exc, "body", None)
    if body:
        haystack += " " + str(body)
    return bool(_MAX_TOKENS_RENAME_HINT.search(haystack))


def _classify(exc: Exception) -> Tuple[FailureKind, Optional[int]]:
    """Map a vendor exception onto a FailureKind and status code.

    Status first, then the class name, then UNKNOWN — which callers treat
    as transient. Erring toward transient is deliberate: mistaking a
    permanent failure for a temporary one costs a few wasted calls, while
    mistaking a temporary one for permanent takes the pipeline down until
    a human intervenes."""
    status = getattr(exc, "status_code", None)
    if isinstance(status, int) and status in _STATUS_KINDS:
        return _STATUS_KINDS[status], status

    name = type(exc).__name__.lower()
    if any(hint in name for hint in _TRANSIENT_NAME_HINTS):
        return FailureKind.TRANSIENT, status if isinstance(status, int) else None

    return FailureKind.UNKNOWN, status if isinstance(status, int) else None


def _require_api_key(env_var: Optional[str], provider: str) -> str:
    if not env_var:
        raise CredentialsError(
            f"Provider '{provider}' has no api_key_env declared in the manifest."
        )
    key = os.environ.get(env_var, "").strip()
    if not key:
        raise CredentialsError(
            f"Provider '{provider}' needs environment variable '{env_var}', "
            f"which is unset or empty."
        )
    return key


def _split_supported(func: Callable, desired: Dict[str, Any]
                     ) -> Tuple[Dict[str, Any], Set[str]]:
    """Keep only the kwargs this SDK build actually accepts.

    Vendor SDKs change shape between generations, and not only in ways
    that add things: the Anthropic 1.x Messages API dropped `temperature`
    from create() outright. A substrate layer whose whole premise is
    "today's model names are tomorrow's legacy code" (§1.1) cannot then
    hardcode today's parameter list — an adapter that passes an unknown
    kwarg raises TypeError on the first live call, which is the worst
    possible time to discover it.

    So the neutral CompletionRequest stays neutral, and each adapter maps
    it onto whatever its SDK supports right now, reporting what it had to
    drop rather than silently ignoring it. A knob that does nothing is a
    lie worth surfacing: a manifest asking for temperature 0.0 and
    quietly getting the default is exactly the kind of drift this project
    treats as a bug.

    Returns (kept, dropped). An SDK that takes **kwargs is trusted with
    everything, since nothing can be inferred from its signature."""
    try:
        params = inspect.signature(func).parameters
    except (TypeError, ValueError):          # pragma: no cover - exotic callables
        return dict(desired), set()

    if any(p.kind is inspect.Parameter.VAR_KEYWORD for p in params.values()):
        return dict(desired), set()

    kept = {k: v for k, v in desired.items() if k in params}
    dropped = {k for k in desired if k not in params}
    return kept, dropped


def _require_module(module: str, provider: str, pip_name: Optional[str] = None) -> Any:
    try:
        return __import__(module)
    except ImportError as exc:  # pragma: no cover - environment-dependent
        raise CredentialsError(
            f"Provider '{provider}' needs the '{module}' package "
            f"(pip install {pip_name or module})."
        ) from exc


# ---------------------------------------------------------------------------
# Anthropic
# ---------------------------------------------------------------------------

class AnthropicProvider(LLMProvider):
    """Native Anthropic Messages API.

    Supports assistant prefill, which the routing agents use to force
    JSON-only output without spending tokens on "respond with only JSON"
    pleading in the system prompt."""

    name = "anthropic"

    def __init__(self, **kwargs: Any) -> None:
        super().__init__(**kwargs)
        #: Neutral request fields this SDK build could not accept. Exposed
        #: so preflight and tests can report them rather than have a
        #: manifest setting quietly do nothing.
        self.unsupported_params: Set[str] = set()
        self._warned: Set[str] = set()

    def _note_dropped(self, dropped: Set[str]) -> None:
        self.unsupported_params |= dropped
        for name in sorted(dropped - self._warned):
            self._warned.add(name)
            print(f"[substrate] NOTE: provider '{self.name}' SDK build does not "
                  f"accept '{name}'; the manifest's value for it has no effect.",
                  file=sys.stderr)

    def validate_credentials(self) -> None:
        # Configuration before installation: a missing key is the more
        # common and more actionable failure, and reporting it first keeps
        # this check's behaviour independent of which SDKs happen to be
        # installed in the environment.
        _require_api_key(self.api_key_env or "ANTHROPIC_API_KEY", self.name)
        _require_module("anthropic", self.name)

    def _client(self):
        anthropic = _require_module("anthropic", self.name)
        key = _require_api_key(self.api_key_env or "ANTHROPIC_API_KEY", self.name)
        kwargs: Dict[str, Any] = {"api_key": key}
        if self.base_url:
            kwargs["base_url"] = self.base_url
        kwargs.update(self.options.get("client_kwargs", {}))
        return anthropic.Anthropic(**kwargs)

    def complete(self, request: CompletionRequest, *, model: str) -> CompletionResponse:
        client = self._client()

        messages: List[Dict[str, Any]] = [{"role": "user", "content": request.user}]
        if request.prefill:
            messages.append({"role": "assistant", "content": request.prefill})

        # Required by every generation of this API; not negotiable.
        kwargs: Dict[str, Any] = {
            "model": model,
            "messages": messages,
            "max_tokens": request.max_tokens,
        }
        # Optional across generations — filtered against the installed SDK.
        optional: Dict[str, Any] = {
            "system": request.system,
            "temperature": request.temperature,
        }
        if request.stop_sequences:
            optional["stop_sequences"] = list(request.stop_sequences)
        if request.timeout_sec:
            optional["timeout"] = request.timeout_sec

        kept, dropped = _split_supported(client.messages.create, optional)
        self._note_dropped(dropped)
        kwargs.update(kept)

        if "system" in dropped:
            # A build with no system parameter still has a user turn.
            kwargs["messages"] = ([{"role": "user", "content": request.system}]
                                  + list(kwargs["messages"]))

        try:
            response = client.messages.create(**kwargs)
        except Exception as exc:  # vendor SDKs raise a wide variety
            kind, status = _classify(exc)
            raise CompletionError(f"anthropic completion failed: {exc}",
                                  kind=kind, status_code=status) from exc

        text = "".join(
            block.text for block in getattr(response, "content", [])
            if getattr(block, "type", None) == "text"
        )
        if request.prefill:
            text = request.prefill + text

        usage = {}
        raw_usage = getattr(response, "usage", None)
        if raw_usage is not None:
            usage = {
                "input_tokens": getattr(raw_usage, "input_tokens", None),
                "output_tokens": getattr(raw_usage, "output_tokens", None),
            }

        return CompletionResponse(
            text=text,
            model=getattr(response, "model", model),
            provider=self.name,
            usage=usage,
            raw=response,
        )


# ---------------------------------------------------------------------------
# OpenAI-compatible (covers most of the rest of the field)
# ---------------------------------------------------------------------------

class OpenAICompatibleProvider(LLMProvider):
    """Any endpoint speaking the /v1/chat/completions dialect.

    One adapter covers OpenAI itself plus every gateway and local runtime
    that mimics it (Groq, Together, Mistral, OpenRouter, vLLM, Ollama,
    LM Studio, in-house proxies) — the manifest just points base_url
    somewhere else. Prefill is emulated with a trailing assistant message
    where the endpoint tolerates it, and ignored otherwise."""

    name = "openai-compatible"

    def __init__(self, **kwargs: Any) -> None:
        super().__init__(**kwargs)
        self.unsupported_params: Set[str] = set()
        self._warned: Set[str] = set()
        #: model -> "max_tokens" | "max_completion_tokens". Learned the
        #: first time a model 400s on the old name (see
        #: _is_max_tokens_rename_error above), so every call after the
        #: first for that model sends the right key straight away instead
        #: of paying for a failing round trip every time.
        self._token_param_by_model: Dict[str, str] = {}

    def _note_dropped(self, dropped: Set[str]) -> None:
        self.unsupported_params |= dropped
        for name in sorted(dropped - self._warned):
            self._warned.add(name)
            print(f"[substrate] NOTE: provider '{self.name}' SDK build does not "
                  f"accept '{name}'; the manifest's value for it has no effect.",
                  file=sys.stderr)

    def validate_credentials(self) -> None:
        # Configuration before installation — see AnthropicProvider.
        # Local runtimes (Ollama, LM Studio) legitimately need no key —
        # declaring `api_key_env: null` opts out of the key check, but a
        # base_url is then mandatory so we never silently hit OpenAI.
        if self.api_key_env:
            _require_api_key(self.api_key_env, self.name)
        elif not self.base_url:
            raise CredentialsError(
                f"Provider '{self.name}' declared without api_key_env must set "
                f"base_url (a keyless endpoint has to be a local/self-hosted one)."
            )
        _require_module("openai", self.name)

    def _client(self):
        openai = _require_module("openai", self.name)
        kwargs: Dict[str, Any] = {}
        if self.api_key_env:
            kwargs["api_key"] = _require_api_key(self.api_key_env, self.name)
        else:
            kwargs["api_key"] = "not-needed"
        if self.base_url:
            kwargs["base_url"] = self.base_url
        kwargs.update(self.options.get("client_kwargs", {}))
        return openai.OpenAI(**kwargs)

    def complete(self, request: CompletionRequest, *, model: str) -> CompletionResponse:
        client = self._client()

        messages: List[Dict[str, Any]] = [
            {"role": "system", "content": request.system},
            {"role": "user", "content": request.user},
        ]
        if request.prefill:
            messages.append({"role": "assistant", "content": request.prefill})

        kwargs: Dict[str, Any] = {"model": model, "messages": messages}
        optional: Dict[str, Any] = {
            "max_tokens": request.max_tokens,
            "temperature": request.temperature,
        }
        if request.stop_sequences:
            optional["stop"] = list(request.stop_sequences)
        if request.timeout_sec:
            optional["timeout"] = request.timeout_sec

        kept, dropped = _split_supported(client.chat.completions.create, optional)
        if "max_tokens" in dropped:
            # The installed SDK build itself has no such parameter —
            # renamed rather than removed.
            kept["max_completion_tokens"] = request.max_tokens
            dropped.discard("max_tokens")
        elif self.options.get("token_param") == "max_completion_tokens":
            # Manifest-pinned (roles/substrate `options: {token_param:
            # max_completion_tokens}`): the requirement is already
            # confirmed for this substrate class, so skip the reactive
            # probe entirely — no failing round trip, not even once.
            kept["max_completion_tokens"] = kept.pop("max_tokens")
        elif self._token_param_by_model.get(model) == "max_completion_tokens":
            # This exact model already told us, on a prior call, that it
            # 400s on max_tokens even though the SDK signature accepts it.
            kept["max_completion_tokens"] = kept.pop("max_tokens")
        self._note_dropped(dropped)
        kwargs.update(kept)

        try:
            response = client.chat.completions.create(**kwargs)
        except Exception as exc:
            if "max_tokens" in kwargs and _is_max_tokens_rename_error(exc):
                # Model-level rejection the SDK signature couldn't predict
                # (see _is_max_tokens_rename_error). Retry once with the
                # renamed parameter and remember it, so every later call
                # against this model goes straight there instead of
                # spending a failing round trip first.
                self._token_param_by_model[model] = "max_completion_tokens"
                kwargs["max_completion_tokens"] = kwargs.pop("max_tokens")
                print(f"[substrate] NOTE: model '{model}' rejected 'max_tokens' "
                      f"('Use max_completion_tokens instead'); retrying with the "
                      f"renamed parameter and remembering this for later calls.",
                      file=sys.stderr)
                try:
                    response = client.chat.completions.create(**kwargs)
                except Exception as exc2:
                    kind, status = _classify(exc2)
                    raise CompletionError(f"openai-compatible completion failed: {exc2}",
                                          kind=kind, status_code=status) from exc2
            else:
                kind, status = _classify(exc)
                raise CompletionError(f"openai-compatible completion failed: {exc}",
                                      kind=kind, status_code=status) from exc

        try:
            text = response.choices[0].message.content or ""
        except (AttributeError, IndexError) as exc:
            raise CompletionError(f"malformed openai-compatible response: {exc}") from exc

        if request.prefill and not text.startswith(request.prefill):
            text = request.prefill + text

        usage = {}
        raw_usage = getattr(response, "usage", None)
        if raw_usage is not None:
            usage = {
                "input_tokens": getattr(raw_usage, "prompt_tokens", None),
                "output_tokens": getattr(raw_usage, "completion_tokens", None),
            }

        return CompletionResponse(
            text=text,
            model=getattr(response, "model", model),
            provider=self.name,
            usage=usage,
            raw=response,
        )


# ---------------------------------------------------------------------------
# Echo — offline, deterministic, free
# ---------------------------------------------------------------------------

class EchoProvider(LLMProvider):
    """Returns canned text. No network, no key, no cost.

    Two uses: developing against the real agent code path with no vendor
    configured at all, and letting a manifest declare a substrate that is
    deliberately inert. Configure via the manifest's `options`:

        options: { script: ["first reply", "second reply"], loop: true }

    With no script it returns an empty string, which every caller in this
    codebase treats as an invalid response and degrades from — that is
    the point: it exercises the fallback path deterministically."""

    name = "echo"

    def __init__(self, **kwargs: Any) -> None:
        super().__init__(**kwargs)
        self._script: List[str] = list(self.options.get("script", []))
        self._loop: bool = bool(self.options.get("loop", True))
        self._cursor = 0
        self.calls: List[CompletionRequest] = []

    def validate_credentials(self) -> None:
        return  # always ready

    def complete(self, request: CompletionRequest, *, model: str) -> CompletionResponse:
        self.calls.append(request)
        if not self._script:
            text = ""
        elif self._cursor < len(self._script):
            text = self._script[self._cursor]
            self._cursor += 1
        elif self._loop:
            self._cursor = 1
            text = self._script[0]
        else:
            text = ""
        return CompletionResponse(text=text, model=model, provider=self.name)
