"""
Substrate client layer — provider-agnostic LLM access (§10.2).

The spec's rule is that the ecosystem declares *substrate classes*
("local" / "low" / "medium" / "high"), never vendor model names, and
that the class -> vendor mapping lives in exactly one place (the
manifest). This module is the code-side half of that contract:
agents ask for a substrate class and receive an object they can call,
with no idea which vendor is behind it.

    "Today's model names are tomorrow's legacy code." (§1.1)

Three types live here:

  CompletionRequest / CompletionResponse
      The neutral wire shape. Deliberately minimal — a system
      instruction, a single user turn, temperature, a token ceiling,
      and an optional assistant prefill. Anything an agent needs
      beyond this belongs in the agent, not in the substrate layer.

  LLMProvider
      The vendor adapter ABC. Two required methods: an OFFLINE
      credential check (validate_credentials) and the actual call
      (complete). The offline check exists because Recovery must carry
      "zero LLM API dependency during deployment" (§9) — bootstrap
      needs to know a substrate is *configured* without proving it is
      *reachable*.

  Substrate
      A provider bound to a concrete model id and default parameters,
      i.e. the resolved form of one manifest substrate class. This is
      what an agent actually holds.

Adding a vendor means adding one LLMProvider subclass and registering
it (see substrates/registry.py). No agent code changes, no spec churn.
"""
from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Dict, Optional


class SubstrateError(RuntimeError):
    """Base class for every failure originating in the substrate layer."""


class CredentialsError(SubstrateError):
    """Raised by validate_credentials() when a substrate is declared but
    not usable — a missing API key, an unset base URL, an SDK that isn't
    installed. Raised OFFLINE, at bootstrap, so Recovery can stop
    deterministically (§9.1 step 6) instead of failing on first traffic."""


class FailureKind(str, Enum):
    """Why a completion failed, in terms an agent can act on.

    The distinction is load-bearing for budget mode (budget/state.py). A
    failure counter that treats every error alike is dishonest in both
    directions: it latches on a blip that would have cleared, and it keeps
    retrying an expired key forever.

      TRANSIENT  Rate limited, overloaded, timed out, connection dropped.
                 The vendor SDK has ALREADY retried these (max_retries=2
                 with backoff) before we see them, so one reaching us
                 means the retries were exhausted — worth counting, not
                 worth panicking over. Latches after N consecutive.
      TERMINAL   Bad key, revoked permission, exhausted credit, unknown
                 model. Retrying cannot help; the next call fails the
                 same way. Latches immediately.
      UNKNOWN    Anything unclassified. Treated as transient, because
                 assuming the pessimistic case would let one unfamiliar
                 error shut down the pipeline.
    """

    TRANSIENT = "transient"
    TERMINAL = "terminal"
    UNKNOWN = "unknown"


class CompletionError(SubstrateError):
    """Raised when an actual completion call fails — network, rate limit,
    auth rejection, malformed vendor response. Agents are expected to
    catch this and degrade deterministically rather than propagate it.

    Carries a FailureKind so callers can tell "try again later" from
    "this will never work"."""

    def __init__(self, message: str, *,
                 kind: FailureKind = FailureKind.UNKNOWN,
                 status_code: Optional[int] = None):
        super().__init__(message)
        self.kind = kind
        self.status_code = status_code

    @property
    def is_terminal(self) -> bool:
        return self.kind is FailureKind.TERMINAL


@dataclass(frozen=True)
class CompletionRequest:
    """Vendor-neutral request. `prefill` seeds the assistant's turn where
    the provider supports it (used to force JSON-only output); providers
    that don't support it must ignore it rather than fail."""
    system: str
    user: str
    temperature: float = 0.0
    max_tokens: int = 512
    prefill: Optional[str] = None
    #: Wall-clock ceiling for one call. Vendor SDKs default to something
    #: close to unbounded, and one hung request hangs the whole pipeline —
    #: the bus dispatches synchronously (§3).
    timeout_sec: Optional[float] = None


@dataclass(frozen=True)
class CompletionResponse:
    """Vendor-neutral response.

    `text` is the completion with the prefill already re-attached where
    one was used, so callers always see a whole document rather than the
    tail of one. `model` is the id the vendor actually served, recorded
    for the forensic `source_model` field on epochs (§7.4)."""
    text: str
    model: str
    provider: str
    usage: Dict[str, Any] = field(default_factory=dict)
    raw: Any = None


class LLMProvider(ABC):
    """One vendor adapter. Stateless with respect to conversations —
    every complete() call is independent, which is what lets Governance
    keep its per-event statutory context reset (§5.1) honest."""

    #: Registry key, e.g. "anthropic". Set by each subclass.
    name: str = "abstract"

    def __init__(self, *, api_key_env: Optional[str] = None,
                 base_url: Optional[str] = None,
                 options: Optional[Dict[str, Any]] = None) -> None:
        self.api_key_env = api_key_env
        self.base_url = base_url
        self.options: Dict[str, Any] = dict(options or {})

    @abstractmethod
    def validate_credentials(self) -> None:
        """Offline readiness check. MUST NOT make a network call.
        Raises CredentialsError when this provider could not currently
        serve a request."""

    @abstractmethod
    def complete(self, request: CompletionRequest, *, model: str) -> CompletionResponse:
        """Execute one completion. Raises CompletionError on any failure."""

    def __repr__(self) -> str:  # pragma: no cover - debug aid
        return f"<{type(self).__name__} name={self.name!r} base_url={self.base_url!r}>"


@dataclass
class Substrate:
    """A manifest substrate class, resolved: provider + model + defaults.

    Agents hold one of these and never learn the vendor. `substrate_class`
    is the stable, analytical name that goes into epoch records; `model`
    is the forensic value resolved at write time (§10.2)."""

    substrate_class: str
    provider: LLMProvider
    model: str
    max_tokens: int = 512
    notes: str = ""
    #: USD per million tokens, declared in the manifest beside the model
    #: (§10.2). Prices belong with the vendor mapping for the same reason
    #: model ids do: they change, they are vendor-specific, and no agent
    #: should ever have to know one.
    price_per_mtok_in: float = 0.0
    price_per_mtok_out: float = 0.0
    timeout_sec: Optional[float] = None

    def validate_credentials(self) -> None:
        self.provider.validate_credentials()

    def complete(self, *, system: str, user: str, temperature: float = 0.0,
                 max_tokens: Optional[int] = None,
                 prefill: Optional[str] = None) -> CompletionResponse:
        request = CompletionRequest(
            system=system,
            user=user,
            temperature=temperature,
            max_tokens=max_tokens or self.max_tokens,
            prefill=prefill,
            timeout_sec=self.timeout_sec,
        )
        return self.provider.complete(request, model=self.model)

    def estimate_cost(self, usage: Optional[Dict[str, Any]]) -> float:
        """Estimated USD for one call, from its reported token usage.

        ESTIMATE is the operative word: it uses manifest-declared list
        prices and ignores cache discounts, batch pricing and tier
        differences. Good enough to notice a runaway loop; not an invoice.
        Returns 0.0 when prices are undeclared, so an unpriced substrate
        reads as free rather than as a spurious number."""
        if not usage:
            return 0.0
        tokens_in = usage.get("input_tokens") or 0
        tokens_out = usage.get("output_tokens") or 0
        return round(
            (tokens_in / 1_000_000) * self.price_per_mtok_in
            + (tokens_out / 1_000_000) * self.price_per_mtok_out,
            8,
        )

    @property
    def has_prices(self) -> bool:
        return bool(self.price_per_mtok_in or self.price_per_mtok_out)

    @property
    def provider_name(self) -> str:
        return self.provider.name

    def describe(self) -> str:
        return f"{self.substrate_class} -> {self.provider.name}:{self.model}"
