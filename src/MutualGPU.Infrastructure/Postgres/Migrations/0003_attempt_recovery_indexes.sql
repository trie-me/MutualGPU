create index task_attempts_assigned_recovery_idx
    on task_attempts (assigned_at, task_id)
    where state = 'assigned';

create index task_attempts_disconnected_recovery_idx
    on task_attempts (disconnected_at, task_id)
    where state = 'disconnected';
