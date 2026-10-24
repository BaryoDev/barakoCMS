# BarakoCMS.Services

A registry of the outside services barakoCMS talks to, with heartbeats and health. This is the
first piece of the Services module (#833). Message state, replay and pause come after the outbox
(#831) and are not here yet.

## Install

```sh
dotnet add package BarakoCMS.Services
```

The package reference plus a restart is the install. `BarakoCMS:Modules:Enabled` decides which
referenced modules run (`BarakoCMS__Modules__Enabled=Services`). To name it by hand, put
`modules.Add(new BarakoCMS.Services.ServicesModule())` in the `AddBarakoCMS` callback.

## Endpoints

| Method | Route | Who | Purpose |
| --- | --- | --- | --- |
| POST | `/api/services` | `manage_services`, held globally | register a service; a name already taken is a 409 |
| GET | `/api/services` | `manage_services`, held globally | list services with their health, paged |
| GET | `/api/services/{name}` | `manage_services`, held globally | one service |
| POST | `/api/services/{name}/heartbeat` | `send_service_heartbeat` | the service reports it is alive |

A registration takes `name`, `messageTypes`, a `deliveryUrl` or a `topic`, an optional `healthUrl`,
an optional `machineKeyId` and an optional `heartbeatIntervalSeconds` (5 to 86400, default 60).
Names are stored lower case.

## Health

- Healthy: a heartbeat within the interval.
- Late: more than one interval of silence.
- Down: three intervals of silence or more.

A service that has not reported yet is measured from when it was registered.

Polling `healthUrl` is not done yet. The URL is stored.

## License

[MIT](LICENSE) © BaryoDev
