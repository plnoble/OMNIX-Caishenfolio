from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import StrEnum


class TaskStatus(StrEnum):
    CREATED = "created"
    RUNNING = "running"
    WAITING_FOR_USER = "waiting_for_user"
    SUCCEEDED = "succeeded"
    FAILED = "failed"
    CANCELLED = "cancelled"


class TaskKind(StrEnum):
    SYSTEM = "system"
    MARKET_DATA = "market_data"
    RESEARCH = "research"
    SIMULATION = "simulation"
    REPORT = "report"


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass
class WorkTask:
    id: str
    kind: TaskKind
    title: str
    status: TaskStatus = TaskStatus.CREATED
    created_at: datetime = field(default_factory=_utc_now)
    updated_at: datetime = field(default_factory=_utc_now)
    summary: str | None = None
    artifact_ids: list[str] = field(default_factory=list)
    metadata: dict[str, str] = field(default_factory=dict)

    def to_dict(self) -> dict[str, object]:
        return {
            "id": self.id,
            "kind": self.kind.value,
            "title": self.title,
            "status": self.status.value,
            "created_at": self.created_at.isoformat(),
            "updated_at": self.updated_at.isoformat(),
            "summary": self.summary,
            "artifact_ids": list(self.artifact_ids),
            "metadata": dict(self.metadata),
        }


@dataclass(frozen=True)
class ArtifactRecord:
    id: str
    task_id: str
    kind: str
    title: str
    content_type: str | None = None
    uri_or_payload: str | None = None
    created_at: datetime = field(default_factory=_utc_now)
    metadata: dict[str, str] = field(default_factory=dict)

    def to_dict(self) -> dict[str, object]:
        return {
            "id": self.id,
            "task_id": self.task_id,
            "kind": self.kind,
            "title": self.title,
            "content_type": self.content_type,
            "uri_or_payload": self.uri_or_payload,
            "created_at": self.created_at.isoformat(),
            "metadata": dict(self.metadata),
        }


@dataclass(frozen=True)
class AuditEvent:
    id: str
    task_id: str
    event_type: str
    message: str
    timestamp: datetime = field(default_factory=_utc_now)
    metadata: dict[str, str] = field(default_factory=dict)

    def to_dict(self) -> dict[str, object]:
        return {
            "id": self.id,
            "task_id": self.task_id,
            "event_type": self.event_type,
            "message": self.message,
            "timestamp": self.timestamp.isoformat(),
            "metadata": dict(self.metadata),
        }
