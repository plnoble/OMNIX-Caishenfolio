from __future__ import annotations

from threading import Lock
from uuid import uuid4

from caishenfolio_core.tasks.models import (
    ArtifactRecord,
    AuditEvent,
    TaskKind,
    TaskStatus,
    WorkTask,
    _utc_now,
)


class InMemoryTaskStore:
    def __init__(self) -> None:
        self._lock = Lock()
        self._tasks: dict[str, WorkTask] = {}
        self._artifacts: dict[str, ArtifactRecord] = {}
        self._audits: list[AuditEvent] = []

    def create_task(
        self,
        kind: TaskKind,
        title: str,
        metadata: dict[str, str] | None = None,
    ) -> WorkTask:
        if not title or not title.strip():
            raise ValueError("Task title is required.")
        task = WorkTask(
            id=f"task_{uuid4().hex}",
            kind=kind,
            title=title.strip(),
            metadata=dict(metadata or {}),
        )
        with self._lock:
            self._tasks[task.id] = task
            self._audits.append(
                AuditEvent(
                    id=f"audit_{uuid4().hex}",
                    task_id=task.id,
                    event_type="task.created",
                    message=f"Created {kind.value} task.",
                    metadata={"title": task.title},
                )
            )
            return self._clone_task(task)

    def get_task(self, task_id: str) -> WorkTask | None:
        with self._lock:
            task = self._tasks.get(task_id)
            return None if task is None else self._clone_task(task)

    def list_tasks(
        self,
        *,
        kind: TaskKind | None = None,
        status: TaskStatus | None = None,
        limit: int = 50,
    ) -> list[WorkTask]:
        limit = max(1, min(limit, 200))
        with self._lock:
            items = list(self._tasks.values())
        if kind is not None:
            items = [item for item in items if item.kind == kind]
        if status is not None:
            items = [item for item in items if item.status == status]
        items.sort(key=lambda item: item.updated_at, reverse=True)
        return [self._clone_task(item) for item in items[:limit]]

    def update_status(self, task_id: str, status: TaskStatus, summary: str | None = None) -> WorkTask:
        with self._lock:
            task = self._tasks.get(task_id)
            if task is None:
                raise KeyError(f"Unknown task '{task_id}'.")
            task.status = status
            task.updated_at = _utc_now()
            if summary is not None:
                task.summary = summary
            self._audits.append(
                AuditEvent(
                    id=f"audit_{uuid4().hex}",
                    task_id=task_id,
                    event_type="task.status",
                    message=f"Status -> {status.value}",
                    metadata={"status": status.value},
                )
            )
            return self._clone_task(task)

    def add_artifact(
        self,
        task_id: str,
        kind: str,
        title: str,
        *,
        uri_or_payload: str | None = None,
        content_type: str | None = None,
    ) -> ArtifactRecord:
        if not kind.strip() or not title.strip():
            raise ValueError("Artifact kind and title are required.")
        with self._lock:
            task = self._tasks.get(task_id)
            if task is None:
                raise KeyError(f"Unknown task '{task_id}'.")
            artifact = ArtifactRecord(
                id=f"artifact_{uuid4().hex}",
                task_id=task_id,
                kind=kind.strip(),
                title=title.strip(),
                uri_or_payload=uri_or_payload,
                content_type=content_type,
            )
            self._artifacts[artifact.id] = artifact
            task.artifact_ids.append(artifact.id)
            task.updated_at = _utc_now()
            self._audits.append(
                AuditEvent(
                    id=f"audit_{uuid4().hex}",
                    task_id=task_id,
                    event_type="artifact.created",
                    message=f"Artifact {artifact.kind}: {artifact.title}",
                    metadata={"artifact_id": artifact.id},
                )
            )
            return artifact

    def get_artifact(self, artifact_id: str) -> ArtifactRecord | None:
        with self._lock:
            return self._artifacts.get(artifact_id)

    def list_audit(
        self,
        task_id: str,
        *,
        event_type: str | None = None,
        limit: int = 50,
    ) -> list[AuditEvent]:
        limit = max(1, min(limit, 500))
        with self._lock:
            items = [item for item in self._audits if item.task_id == task_id]
        if event_type:
            items = [item for item in items if item.event_type == event_type]
        items.sort(key=lambda item: item.timestamp, reverse=True)
        return items[:limit]

    @staticmethod
    def _clone_task(task: WorkTask) -> WorkTask:
        return WorkTask(
            id=task.id,
            kind=task.kind,
            title=task.title,
            status=task.status,
            created_at=task.created_at,
            updated_at=task.updated_at,
            summary=task.summary,
            artifact_ids=list(task.artifact_ids),
            metadata=dict(task.metadata),
        )
