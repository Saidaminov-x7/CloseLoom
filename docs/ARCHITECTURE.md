# Architecture

Core owns process identities, the decision policy and per-instance grace-period state. Infrastructure supplies process and window adapters, a bounded event queue, periodic reconciliation and JSON persistence. Desktop composes these services using dependency injection. Only a selected executable's exact full path is considered; process identity includes PID and creation time. The monitor handles hook faults by cancelling pending work. Grace-period and monitoring tests use fake time and fake adapters.

Dry Run is forced by the monitoring service. The native termination adapter refuses requests. The pipeline has not been validated with real process or window events, and the delivery and ordering guarantees of Windows events do not by themselves prove uninterrupted observation. These components are not evidence of production safety.
