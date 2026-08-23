"""Provider-agnostic substrate layer (§10.2)."""
from substrates.base import (
    CompletionError,
    CompletionRequest,
    CompletionResponse,
    CredentialsError,
    LLMProvider,
    Substrate,
    SubstrateError,
)
from substrates.registry import (
    UnknownSubstrate,
    build_provider,
    register_provider,
    resolve_role_substrate,
    resolve_substrate,
)

__all__ = [
    "CompletionError",
    "CompletionRequest",
    "CompletionResponse",
    "CredentialsError",
    "LLMProvider",
    "Substrate",
    "SubstrateError",
    "UnknownSubstrate",
    "build_provider",
    "register_provider",
    "resolve_role_substrate",
    "resolve_substrate",
]
