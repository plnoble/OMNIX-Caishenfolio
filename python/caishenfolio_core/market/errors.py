"""Typed failures from a data source.

Collapsing every upstream problem into a string loses the one distinction that changes what to
do next: being rate-limited means *wait and ask again*, while an unreachable or broken source
means *move on to another one*. Retrying a parse error is pointless; giving up on a 429 wastes
a source that would have answered a second later.
"""

from __future__ import annotations

import re


class DataSourceError(Exception):
    """Base for anything a provider could not deliver."""

    #: Short code attached to ProviderResult warnings so callers can branch without parsing text.
    code = "data_source_error"
    #: Whether asking the same source again could plausibly succeed.
    retryable = False


class RateLimitError(DataSourceError):
    """The source refused because we asked too often. Waiting is the fix."""

    code = "rate_limited"
    retryable = True

    def __init__(self, message: str, retry_after_seconds: float | None = None) -> None:
        super().__init__(message)
        self.retry_after_seconds = retry_after_seconds


class DataSourceUnavailableError(DataSourceError):
    """The source is unreachable, down, or blocked. Another source is the fix."""

    code = "source_unavailable"
    retryable = False


class DataParseError(DataSourceError):
    """The source answered with something we cannot read. Retrying changes nothing."""

    code = "parse_error"
    retryable = False


class SymbolNotSupportedError(DataSourceError):
    """This source does not cover this instrument. Not a failure of the source."""

    code = "unsupported_symbol"
    retryable = False


_RATE_LIMIT_PATTERNS = (
    r"\b429\b",
    r"too many requests",
    r"rate.?limit",
    r"请求过于频繁",
    r"访问频率",
    r"超过.{0,6}限制",
)

_UNAVAILABLE_PATTERNS = (
    r"\b50[0-4]\b",
    r"timed? ?out",
    r"timeout",
    r"connection (refused|reset|aborted|error)",
    r"unreachable",
    r"proxy",
    r"ssl",
    r"name resolution",
    r"temporarily unavailable",
    r"连接.{0,4}(失败|超时|拒绝)",
    r"代理",
)


def classify(error: BaseException | str) -> DataSourceError:
    """Maps an arbitrary upstream failure onto a typed one.

    Upstream libraries raise whatever they like, so the shape has to be recovered from the
    message. The default is *unavailable* rather than *rate limited*: assuming a rate limit
    would make the caller sit and wait on a source that is simply broken.
    """
    if isinstance(error, DataSourceError):
        return error

    message = str(error)
    lowered = message.lower()

    if any(re.search(p, lowered) for p in _RATE_LIMIT_PATTERNS):
        return RateLimitError(message, _retry_after(message))

    if any(re.search(p, lowered) for p in _UNAVAILABLE_PATTERNS):
        return DataSourceUnavailableError(message)

    if isinstance(error, (ValueError, TypeError, KeyError, IndexError)):
        return DataParseError(message)

    return DataSourceUnavailableError(message)


def _retry_after(message: str) -> float | None:
    match = re.search(r"retry[- ]after[:=\s]+(\d+(?:\.\d+)?)", message, re.IGNORECASE)
    return float(match.group(1)) if match else None


def warning_tags(error: DataSourceError) -> tuple[str, ...]:
    """Warning codes for a ProviderResult, so upstream shape survives into the payload."""
    tags = [error.code, "fail_closed"]
    if error.retryable:
        tags.append("retryable")
    return tuple(tags)
