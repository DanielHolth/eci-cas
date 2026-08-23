"""
Substrate class -> provider resolution (§10.2).

This is the single place the manifest's `substrates:` table is read. An
agent asks for a class name ("fast-reflex") and gets back a Substrate;
it never sees a vendor name, a model id or an API key.

Manifest shape (all keys optional except `model`):

    substrates:
      fast-reflex:
        provider:    "anthropic"            # registry key, default "anthropic"
        model:       "claude-haiku-4-5"     # vendor model id (forensic, §7.4)
        api_key_env: "ANTHROPIC_API_KEY"    # env var holding the credential
        base_url:    null                   # override endpoint (gateways, local)
        max_tokens:  512
        options:     {}                     # provider-specific extras
        notes:       "live duty: concise, cheap, low-latency"

The v0.32 short form — `fast-reflex: { model: "...", notes: "..." }` — is
still accepted and defaults to the Anthropic provider, so an existing
manifest keeps working untouched.
"""
from __future__ import annotations

from typing import Any, Dict, Mapping, Optional, Type

from substrates.base import CredentialsError, LLMProvider, Substrate, SubstrateError
from substrates.providers import (
    AnthropicProvider,
    EchoProvider,
    OpenAICompatibleProvider,
)

#: Registry key -> adapter class. Extend with register_provider().
PROVIDERS: Dict[str, Type[LLMProvider]] = {
    AnthropicProvider.name: AnthropicProvider,
    OpenAICompatibleProvider.name: OpenAICompatibleProvider,
    EchoProvider.name: EchoProvider,
}

#: Friendly aliases, so a manifest can say "openai" or "ollama" and land
#: on the right adapter without anyone memorising the canonical key.
PROVIDER_ALIASES: Dict[str, str] = {
    "openai": OpenAICompatibleProvider.name,
    "azure-openai": OpenAICompatibleProvider.name,
    "groq": OpenAICompatibleProvider.name,
    "together": OpenAICompatibleProvider.name,
    "mistral": OpenAICompatibleProvider.name,
    "openrouter": OpenAICompatibleProvider.name,
    "vllm": OpenAICompatibleProvider.name,
    "ollama": OpenAICompatibleProvider.name,
    "lmstudio": OpenAICompatibleProvider.name,
    "claude": AnthropicProvider.name,
    "mock": EchoProvider.name,
    "none": EchoProvider.name,
}

DEFAULT_PROVIDER = AnthropicProvider.name
DEFAULT_MAX_TOKENS = 512

#: Fallback credential env var per provider, used when the manifest
#: doesn't name one explicitly.
DEFAULT_KEY_ENV: Dict[str, str] = {
    AnthropicProvider.name: "ANTHROPIC_API_KEY",
    OpenAICompatibleProvider.name: "OPENAI_API_KEY",
}


class UnknownSubstrate(SubstrateError):
    """Raised when a role asks for a substrate class the manifest doesn't declare."""


def register_provider(name: str, cls: Type[LLMProvider]) -> None:
    """Add a vendor adapter at runtime. Nothing above this layer changes."""
    if not issubclass(cls, LLMProvider):
        raise TypeError(f"{cls!r} is not an LLMProvider subclass")
    PROVIDERS[name] = cls


def _resolve_provider_key(raw: Optional[str]) -> str:
    key = (raw or DEFAULT_PROVIDER).strip().lower()
    key = PROVIDER_ALIASES.get(key, key)
    if key not in PROVIDERS:
        raise UnknownSubstrate(
            f"Unknown provider '{raw}'. Registered: {sorted(PROVIDERS)} "
            f"(aliases: {sorted(PROVIDER_ALIASES)})."
        )
    return key


def build_provider(config: Mapping[str, Any]) -> LLMProvider:
    """Instantiate one vendor adapter from a manifest substrate entry."""
    key = _resolve_provider_key(config.get("provider"))
    cls = PROVIDERS[key]

    # `api_key_env` may be explicitly null to mean "this endpoint needs no
    # key" (local runtimes). Only fall back to the default when the key is
    # absent from the mapping entirely.
    if "api_key_env" in config:
        api_key_env = config["api_key_env"]
    else:
        api_key_env = DEFAULT_KEY_ENV.get(key)

    return cls(
        api_key_env=api_key_env,
        base_url=config.get("base_url"),
        options=dict(config.get("options", {})),
    )


def resolve_substrate(manifest: Mapping[str, Any], substrate_class: str) -> Substrate:
    """Resolve one manifest substrate class into a callable Substrate.

    Raises UnknownSubstrate if the class isn't declared, or if its entry
    has no model id."""
    table = manifest.get("substrates") or {}
    if substrate_class not in table:
        raise UnknownSubstrate(
            f"Manifest declares no substrate class '{substrate_class}'. "
            f"Declared: {sorted(table)}."
        )

    config = table[substrate_class] or {}
    if isinstance(config, str):           # tolerated shorthand: just a model id
        config = {"model": config}

    model = config.get("model")
    if not model:
        raise UnknownSubstrate(
            f"Substrate class '{substrate_class}' declares no 'model'."
        )

    # Prices live beside the model, for the same reason model ids do:
    # they are vendor-specific and they change (§10.2). Undeclared means
    # zero, so an unpriced substrate reads as free rather than as a made-up
    # number — budget mode reports "unpriced" instead of guessing.
    prices = config.get("price_per_mtok") or {}
    timeout = config.get("timeout_sec")

    return Substrate(
        substrate_class=substrate_class,
        provider=build_provider(config),
        model=model,
        max_tokens=int(config.get("max_tokens", DEFAULT_MAX_TOKENS)),
        notes=str(config.get("notes", "")),
        price_per_mtok_in=float(prices.get("input", 0.0) or 0.0),
        price_per_mtok_out=float(prices.get("output", 0.0) or 0.0),
        timeout_sec=float(timeout) if timeout else None,
    )


def resolve_role_substrate(manifest: Mapping[str, Any], role: str) -> Substrate:
    """Resolve the substrate for a named role from `roles.<role>.substrate`."""
    roles = manifest.get("roles") or {}
    role_config = roles.get(role) or {}
    substrate_class = role_config.get("substrate")
    if not substrate_class:
        raise UnknownSubstrate(
            f"Role '{role}' declares no 'substrate' class in the manifest."
        )
    return resolve_substrate(manifest, substrate_class)


__all__ = [
    "PROVIDERS",
    "PROVIDER_ALIASES",
    "UnknownSubstrate",
    "CredentialsError",
    "register_provider",
    "build_provider",
    "resolve_substrate",
    "resolve_role_substrate",
]
